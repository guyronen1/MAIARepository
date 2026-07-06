using Maia.API.Contracts;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// Configuration CRUD with audit. Every successful POST/PUT/DELETE writes
/// an AuditLog row (EntityType + EntityId discriminator, EventType =
/// "{EntityType}{ActionVerb}" e.g. "FixPolicyUpdated"). Audit-write failures
/// log at Error level but never fail the request — the operator's config
/// change already succeeded by then; degraded audit beats a rolled-back UX.
///
/// The audit actor is the authenticated principal (currentUser.UserName), resolved
/// server-side — no client-supplied operatorId. Authorization (RequireOperator for
/// reads, RequireAdmin for writes) guarantees a known user reaches any action.
/// </summary>
[ApiController]
[Route("api/[controller]")]
// Class floor = Operator (config READS expose SQL payloads / connection names /
// SqlQuery text). Every write action additionally carries [Authorize(RequireAdmin)];
// ASP.NET AND-combines them, so writes require Admin while reads stay Operator.
[Authorize(Policy = "RequireOperator")]
public class ConfigController(
    IMonitoredJobRepository           jobRepo,
    IClassificationRuleRepository     ruleRepo,
    IAuditRepository                  audit,
    ILogger<ConfigController>         logger,
    IDbContextFactory<MaiaDbContext>    dbFactory,
    IEnumerable<IFileContentExtractor> extractors,
    ISqlFixScopeValidator             sqlFixScope,
    ICurrentUserAccessor              currentUser) : ControllerBase
{
    // Audit actor = the authenticated principal. Guaranteed present: every action is
    // gated by RequireOperator/RequireAdmin, so no anonymous request reaches a body.
    // The client-supplied operatorId is gone from the contract (server-authoritative).
    private string Actor => currentUser.UserName!;

    // FileContent extractors keyed by format — used to validate a rule's
    // locator syntax at save time (each extractor owns its locator grammar).
    private readonly Dictionary<FileFormat, IFileContentExtractor> _extractors =
        extractors.ToDictionary(e => e.Format);

    // ── Audit helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Write an AuditLog row. Never throws — failures are logged so they
    /// surface in ops monitoring but don't fail the request that already
    /// succeeded. If audit-write reliability becomes a concern, an outbox
    /// pattern fits cleanly on top of this.
    /// </summary>
    private async Task WriteAuditAsync(
        string entityType, string entityId, string eventType,
        string actor, string detail, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(new AuditLog
            {
                EntityType = entityType,
                EntityId   = entityId,
                EventType  = eventType,
                Actor      = actor,
                Detail     = detail,
                Timestamp  = DateTime.Now,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Audit write failed: EventType={EventType} EntityType={EntityType} EntityId={EntityId}",
                eventType, entityType, entityId);
        }
    }

    /// <summary>
    /// Build the Detail string for an Updated event: "Field: before → after"
    /// joined by ", ", with only the fields that actually changed included.
    /// Returns empty string when nothing changed — caller decides whether
    /// to skip the audit write or emit a "No changes" placeholder.
    /// </summary>
    private static string BuildDiff(params (string field, object? before, object? after)[] changes)
    {
        var changed = changes
            .Where(c => !Equals(c.before, c.after))
            .Select(c => $"{c.field}: {FormatValue(c.before)} → {FormatValue(c.after)}");
        return string.Join(", ", changed);
    }

    /// <summary>Render a value for the audit Detail string. Strings get single
    /// quotes, bools lowercase (matches JSON convention), null → "null".</summary>
    private static string FormatValue(object? v) => v switch
    {
        null     => "null",
        string s => $"'{s}'",
        bool b   => b ? "true" : "false",
        _        => v.ToString() ?? "null",
    };

    /// <summary>
    /// Validates the composite/step shape of a FixPolicyRule upsert request.
    /// Returns true (with a populated <paramref name="error"/>) when the
    /// request is invalid — caller short-circuits with that response. Returns
    /// false (with a populated <paramref name="normalisedSteps"/>) when the
    /// request is valid; <paramref name="normalisedSteps"/> is non-empty for
    /// composite and empty/null for single-action.
    ///
    /// The eight rules enforced (in order):
    ///   1. Composite must have at least one step
    ///   2. Composite must not have a header ActionPayload
    ///   3. Non-composite must not have steps
    ///   4. No nested composite (steps cannot themselves be Composite)
    ///   5. No Manual steps (composite is automated by definition)
    ///   6. Unknown step ActionType
    ///   7. Duplicate StepOrder
    ///   8. Empty step ActionPayload
    /// </summary>
    private bool ValidateCompositePayload(
        UpsertFixPolicyRuleRequest req,
        FixActionType              actionType,
        out IActionResult          error,
        out List<NormalisedStep>?  normalisedSteps)
    {
        var hasSteps = req.Steps is { Count: > 0 };

        if (actionType == FixActionType.Composite && !hasSteps)
        {
            error = BadRequest(new { error = "CompositeRequiresSteps", message = "Composite policies require at least one step." });
            normalisedSteps = null; return true;
        }

        if (actionType == FixActionType.Composite && !string.IsNullOrWhiteSpace(req.ActionPayload))
        {
            error = BadRequest(new { error = "CompositePayloadConflict", message = "Composite policies must not set ActionPayload; payload belongs on each step." });
            normalisedSteps = null; return true;
        }

        if (actionType != FixActionType.Composite && hasSteps)
        {
            error = BadRequest(new { error = "NonCompositeWithSteps", message = "Only Composite policies can have steps. Set ActionType=Composite." });
            normalisedSteps = null; return true;
        }

        if (!hasSteps)
        {
            // Single-action policy. The one content check: a SqlScript fix is a
            // WRITE against the source DB, so it must be scoped to the failing row
            // ({sourceId} in WHERE / EXEC param) — block bulk UPDATE/DELETE.
            if (actionType == FixActionType.SqlScript
                && !string.IsNullOrWhiteSpace(req.ActionPayload)
                && sqlFixScope.Validate(req.ActionPayload!) is { } reason)
            {
                error = BadRequest(new { error = "DbFixRequiresSourceIdInWhere", message = $"SqlScript fix {reason}" });
                normalisedSteps = null; return true;
            }
            error = null!; normalisedSteps = null; return false;
        }

        var seenOrders = new HashSet<int>();
        var parsed = new List<NormalisedStep>(req.Steps!.Count);
        foreach (var s in req.Steps!)
        {
            if (!Enum.TryParse<FixActionType>(s.ActionType, out var stepActionType))
            {
                error = BadRequest(new { error = "UnknownStepActionType", message = $"Step at order {s.StepOrder} has unsupported ActionType '{s.ActionType}'." });
                normalisedSteps = null; return true;
            }
            if (stepActionType == FixActionType.Composite)
            {
                error = BadRequest(new { error = "NestedCompositeForbidden", message = $"Step at order {s.StepOrder} cannot itself be Composite." });
                normalisedSteps = null; return true;
            }
            if (stepActionType == FixActionType.Manual)
            {
                error = BadRequest(new { error = "ManualStepForbidden", message = $"Step at order {s.StepOrder} cannot be Manual. A composite is by definition automated." });
                normalisedSteps = null; return true;
            }
            if (string.IsNullOrWhiteSpace(s.ActionPayload))
            {
                error = BadRequest(new { error = "StepPayloadRequired", message = $"Step at order {s.StepOrder} has no payload." });
                normalisedSteps = null; return true;
            }
            // Same WRITE guard as single-action, per SqlScript step.
            if (stepActionType == FixActionType.SqlScript
                && sqlFixScope.Validate(s.ActionPayload!) is { } stepReason)
            {
                error = BadRequest(new { error = "DbFixRequiresSourceIdInWhere", message = $"SqlScript step at order {s.StepOrder} {stepReason}" });
                normalisedSteps = null; return true;
            }
            if (!seenOrders.Add(s.StepOrder))
            {
                error = BadRequest(new { error = "DuplicateStepOrder", message = $"Step orders must be unique within a policy (duplicate: {s.StepOrder})." });
                normalisedSteps = null; return true;
            }
            parsed.Add(new NormalisedStep(s.StepOrder, stepActionType, s.ActionPayload, s.Description));
        }

        // Renormalise orders to 1..N regardless of input — spares the UI from
        // having to re-pack after deletes. Sort by input StepOrder ascending
        // to preserve the operator's intended sequence.
        var packed = parsed
            .OrderBy(p => p.StepOrder)
            .Select((p, i) => p with { StepOrder = i + 1 })
            .ToList();

        error = null!; normalisedSteps = packed; return false;
    }

    /// <summary>Compact one-line summary of a step list for audit diff text.
    /// Order matters for diff legibility: a reorder shows clearly here.</summary>
    private static string SummariseSteps(IEnumerable<FixPolicyRuleStep> steps)
    {
        var ordered = steps.OrderBy(s => s.StepOrder).ToList();
        return ordered.Count == 0
            ? "(none)"
            : string.Join(" → ", ordered.Select(s => $"{s.StepOrder}:{s.ActionType}"));
    }

    /// <summary>Internal validated-step shape — keeps ValidateCompositePayload's
    /// output strongly-typed (vs reusing the DTO which has a string ActionType).</summary>
    public sealed record NormalisedStep(
        int           StepOrder,
        FixActionType ActionType,
        string        ActionPayload,
        string?       Description);

    // ── Lookup data ──────────────────────────────────────────────────────────

    [HttpGet("job-types")]
    public async Task<IActionResult> GetJobTypes(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var types = await db.JobTypes.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync(ct);
        return Ok(types.Select(t => new { t.JobTypeId, t.Name, t.Description }));
    }

    [HttpGet("error-types")]
    public async Task<IActionResult> GetErrorTypes(
        [FromQuery] bool includeInactive = false,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = includeInactive ? db.ErrorTypes.AsQueryable() : db.ErrorTypes.Where(t => t.IsActive);
        var types = await q.OrderBy(t => t.Code).ToListAsync(ct);
        return Ok(types.Select(t => new
        {
            t.ErrorTypeId, t.Code, t.DisplayName, t.Description,
            Severity = t.Severity.ToString(),
            t.IsActive,
        }));
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("error-types")]
    public async Task<IActionResult> CreateErrorType([FromBody] UpsertErrorTypeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code))        return BadRequest(new { Message = "Code is required." });
        if (string.IsNullOrWhiteSpace(req.DisplayName)) return BadRequest(new { Message = "DisplayName is required." });
        if (!Enum.TryParse<Severity>(req.Severity, ignoreCase: true, out var severity))
            return BadRequest(new { Message = $"Unknown Severity '{req.Severity}'. Expected: Low, Medium, High, Critical." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ErrorTypes.AnyAsync(t => t.Code == req.Code, ct))
            return Conflict(new { Message = $"ErrorType with Code '{req.Code}' already exists." });

        var et = new ErrorType
        {
            Code        = req.Code,
            DisplayName = req.DisplayName,
            Description = req.Description,
            Severity    = severity,
            IsActive    = req.IsActive,
        };
        db.ErrorTypes.Add(et);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ErrorType",
            entityId:   et.ErrorTypeId.ToString(),
            eventType:  "ErrorTypeCreated",
            actor:      Actor,
            detail:     $"Created ErrorType '{et.Code}' (DisplayName='{et.DisplayName}', Severity={et.Severity}, IsActive={FormatValue(et.IsActive)})",
            ct: ct);

        return Ok(new { et.ErrorTypeId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("error-types/{id:int}")]
    public async Task<IActionResult> UpdateErrorType(int id, [FromBody] UpsertErrorTypeRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<Severity>(req.Severity, ignoreCase: true, out var severity))
            return BadRequest(new { Message = $"Unknown Severity '{req.Severity}'. Expected: Low, Medium, High, Critical." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var et = await db.ErrorTypes.FindAsync([id], ct);
        if (et is null) return NotFound();

        // Code is the natural key — block accidental collisions on rename
        if (!string.Equals(et.Code, req.Code, StringComparison.Ordinal)
            && await db.ErrorTypes.AnyAsync(t => t.Code == req.Code && t.ErrorTypeId != id, ct))
            return Conflict(new { Message = $"ErrorType with Code '{req.Code}' already exists." });

        // Snapshot original values BEFORE mutation so the audit diff sees
        // the actual before/after — EF Core's tracked entity is about to
        // be overwritten in place.
        var beforeCode        = et.Code;
        var beforeDisplayName = et.DisplayName;
        var beforeDescription = et.Description;
        var beforeSeverity    = et.Severity;
        var beforeIsActive    = et.IsActive;

        et.Code        = req.Code;
        et.DisplayName = req.DisplayName;
        et.Description = req.Description;
        et.Severity    = severity;
        et.IsActive    = req.IsActive;
        await db.SaveChangesAsync(ct);

        var diff = BuildDiff(
            ("Code",        beforeCode,        et.Code),
            ("DisplayName", beforeDisplayName, et.DisplayName),
            ("Description", beforeDescription, et.Description),
            ("Severity",    beforeSeverity,    et.Severity),
            ("IsActive",    beforeIsActive,    et.IsActive));
        await WriteAuditAsync(
            entityType: "ErrorType",
            entityId:   id.ToString(),
            eventType:  "ErrorTypeUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("error-types/{id:int}")]
    public async Task<IActionResult> DeleteErrorType(
        int id, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var et = await db.ErrorTypes.FindAsync([id], ct);
        if (et is null) return NotFound();

        // Soft delete — referenced from JobFailures / ClassificationRules / FixPolicyRules / AIRecommendations
        // with RESTRICT FKs. A hard DELETE would fail; flipping IsActive is the right primitive.
        et.IsActive = false;
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ErrorType",
            entityId:   id.ToString(),
            eventType:  "ErrorTypeDeleted",
            actor:      Actor,
            detail:     $"Soft-deleted ErrorType {id} ('{et.Code}', DisplayName='{et.DisplayName}')",
            ct: ct);

        return NoContent();
    }

    // ── Monitored Jobs ───────────────────────────────────────────────────────

    [HttpGet("monitored-jobs")]
    public async Task<IActionResult> GetAllJobs(CancellationToken ct)
    {
        var jobs = await jobRepo.GetAllWithRulesAsync(ct);
        return Ok(jobs.Select(MonitoredJobDto.From));
    }

    /// <summary>Full operational picture of one job for the dedicated config screen
    /// (Tier 2.5 d2): active sources with their active rules, classification rules,
    /// fix policies. One round-trip. (Lease state isn't included — the config screen
    /// doesn't render it; the dashboard polls worker-status for that.)</summary>
    [HttpGet("monitored-jobs/{id:int}")]
    public async Task<IActionResult> GetJob(int id, CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null) return NotFound();
        return Ok(MonitoredJobDto.From(job));
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs")]
    public async Task<IActionResult> CreateJob([FromBody] UpsertMonitoredJobRequest req, CancellationToken ct)
    {

        var job = new MonitoredJob
        {
            Name                   = req.Name,
            DisplayName            = req.DisplayName,
            JobTypeId              = req.JobTypeId,
            PollingIntervalSeconds = req.PollingIntervalSeconds,
            IsActive               = req.IsActive,
            Description            = req.Description,
            CreatedAt              = DateTime.Now,
        };
        var saved = await jobRepo.SaveAsync(job, ct);

        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   saved.MonitoredJobId.ToString(),
            eventType:  "MonitoredJobCreated",
            actor:      Actor,
            detail:     $"Created MonitoredJob '{saved.Name}' (JobTypeId={saved.JobTypeId}, PollingIntervalSeconds={saved.PollingIntervalSeconds}, IsActive={FormatValue(saved.IsActive)})",
            ct: ct);

        return Ok(new { saved.MonitoredJobId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("monitored-jobs/{id:int}")]
    public async Task<IActionResult> UpdateJob(int id, [FromBody] UpsertMonitoredJobRequest req, CancellationToken ct)
    {

        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null) return NotFound();

        // Snapshot before mutation for the audit diff. Tier 2.5 Option 1: a job is pure
        // identity — scan config (ScanType/folder/pattern/connection/url) lives on its
        // ScanSources and is NOT edited here. Those columns are left untouched (preserved
        // at their backfilled values until the cleanup migration drops them).
        var beforeName           = job.Name;
        var beforeDisplayName    = job.DisplayName;
        var beforeJobTypeId      = job.JobTypeId;
        var beforePollingInterval= job.PollingIntervalSeconds;
        var beforeIsActive       = job.IsActive;
        var beforeDescription    = job.Description;

        job.Name                   = req.Name;
        job.DisplayName            = req.DisplayName;
        job.JobTypeId              = req.JobTypeId;
        job.PollingIntervalSeconds = req.PollingIntervalSeconds;
        job.IsActive               = req.IsActive;
        job.Description            = req.Description;

        await jobRepo.UpdateAsync(job, ct);

        var diff = BuildDiff(
            ("Name",                   beforeName,            job.Name),
            ("DisplayName",            beforeDisplayName,     job.DisplayName),
            ("JobTypeId",              beforeJobTypeId,       job.JobTypeId),
            ("PollingIntervalSeconds", beforePollingInterval, job.PollingIntervalSeconds),
            ("IsActive",               beforeIsActive,        job.IsActive),
            ("Description",            beforeDescription,     job.Description));
        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   id.ToString(),
            eventType:  "MonitoredJobUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("monitored-jobs/{id:int}")]
    public async Task<IActionResult> DeleteJob(
        int id, CancellationToken ct)
    {

        // Snapshot name for the audit row before the entity is gone.
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null) return NotFound();
        var jobName = job.Name;

        await jobRepo.DeleteAsync(id, ct);

        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   id.ToString(),
            eventType:  "MonitoredJobDeleted",
            actor:      Actor,
            detail:     $"Deleted MonitoredJob {id} ('{jobName}')",
            ct: ct);

        return NoContent();
    }

    // ── Scan Check Rules ─────────────────────────────────────────────────────

    /// <summary>
    /// Maps the FileContent-only fields onto the rule and validates them.
    /// Scoped to CheckType=FileContent — for every other CheckType the five
    /// fields are nulled (they're meaningless) and no validation runs, so
    /// existing FS/DB/API rule types are untouched. Returns a 400 IActionResult
    /// on inconsistent config, else null.
    /// </summary>
    private IActionResult? ApplyAndValidateFileContent(UpsertScanCheckRuleRequest req, ScanCheckRule rule, CheckType checkType)
    {
        if (checkType != CheckType.FileContent)
        {
            rule.ExtractorType           = null;
            rule.ExtractorLocator        = null;
            rule.IdentifierLocator       = null;
            rule.ExtractorPredicateType  = null;
            rule.ExtractorPredicateValue = null;
            return null;
        }

        if (string.IsNullOrWhiteSpace(req.ExtractorType) ||
            !Enum.TryParse<FileFormat>(req.ExtractorType, ignoreCase: true, out var fmt))
            return BadRequest(new { error = "ExtractorTypeRequired",
                message = "FileContent rules require a valid ExtractorType (e.g. 'Xml')." });

        var hasPredType = !string.IsNullOrWhiteSpace(req.ExtractorPredicateType);
        var hasPredVal  = !string.IsNullOrWhiteSpace(req.ExtractorPredicateValue);
        if (hasPredType != hasPredVal)
            return BadRequest(new { error = "PredicateIncomplete",
                message = "Set both ExtractorPredicateType and ExtractorPredicateValue, or neither." });

        ScanPredicateType? predType = null;
        if (hasPredType)
        {
            if (!Enum.TryParse<ScanPredicateType>(req.ExtractorPredicateType, ignoreCase: true, out var pt))
                return BadRequest(new { error = "PredicateIncomplete",
                    message = $"Unknown ExtractorPredicateType '{req.ExtractorPredicateType}'. Use Equals, NotEquals, Contains, or NotContains." });
            predType = pt;

            if (string.IsNullOrWhiteSpace(req.ExtractorLocator))
                return BadRequest(new { error = "PredicateRequiresLocator",
                    message = "A predicate needs an ExtractorLocator to extract the value it tests." });
        }

        // Locator syntax check — the chosen extractor validates its own grammar
        // (XPath for XML). Rejects a malformed locator (e.g. `\\` instead of `//`)
        // at save instead of letting it fail silently at scan time.
        if (_extractors.TryGetValue(fmt, out var extractor))
        {
            foreach (var (field, loc) in new[]
                     {
                         ("ExtractorLocator",  req.ExtractorLocator),
                         ("IdentifierLocator", req.IdentifierLocator),
                     })
            {
                if (!string.IsNullOrWhiteSpace(loc) && extractor.ValidateLocator(loc) is { } reason)
                    return BadRequest(new { error = "InvalidLocator",
                        message = $"{field} is {reason}" });
            }
        }

        rule.ExtractorType           = fmt;
        rule.ExtractorLocator        = req.ExtractorLocator;
        rule.IdentifierLocator       = req.IdentifierLocator;
        rule.ExtractorPredicateType  = predType;
        rule.ExtractorPredicateValue = hasPredVal ? req.ExtractorPredicateValue : null;
        return null;
    }

    /// <summary>
    /// SqlQuery-scoped validation (CheckType.SqlQuery), mirroring the FileContent
    /// validator. Cheap + save-time — no SQL parsing/execution (same trust model as
    /// the existing SourceTable: a wrong query fails clearly at scan time). For
    /// CheckType.SqlQuery, SourceTable holds the operator-written query / "EXEC
    /// sp_Name …". Returns a 400 on inconsistent config, else null.
    /// </summary>
    private IActionResult? ApplyAndValidateSqlQuery(UpsertScanCheckRuleRequest req, ScanCheckRule rule, CheckType checkType)
    {
        if (checkType != CheckType.SqlQuery) return null;

        if (string.IsNullOrWhiteSpace(rule.SourceTable))
            return BadRequest(new { error = "SourceQueryRequired",
                message = "SqlQuery rules require a Source Query — a SELECT statement or 'EXEC sp_Name @p=…'." });

        if (string.IsNullOrWhiteSpace(rule.TargetField))
            return BadRequest(new { error = "TargetFieldRequired",
                message = "SqlQuery rules require a TargetField — the result-set column whose value is shown on each failure." });

        // Option A: every returned row is a failure (the operator's WHERE is the
        // filter). The range/equality predicate and file-path fields don't apply —
        // null them so stale UI values can't leak onto a SqlQuery rule.
        //
        // WatermarkColumn and SourceIdColumn ARE kept: SqlQuery now does incremental
        // watermarking + per-SourceId dedup in-memory (parity with ValueEquals). Both
        // are optional and name result-set columns; we can't validate their presence
        // here (the result shape is only known at scan time — a missing column then
        // fails the scan with a clear message).
        rule.MinValue         = null;
        rule.MaxValue         = null;
        rule.ExpectedValue    = null;
        rule.FilePathColumn   = null;
        rule.InputPathPattern = null;
        return null;
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs/{jobId:int}/scan-rules")]
    public async Task<IActionResult> CreateScanRule(int jobId, [FromBody] UpsertScanCheckRuleRequest req, CancellationToken ct)
    {

        var checkType = Enum.Parse<CheckType>(req.CheckType);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Transitional (Tier 2.5): a rule must belong to a ScanSource, or the worker
        // (which scans per source) never loads it. The current UI still posts here
        // job-scoped, so attach to the job's single active source. With 0 or >1
        // sources it's ambiguous → 400 directing to the per-source endpoint. Removed
        // once the new config UI (phase d2) posts to /scan-sources/{id}/scan-rules.
        var activeSourceIds = await db.ScanSources
            .Where(s => s.MonitoredJobId == jobId && s.IsActive)
            .Select(s => s.ScanSourceId)
            .ToListAsync(ct);
        if (activeSourceIds.Count != 1)
            return BadRequest(new { error = "AmbiguousSourceForRule",
                message = activeSourceIds.Count == 0
                    ? "This job has no active scan source. Add a source first, then add rules to it."
                    : "This job has multiple sources. Add the rule to a specific source via /config/scan-sources/{id}/scan-rules." });

        var rule = new ScanCheckRule
        {
            MonitoredJobId   = jobId,
            ScanSourceId     = activeSourceIds[0],
            CheckType        = checkType,
            SourceTable      = req.SourceTable,
            TargetField      = req.TargetField,
            MinValue         = req.MinValue,
            MaxValue         = req.MaxValue,
            ExpectedValue    = req.ExpectedValue,
            WatermarkColumn  = req.WatermarkColumn,
            SourceIdColumn   = req.SourceIdColumn,
            ReferenceIdColumn = req.ReferenceIdColumn,
            FilePathColumn   = req.FilePathColumn,
            InputPathPattern = req.InputPathPattern,
            Severity         = Enum.Parse<Severity>(req.Severity),
            Description      = req.Description,
            IsActive         = true,
        };
        if (ApplyAndValidateFileContent(req, rule, checkType) is { } fcError) return fcError;
        if (ApplyAndValidateSqlQuery(req, rule, checkType) is { } sqError) return sqError;

        db.ScanCheckRules.Add(rule);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ScanCheckRule",
            entityId:   rule.CheckRuleId.ToString(),
            eventType:  "ScanRuleCreated",
            actor:      Actor,
            detail:     $"Created ScanCheckRule for MonitoredJob {jobId} (CheckType={rule.CheckType}, TargetField='{rule.TargetField}', Severity={rule.Severity})",
            ct: ct);

        return Ok(new { rule.CheckRuleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("scan-rules/{id:int}")]
    public async Task<IActionResult> UpdateScanRule(int id, [FromBody] UpsertScanCheckRuleRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ScanCheckRules.FindAsync([id], ct);
        if (rule is null) return NotFound();

        var beforeCheckType        = rule.CheckType;
        var beforeSourceTable      = rule.SourceTable;
        var beforeTargetField      = rule.TargetField;
        var beforeMinValue         = rule.MinValue;
        var beforeMaxValue         = rule.MaxValue;
        var beforeExpected         = rule.ExpectedValue;
        var beforeWatermark        = rule.WatermarkColumn;
        var beforeSourceId         = rule.SourceIdColumn;
        var beforeReferenceId      = rule.ReferenceIdColumn;
        var beforeFilePathColumn   = rule.FilePathColumn;
        var beforeInputPathPattern = rule.InputPathPattern;
        var beforeExtractorType    = rule.ExtractorType;
        var beforeExtractorLocator = rule.ExtractorLocator;
        var beforeIdentifierLocator= rule.IdentifierLocator;
        var beforePredicateType    = rule.ExtractorPredicateType;
        var beforePredicateValue   = rule.ExtractorPredicateValue;
        var beforeSeverity         = rule.Severity;
        var beforeDescription      = rule.Description;
        var beforeIsActive         = rule.IsActive;

        var checkType = Enum.Parse<CheckType>(req.CheckType);
        rule.CheckType        = checkType;
        rule.SourceTable      = req.SourceTable;
        rule.TargetField      = req.TargetField;
        rule.MinValue         = req.MinValue;
        rule.MaxValue         = req.MaxValue;
        rule.ExpectedValue    = req.ExpectedValue;
        rule.WatermarkColumn  = req.WatermarkColumn;
        rule.SourceIdColumn   = req.SourceIdColumn;
        rule.ReferenceIdColumn = req.ReferenceIdColumn;
        rule.FilePathColumn   = req.FilePathColumn;
        rule.InputPathPattern = req.InputPathPattern;
        rule.Severity         = Enum.Parse<Severity>(req.Severity);
        rule.Description      = req.Description;
        rule.IsActive         = req.IsActive;
        if (ApplyAndValidateFileContent(req, rule, checkType) is { } fcError) return fcError;
        if (ApplyAndValidateSqlQuery(req, rule, checkType) is { } sqError) return sqError;

        await db.SaveChangesAsync(ct);

        var diff = BuildDiff(
            ("CheckType",        beforeCheckType,        rule.CheckType),
            ("SourceTable",      beforeSourceTable,      rule.SourceTable),
            ("TargetField",      beforeTargetField,      rule.TargetField),
            ("MinValue",         beforeMinValue,         rule.MinValue),
            ("MaxValue",         beforeMaxValue,         rule.MaxValue),
            ("ExpectedValue",    beforeExpected,         rule.ExpectedValue),
            ("WatermarkColumn",  beforeWatermark,        rule.WatermarkColumn),
            ("SourceIdColumn",   beforeSourceId,         rule.SourceIdColumn),
            ("ReferenceIdColumn", beforeReferenceId,     rule.ReferenceIdColumn),
            ("FilePathColumn",   beforeFilePathColumn,   rule.FilePathColumn),
            ("InputPathPattern", beforeInputPathPattern, rule.InputPathPattern),
            ("ExtractorType",    beforeExtractorType,    rule.ExtractorType),
            ("ExtractorLocator", beforeExtractorLocator, rule.ExtractorLocator),
            ("IdentifierLocator",beforeIdentifierLocator,rule.IdentifierLocator),
            ("ExtractorPredicateType",  beforePredicateType,  rule.ExtractorPredicateType),
            ("ExtractorPredicateValue", beforePredicateValue, rule.ExtractorPredicateValue),
            ("Severity",         beforeSeverity,         rule.Severity),
            ("Description",      beforeDescription,      rule.Description),
            ("IsActive",         beforeIsActive,         rule.IsActive));
        await WriteAuditAsync(
            entityType: "ScanCheckRule",
            entityId:   id.ToString(),
            eventType:  "ScanRuleUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("scan-rules/{id:int}")]
    public async Task<IActionResult> DeleteScanRule(
        int id, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ScanCheckRules.FindAsync([id], ct);
        if (rule is null) return NotFound();
        var ruleJobId       = rule.MonitoredJobId;
        var ruleCheckType   = rule.CheckType;
        var ruleTargetField = rule.TargetField;
        rule.IsActive = false;
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ScanCheckRule",
            entityId:   id.ToString(),
            eventType:  "ScanRuleDeleted",
            actor:      Actor,
            detail:     $"Soft-deleted ScanCheckRule {id} (MonitoredJob {ruleJobId}, {ruleCheckType} on '{ruleTargetField}')",
            ct: ct);

        return NoContent();
    }

    // ── Scan Sources (Tier 2.5) ─────────────────────────────────────────────

    /// <summary>
    /// Validates a source's config against its ScanType + cross-source constraints.
    /// Returns a 400 IActionResult on the first violation, else null.
    ///   • LogFolderRequired / ConnectionNameRequired / LogSourceUrlRequired — the
    ///     config field the source's type needs.
    ///   • IncludeSubfoldersInvalidForType — recursion only applies to file types.
    ///   • SourceNameRequired / SourceNameDuplicate — name present + unique among the
    ///     job's ACTIVE sources (case-insensitive via the DB's CI collation).
    ///   • UnknownScanType — ScanTypeId not in ScanTypes.
    ///   • SourceFolderConflict — two ACTIVE FS/FileContent sources of one job may not
    ///     share a LogFolder: watermarks are keyed (MonitoredJobId, FilePath), NOT
    ///     (ScanSourceId, FilePath), so they'd fight over the same watermark rows
    ///     (silent data loss). Guard lifts when watermarks are re-keyed to the source.
    /// </summary>
    private async Task<IActionResult?> ValidateScanSourceAsync(
        MaiaDbContext db, int jobId, UpsertScanSourceRequest req, int? existingSourceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "SourceNameRequired", message = "Source Name is required." });

        var st = await db.ScanTypes.FirstOrDefaultAsync(s => s.ScanTypeId == req.ScanTypeId, ct);
        if (st is null)
            return BadRequest(new { error = "UnknownScanType", message = $"ScanTypeId {req.ScanTypeId} not found." });
        var scanType   = Enum.Parse<ScanType>(st.Name);
        var isFileBased = scanType is ScanType.FileSystem or ScanType.FileContent;

        if (isFileBased && string.IsNullOrWhiteSpace(req.LogFolder))
            return BadRequest(new { error = "LogFolderRequired", message = $"{scanType} sources require a Log Folder." });
        if (scanType == ScanType.Database && string.IsNullOrWhiteSpace(req.ConnectionName))
            return BadRequest(new { error = "ConnectionNameRequired", message = "Database sources require a Connection Name." });
        if (scanType == ScanType.ApiEndpoint && string.IsNullOrWhiteSpace(req.LogSourceUrl))
            return BadRequest(new { error = "LogSourceUrlRequired", message = "ApiEndpoint sources require a URL." });
        if (req.IncludeSubfolders && !isFileBased)
            return BadRequest(new { error = "IncludeSubfoldersInvalidForType", message = "Include Subfolders applies only to FileSystem / FileContent sources." });

        var selfId = existingSourceId ?? 0;
        var name   = req.Name.Trim();
        if (await db.ScanSources.AnyAsync(s =>
                s.MonitoredJobId == jobId && s.IsActive && s.ScanSourceId != selfId && s.Name == name, ct))
            return BadRequest(new { error = "SourceNameDuplicate", message = $"This job already has an active source named '{name}'." });

        if (isFileBased && !string.IsNullOrWhiteSpace(req.LogFolder))
        {
            var folder = req.LogFolder.Trim().ToLower();
            var fileBasedTypeIds = await db.ScanTypes
                .Where(t => t.Name == "FileSystem" || t.Name == "FileContent")
                .Select(t => t.ScanTypeId).ToListAsync(ct);
            var conflict = await db.ScanSources.AnyAsync(s =>
                s.MonitoredJobId == jobId && s.IsActive && s.ScanSourceId != selfId
                && fileBasedTypeIds.Contains(s.ScanTypeId)
                && s.LogFolder != null && s.LogFolder.ToLower() == folder, ct);
            if (conflict)
                return BadRequest(new { error = "SourceFolderConflict",
                    message = "Cannot create a second source with the same LogFolder. Add additional rules to the existing source instead, or use a different folder." });
        }

        return null;
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs/{jobId:int}/scan-sources")]
    public async Task<IActionResult> CreateScanSource(int jobId, [FromBody] UpsertScanSourceRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.MonitoredJobs.FindAsync([jobId], ct) is null) return NotFound();
        if (await ValidateScanSourceAsync(db, jobId, req, existingSourceId: null, ct) is { } err) return err;

        var source = new ScanSource
        {
            MonitoredJobId    = jobId,
            Name              = req.Name.Trim(),
            ScanTypeId        = req.ScanTypeId,
            LogFolder         = req.LogFolder,
            SearchPatterns    = req.SearchPatterns,
            InputFolder       = req.InputFolder,
            IncludeSubfolders = req.IncludeSubfolders,
            ConnectionName    = req.ConnectionName,
            LogSourceUrl      = req.LogSourceUrl,
            IsActive          = true,
        };
        db.ScanSources.Add(source);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync("ScanSource", source.ScanSourceId.ToString(), "ScanSourceCreated", Actor,
            $"Created ScanSource '{source.Name}' for MonitoredJob {jobId} (ScanTypeId={source.ScanTypeId})", ct);
        return Ok(new { source.ScanSourceId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("scan-sources/{id:int}")]
    public async Task<IActionResult> UpdateScanSource(int id, [FromBody] UpsertScanSourceRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.ScanSources.FindAsync([id], ct);
        if (source is null) return NotFound();

        if (req.ScanTypeId != source.ScanTypeId)
            return BadRequest(new { error = "ScanTypeImmutable",
                message = "A source's ScanType cannot be changed. Delete the source and create a new one." });

        if (await ValidateScanSourceAsync(db, source.MonitoredJobId, req, existingSourceId: id, ct) is { } err) return err;

        var beforeName     = source.Name;
        var beforeFolder   = source.LogFolder;
        var beforePatterns = source.SearchPatterns;
        var beforeInput    = source.InputFolder;
        var beforeRecurse  = source.IncludeSubfolders;
        var beforeConn     = source.ConnectionName;
        var beforeUrl      = source.LogSourceUrl;
        var beforeActive   = source.IsActive;

        source.Name              = req.Name.Trim();
        source.LogFolder         = req.LogFolder;
        source.SearchPatterns    = req.SearchPatterns;
        source.InputFolder       = req.InputFolder;
        source.IncludeSubfolders = req.IncludeSubfolders;
        source.ConnectionName    = req.ConnectionName;
        source.LogSourceUrl      = req.LogSourceUrl;
        source.IsActive          = req.IsActive;
        await db.SaveChangesAsync(ct);

        var diff = BuildDiff(
            ("Name",              beforeName,     source.Name),
            ("LogFolder",         beforeFolder,   source.LogFolder),
            ("SearchPatterns",    beforePatterns, source.SearchPatterns),
            ("InputFolder",       beforeInput,    source.InputFolder),
            ("IncludeSubfolders", beforeRecurse,  source.IncludeSubfolders),
            ("ConnectionName",    beforeConn,     source.ConnectionName),
            ("LogSourceUrl",      beforeUrl,      source.LogSourceUrl),
            ("IsActive",          beforeActive,   source.IsActive));
        await WriteAuditAsync("ScanSource", id.ToString(), "ScanSourceUpdated", Actor,
            diff.Length > 0 ? diff : "No changes", ct);
        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("scan-sources/{id:int}")]
    public async Task<IActionResult> DeleteScanSource(int id, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.ScanSources
            .Include(s => s.ScanCheckRules)
            .FirstOrDefaultAsync(s => s.ScanSourceId == id, ct);
        if (source is null) return NotFound();

        var name = source.Name; var typeId = source.ScanTypeId;
        // Soft-delete (matches the codebase pattern; the NoAction FKs block a hard
        // cascade and JobFailures reference ScanSourceId). Cascade soft-delete to the
        // source's active rules so none linger as "active under an inactive source".
        // Watermarks left dormant (the inactive source won't scan); JobFailures keep
        // their ScanSourceId for drill-down history.
        source.IsActive = false;
        var deactivated = 0;
        foreach (var r in source.ScanCheckRules.Where(r => r.IsActive)) { r.IsActive = false; deactivated++; }
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync("ScanSource", id.ToString(), "ScanSourceDeleted", Actor,
            $"Soft-deleted ScanSource {id} ('{name}', ScanTypeId={typeId}); deactivated {deactivated} rule(s)", ct);
        return NoContent();
    }

    /// <summary>Source-scoped scan-rule create (Tier 2.5). The rule's ScanSourceId is
    /// the source; MonitoredJobId is derived from the source's job (kept populated for
    /// the migration era). This is the canonical add-rule path now that the worker
    /// scans per source.</summary>
    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("scan-sources/{sourceId:int}/scan-rules")]
    public async Task<IActionResult> CreateScanRuleForSource(int sourceId, [FromBody] UpsertScanCheckRuleRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.ScanSources.FindAsync([sourceId], ct);
        if (source is null) return NotFound();

        var checkType = Enum.Parse<CheckType>(req.CheckType);
        var rule = new ScanCheckRule
        {
            MonitoredJobId   = source.MonitoredJobId,
            ScanSourceId     = sourceId,
            CheckType        = checkType,
            SourceTable      = req.SourceTable,
            TargetField      = req.TargetField,
            MinValue         = req.MinValue,
            MaxValue         = req.MaxValue,
            ExpectedValue    = req.ExpectedValue,
            WatermarkColumn   = req.WatermarkColumn,
            SourceIdColumn    = req.SourceIdColumn,
            ReferenceIdColumn = req.ReferenceIdColumn,
            FilePathColumn    = req.FilePathColumn,
            InputPathPattern  = req.InputPathPattern,
            Severity          = Enum.Parse<Severity>(req.Severity),
            Description       = req.Description,
            IsActive          = true,
        };
        if (ApplyAndValidateFileContent(req, rule, checkType) is { } fcError) return fcError;
        if (ApplyAndValidateSqlQuery(req, rule, checkType) is { } sqError) return sqError;

        db.ScanCheckRules.Add(rule);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync("ScanCheckRule", rule.CheckRuleId.ToString(), "ScanRuleCreated", Actor,
            $"Created ScanCheckRule for ScanSource {sourceId} (CheckType={rule.CheckType}, TargetField='{rule.TargetField}', Severity={rule.Severity})", ct);
        return Ok(new { rule.CheckRuleId });
    }

    // ── Per-job Classification Rules ────────────────────────────────────────

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs/{jobId:int}/classification-rules")]
    public async Task<IActionResult> CreateJobClassificationRule(
        int jobId, [FromBody] UpsertJobClassificationRuleRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.MonitoredJobs.FindAsync([jobId], ct);
        if (job is null) return NotFound();

        // Same duplicate guard as CreateClassificationRule — UX_ClassificationRules_ActiveKey
        // is on (JobTypeId, Pattern) WHERE IsActive=1, so an existing global rule for this
        // job's JobType will conflict. Return 409 with the conflicting rule id so the UI
        // can offer "Link the existing rule" instead of silently crashing.
        var dupId = await FindActiveClassificationDuplicateAsync(db, job.JobTypeId, req.Pattern, null, ct);
        if (dupId is not null)
            return Conflict(new
            {
                error             = "DuplicateClassificationRule",
                message           = $"An enabled classification rule with this pattern already exists for this job type (rule {dupId}). Link the existing rule instead of creating a duplicate.",
                conflictingRuleId = dupId,
            });

        var rule = new ClassificationRule
        {
            JobTypeId   = job.JobTypeId,
            ErrorTypeId = req.ErrorTypeId,
            Pattern     = req.Pattern,
            Confidence  = req.Confidence,
            Priority    = req.Priority,
            IsActive    = req.IsActive,
            CreatedBy   = Actor,
        };
        db.ClassificationRules.Add(rule);
        await db.SaveChangesAsync(ct);

        db.MonitoredJobRules.Add(new MonitoredJobRule
        {
            MonitoredJobId = jobId,
            RuleId         = rule.RuleId,
            IsActive       = true,
        });
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   rule.RuleId.ToString(),
            eventType:  "ClassificationRuleCreated",
            actor:      Actor,
            detail:     $"Created ClassificationRule for MonitoredJob {jobId} (ErrorTypeId={req.ErrorTypeId}, Pattern='{rule.Pattern}', Confidence={rule.Confidence}, Priority={rule.Priority}) and linked it",
            ct: ct);

        return Ok(new { rule.RuleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs/{jobId:int}/classification-rules/{ruleId:int}/link")]
    public async Task<IActionResult> LinkJobClassificationRule(
        int jobId, int ruleId, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.MonitoredJobs.FindAsync([jobId], ct) is null) return NotFound("Job not found");
        if (await db.ClassificationRules.FindAsync([ruleId], ct) is null) return NotFound("Rule not found");
        var exists = await db.MonitoredJobRules
            .AnyAsync(r => r.MonitoredJobId == jobId && r.RuleId == ruleId, ct);
        if (exists) return Conflict("Rule already linked to this job");
        db.MonitoredJobRules.Add(new MonitoredJobRule { MonitoredJobId = jobId, RuleId = ruleId, IsActive = true });
        await db.SaveChangesAsync(ct);

        // Linking is a relationship change, not an entity edit; audit it
        // against the MonitoredJob so an auditor scanning a job's history
        // sees both job-level and link-level events together.
        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   jobId.ToString(),
            eventType:  "ClassificationRuleLinked",
            actor:      Actor,
            detail:     $"Linked ClassificationRule {ruleId} to MonitoredJob {jobId}",
            ct: ct);

        return Ok(new { ruleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("monitored-jobs/{jobId:int}/classification-rules/{ruleId:int}")]
    public async Task<IActionResult> DeleteJobClassificationRule(
        int jobId, int ruleId, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var link = await db.MonitoredJobRules
            .FirstOrDefaultAsync(r => r.MonitoredJobId == jobId && r.RuleId == ruleId, ct);
        if (link is null) return NotFound();
        db.MonitoredJobRules.Remove(link);
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   jobId.ToString(),
            eventType:  "ClassificationRuleUnlinked",
            actor:      Actor,
            detail:     $"Unlinked ClassificationRule {ruleId} from MonitoredJob {jobId}",
            ct: ct);

        return NoContent();
    }

    // ── Fix Policy Rules ─────────────────────────────────────────────────────

    [HttpGet("fix-policy-rules")]
    public async Task<IActionResult> GetFixPolicyRules(
        [FromQuery] int? jobTypeId,
        [FromQuery] int? monitoredJobId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Steps include is split so EF doesn't Cartesian-join ErrorType ×
        // Steps when both are pulled in the same query.
        var q = db.FixPolicyRules
            .Include(r => r.ErrorType)
            .Include(r => r.Steps.OrderBy(s => s.StepOrder))
            .AsSplitQuery()
            .Where(r => r.Enabled);

        // Filtering semantics:
        //   monitoredJobId set → returns defaults for the job's JobType
        //                        PLUS any override scoped to that job.
        //                        Lets the per-job Fix Options tab show the
        //                        full effective config for that job.
        //   jobTypeId only     → returns every enabled rule under the JobType
        //                        (defaults + all overrides for any of its jobs).
        //   neither            → returns every enabled rule.
        if (monitoredJobId.HasValue)
        {
            q = q.Where(r =>
                (r.MonitoredJobId == monitoredJobId.Value)
             || (r.MonitoredJobId == null && jobTypeId.HasValue && r.JobTypeId == jobTypeId.Value));
        }
        else if (jobTypeId.HasValue)
        {
            q = q.Where(r => r.JobTypeId == jobTypeId.Value);
        }

        var rules = await q.OrderBy(r => r.ErrorTypeId).ThenBy(r => r.MonitoredJobId).ToListAsync(ct);
        return Ok(rules.Select(r => new
        {
            r.RuleId, r.JobTypeId, r.ErrorTypeId, r.MonitoredJobId,
            ErrorTypeCode      = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.ActionToApply,
            FixCategory        = r.FixCategory.ToString(),
            ActionType         = r.ActionType.ToString(),
            r.ActionPayload, r.IsAutoHealEligible, r.Enabled,
            // Empty array (not null) for non-composite — cleaner on the wire.
            Steps              = r.Steps
                .OrderBy(s => s.StepOrder)
                .Select(s => new { s.StepId, s.StepOrder, ActionType = s.ActionType.ToString(), s.ActionPayload, s.Description })
                .ToList(),
        }));
    }

    [HttpGet("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> GetFixPolicyRule(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var r = await db.FixPolicyRules
            .Include(p => p.ErrorType)
            .Include(p => p.Steps.OrderBy(s => s.StepOrder))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.RuleId == id, ct);
        if (r is null) return NotFound();
        return Ok(new
        {
            r.RuleId, r.JobTypeId, r.ErrorTypeId, r.MonitoredJobId,
            ErrorTypeCode = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.ActionToApply,
            FixCategory   = r.FixCategory.ToString(),
            ActionType    = r.ActionType.ToString(),
            r.ActionPayload, r.IsAutoHealEligible, r.Enabled,
            Steps         = r.Steps
                .OrderBy(s => s.StepOrder)
                .Select(s => new { s.StepId, s.StepOrder, ActionType = s.ActionType.ToString(), s.ActionPayload, s.Description })
                .ToList(),
        });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("fix-policy-rules")]
    public async Task<IActionResult> CreateFixPolicyRule([FromBody] UpsertFixPolicyRuleRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!Enum.TryParse<FixCategory>(req.FixCategory, out var fixCategory))
            return BadRequest($"Unknown FixCategory: '{req.FixCategory}'");
        if (!Enum.TryParse<FixActionType>(req.ActionType, out var actionType))
            return BadRequest($"Unknown ActionType: '{req.ActionType}'");

        // Composite-shape validation. Same rules apply to Create and Update;
        // see ValidateCompositePayload for the eight individual checks.
        if (ValidateCompositePayload(req, actionType, out var validationError, out var validatedSteps))
            return validationError;

        // Two-pronged duplicate check — different layer, different key:
        //   • Override layer: at most one enabled rule per (MonitoredJobId, ErrorTypeId)
        //   • Default  layer: at most one enabled rule per (JobTypeId, ErrorTypeId) AND MonitoredJobId IS NULL
        // A default + an override for the same (JobType, ErrorType) are NOT
        // duplicates — they're complementary by design. The filtered unique
        // indexes on the DB enforce the same two-pronged constraint as the
        // last line of defence.
        if (req.Enabled)
        {
            var conflict = req.MonitoredJobId is int monId
                ? await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.MonitoredJobId == monId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.Enabled, ct)
                : await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.JobTypeId      == req.JobTypeId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.MonitoredJobId == null
                      && p.Enabled, ct);
            if (conflict is not null)
            {
                return Conflict(new
                {
                    error               = "DuplicateFixPolicy",
                    message             = req.MonitoredJobId.HasValue
                        ? "An active fix policy override already exists for this job + Error Type combination. Disable the existing override or edit it instead of creating a new one."
                        : "An active fix policy already exists for this Job Type + Error Type combination. Disable the existing policy or edit it instead of creating a new one.",
                    conflictingPolicyId = conflict.RuleId,
                });
            }
        }

        var rule = new FixPolicyRule
        {
            JobTypeId          = req.JobTypeId,
            ErrorTypeId        = req.ErrorTypeId,
            MonitoredJobId     = req.MonitoredJobId,
            ActionToApply      = req.ActionToApply,
            FixCategory        = fixCategory,
            ActionType         = actionType,
            // Composite header has no payload of its own (validated above).
            ActionPayload      = actionType == FixActionType.Composite ? null : req.ActionPayload,
            IsAutoHealEligible = req.IsAutoHealEligible,
            Enabled            = req.Enabled,
            CreatedBy          = Actor,
            ActionTimestamp    = DateTime.Now,
            // Provenance when created from an /unconfigured Case-B gap (else null).
            SuggestedBy         = req.SuggestedBy,
            SuggestedFromHash   = req.SuggestedFromHash,
            SuggestedConfidence = req.SuggestedConfidence,
        };
        db.FixPolicyRules.Add(rule);
        await db.SaveChangesAsync(ct);

        // Steps persistence — validatedSteps is the normalised list (orders
        // packed 1..N). Empty for non-composite policies.
        if (validatedSteps is { Count: > 0 })
        {
            foreach (var s in validatedSteps)
                db.FixPolicyRuleSteps.Add(new FixPolicyRuleStep
                {
                    RuleId        = rule.RuleId,
                    StepOrder     = s.StepOrder,
                    ActionType    = s.ActionType,
                    ActionPayload = s.ActionPayload,
                    Description   = s.Description,
                });
            await db.SaveChangesAsync(ct);
        }

        var stepSummary = validatedSteps is { Count: > 0 }
            ? $", {validatedSteps.Count} steps: {string.Join(", ", validatedSteps.Select(s => s.ActionType))}"
            : string.Empty;

        await WriteAuditAsync(
            entityType: "FixPolicyRule",
            entityId:   rule.RuleId.ToString(),
            eventType:  "FixPolicyCreated",
            actor:      Actor,
            detail:     $"Created FixPolicyRule (JobTypeId={rule.JobTypeId}, ErrorTypeId={rule.ErrorTypeId}, MonitoredJobId={FormatValue((object?)rule.MonitoredJobId)}, FixCategory={rule.FixCategory}, ActionType={rule.ActionType}, IsAutoHealEligible={FormatValue(rule.IsAutoHealEligible)}, Enabled={FormatValue(rule.Enabled)}{stepSummary})",
            ct: ct);

        return Ok(new { rule.RuleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> UpdateFixPolicyRule(int id, [FromBody] UpsertFixPolicyRuleRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.FixPolicyRules
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.RuleId == id, ct);
        if (rule is null) return NotFound();
        if (!Enum.TryParse<FixCategory>(req.FixCategory, out var fixCat))
            return BadRequest($"Unknown FixCategory: '{req.FixCategory}'");
        if (!Enum.TryParse<FixActionType>(req.ActionType, out var actType))
            return BadRequest($"Unknown ActionType: '{req.ActionType}'");

        // Composite-shape validation — same rules as Create.
        if (ValidateCompositePayload(req, actType, out var validationError, out var validatedSteps))
            return validationError;

        // Two-pronged duplicate guard, same as Create but excluding self.
        // The req.MonitoredJobId on the PUT determines which layer the
        // resulting row will live in — switching layers via edit is allowed,
        // but must not produce two enabled rows at the same key.
        if (req.Enabled)
        {
            var conflict = req.MonitoredJobId is int monId
                ? await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.RuleId         != id
                      && p.MonitoredJobId == monId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.Enabled, ct)
                : await db.FixPolicyRules.FirstOrDefaultAsync(
                    p => p.RuleId         != id
                      && p.JobTypeId      == req.JobTypeId
                      && p.ErrorTypeId    == req.ErrorTypeId
                      && p.MonitoredJobId == null
                      && p.Enabled, ct);
            if (conflict is not null)
            {
                return Conflict(new
                {
                    error               = "DuplicateFixPolicy",
                    message             = req.MonitoredJobId.HasValue
                        ? "An active fix policy override already exists for this job + Error Type combination. Disable the existing override or edit it instead of creating a new one."
                        : "An active fix policy already exists for this Job Type + Error Type combination. Disable the existing policy or edit it instead of creating a new one.",
                    conflictingPolicyId = conflict.RuleId,
                });
            }
        }

        // IsAutoHealEligible flips are the highest-stakes config change in
        // the whole system — an auto-heal toggle decides whether a future
        // recommendation runs without human review. Capture every field.
        // MonitoredJobId is captured too — flipping a default into an
        // override (or vice versa) is a scope change worth auditing.
        var beforeErrorTypeId    = rule.ErrorTypeId;
        var beforeMonitoredJobId = rule.MonitoredJobId;
        var beforeActionToApply  = rule.ActionToApply;
        var beforeFixCategory    = rule.FixCategory;
        var beforeActionType     = rule.ActionType;
        var beforeActionPayload  = rule.ActionPayload;
        var beforeIsAutoHeal     = rule.IsAutoHealEligible;
        var beforeEnabled        = rule.Enabled;

        // Snapshot prior step shape for the diff (operator-visible summary).
        var beforeStepShape = SummariseSteps(rule.Steps);

        rule.ErrorTypeId        = req.ErrorTypeId;
        rule.MonitoredJobId     = req.MonitoredJobId;
        rule.ActionToApply      = req.ActionToApply;
        rule.FixCategory        = fixCat;
        rule.ActionType         = actType;
        rule.ActionPayload      = actType == FixActionType.Composite ? null : req.ActionPayload;
        rule.IsAutoHealEligible = req.IsAutoHealEligible;
        rule.Enabled            = req.Enabled;

        // Steps update: replace-all. Diffing would be cleaner per-step but
        // steps are small, replace-all keeps the code simple and the cascade
        // FK handles orphan deletes for us. Validated steps already have
        // packed orders 1..N.
        db.FixPolicyRuleSteps.RemoveRange(rule.Steps);
        if (validatedSteps is { Count: > 0 })
        {
            foreach (var s in validatedSteps)
                db.FixPolicyRuleSteps.Add(new FixPolicyRuleStep
                {
                    RuleId        = rule.RuleId,
                    StepOrder     = s.StepOrder,
                    ActionType    = s.ActionType,
                    ActionPayload = s.ActionPayload,
                    Description   = s.Description,
                });
        }

        await db.SaveChangesAsync(ct);

        var afterStepShape = SummariseSteps(rule.Steps);
        var diff = BuildDiff(
            ("ErrorTypeId",        beforeErrorTypeId,    rule.ErrorTypeId),
            ("MonitoredJobId",     (object?)beforeMonitoredJobId, (object?)rule.MonitoredJobId),
            ("ActionToApply",      beforeActionToApply,  rule.ActionToApply),
            ("FixCategory",        beforeFixCategory,    rule.FixCategory),
            ("ActionType",         beforeActionType,     rule.ActionType),
            ("ActionPayload",      beforeActionPayload,  rule.ActionPayload),
            ("IsAutoHealEligible", beforeIsAutoHeal,     rule.IsAutoHealEligible),
            ("Enabled",            beforeEnabled,        rule.Enabled),
            ("Steps",              beforeStepShape,      afterStepShape));
        await WriteAuditAsync(
            entityType: "FixPolicyRule",
            entityId:   id.ToString(),
            eventType:  "FixPolicyUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> DeleteFixPolicyRule(
        int id, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.FixPolicyRules.FindAsync([id], ct);
        if (rule is null) return NotFound();
        var snapshotJobTypeId   = rule.JobTypeId;
        var snapshotErrorTypeId = rule.ErrorTypeId;
        var snapshotActionType  = rule.ActionType;
        rule.Enabled = false;
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            entityType: "FixPolicyRule",
            entityId:   id.ToString(),
            eventType:  "FixPolicyDeleted",
            actor:      Actor,
            detail:     $"Soft-deleted FixPolicyRule {id} (JobTypeId={snapshotJobTypeId}, ErrorTypeId={snapshotErrorTypeId}, ActionType={snapshotActionType})",
            ct: ct);

        return NoContent();
    }

    // ── Classification Rules ─────────────────────────────────────────────────

    [HttpGet("classification-rules")]
    public async Task<IActionResult> GetAllClassificationRules(CancellationToken ct)
    {
        var rules = await ruleRepo.GetAllAsync(ct);

        // Active job links per rule → drives the "Scope" column: no links =
        // JobType default (all jobs of the type); links = scoped to those jobs.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var links = await (from m in db.MonitoredJobRules
                           where m.IsActive
                           join j in db.MonitoredJobs on m.MonitoredJobId equals j.MonitoredJobId
                           select new { m.RuleId, j.Name }).ToListAsync(ct);
        var linkedJobsByRule = links
            .GroupBy(l => l.RuleId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n).ToList());

        return Ok(rules.Select(r => new
        {
            r.RuleId, r.JobTypeId,
            JobTypeName  = r.JobType?.Name ?? r.JobTypeId.ToString(),
            r.ErrorTypeId,
            ErrorTypeCode = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.Pattern, r.Confidence, r.Priority, r.IsActive, r.CreatedBy,
            LinkedJobNames = linkedJobsByRule.GetValueOrDefault(r.RuleId, new List<string>()),
        }));
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("classification-rules")]
    public async Task<IActionResult> CreateClassificationRule([FromBody] UpsertClassificationRuleRequest req, CancellationToken ct)
    {

        // Duplicate guard (backend layer): at most one ENABLED rule per
        // (JobTypeId, Pattern). Returns an actionable 409 so the UI can offer
        // "open existing" instead of the operator silently creating a copy
        // (which the /unconfigured retry-on-no-effect flow did 4× in practice).
        await using (var dupDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var dupId = await FindActiveClassificationDuplicateAsync(dupDb, req.JobTypeId, req.Pattern, null, ct);
            if (dupId is not null)
                return Conflict(new
                {
                    error = "DuplicateClassificationRule",
                    message = $"An enabled classification rule with this pattern already exists for this job type (rule {dupId}).",
                    conflictingRuleId = dupId,
                });
        }

        var rule = new ClassificationRule
        {
            JobTypeId   = req.JobTypeId,
            ErrorTypeId = req.ErrorTypeId,
            Pattern     = req.Pattern,
            Confidence  = req.Confidence,
            Priority    = req.Priority,
            IsActive    = true,
            CreatedBy   = Actor,
            // Provenance when accepted from an /unconfigured cluster (else null).
            SuggestedBy         = req.SuggestedBy,
            SuggestedFromHash   = req.SuggestedFromHash,
            SuggestedConfidence = req.SuggestedConfidence,
        };
        var saved = await ruleRepo.SaveAsync(rule, ct);

        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   saved.RuleId.ToString(),
            eventType:  "ClassificationRuleCreated",
            actor:      Actor,
            detail:     $"Created ClassificationRule (JobTypeId={saved.JobTypeId}, ErrorTypeId={saved.ErrorTypeId}, Pattern='{saved.Pattern}', Confidence={saved.Confidence}, Priority={saved.Priority})",
            ct: ct);

        return Ok(new { saved.RuleId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("classification-rules/{id:int}")]
    public async Task<IActionResult> UpdateClassificationRule(int id, [FromBody] UpsertClassificationRuleRequest req, CancellationToken ct)
    {

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ClassificationRules.FindAsync([id], ct);
        if (rule is null) return NotFound();

        // Duplicate guard — only an ENABLED rule can collide. Excludes self.
        if (req.IsActive)
        {
            var dupId = await FindActiveClassificationDuplicateAsync(db, req.JobTypeId, req.Pattern, id, ct);
            if (dupId is not null)
                return Conflict(new
                {
                    error = "DuplicateClassificationRule",
                    message = $"An enabled classification rule with this pattern already exists for this job type (rule {dupId}).",
                    conflictingRuleId = dupId,
                });
        }

        var beforeJobTypeId   = rule.JobTypeId;
        var beforeErrorTypeId = rule.ErrorTypeId;
        var beforePattern     = rule.Pattern;
        var beforeConfidence  = rule.Confidence;
        var beforePriority    = rule.Priority;
        var beforeIsActive    = rule.IsActive;

        rule.JobTypeId   = req.JobTypeId;
        rule.ErrorTypeId = req.ErrorTypeId;
        rule.Pattern     = req.Pattern;
        rule.Confidence  = req.Confidence;
        rule.Priority    = req.Priority;
        rule.IsActive    = req.IsActive;

        await db.SaveChangesAsync(ct);

        var diff = BuildDiff(
            ("JobTypeId",   beforeJobTypeId,   rule.JobTypeId),
            ("ErrorTypeId", beforeErrorTypeId, rule.ErrorTypeId),
            ("Pattern",     beforePattern,     rule.Pattern),
            ("Confidence",  beforeConfidence,  rule.Confidence),
            ("Priority",    beforePriority,    rule.Priority),
            ("IsActive",    beforeIsActive,    rule.IsActive));
        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   id.ToString(),
            eventType:  "ClassificationRuleUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("classification-rules/{id:int}")]
    public async Task<IActionResult> DeleteClassificationRule(
        int id, CancellationToken ct)
    {

        // Capture identifying fields before the hard delete so the audit
        // row remains intelligible after the rule is gone.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ClassificationRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RuleId == id, ct);
        if (rule is null) return NotFound();
        var rulePattern = rule.Pattern;

        await ruleRepo.DeleteAsync(id, ct);

        await WriteAuditAsync(
            entityType: "ClassificationRule",
            entityId:   id.ToString(),
            eventType:  "ClassificationRuleDeleted",
            actor:      Actor,
            detail:     $"Deleted ClassificationRule {id} (Pattern='{rulePattern}')",
            ct: ct);

        return NoContent();
    }

    /// <summary>
    /// Returns the RuleId of an ENABLED ClassificationRule with the same
    /// (JobTypeId, Pattern) — the active-key duplicate. Case-insensitive via
    /// the DB collation (matches the unique index + the classifier's matching).
    /// <paramref name="excludeRuleId"/> skips self on update.
    /// </summary>
    private static async Task<int?> FindActiveClassificationDuplicateAsync(
        MaiaDbContext db, int jobTypeId, string pattern, int? excludeRuleId, CancellationToken ct)
        => await db.ClassificationRules
            .Where(r => r.IsActive && r.JobTypeId == jobTypeId && r.Pattern == pattern
                     && (excludeRuleId == null || r.RuleId != excludeRuleId))
            .Select(r => (int?)r.RuleId)
            .FirstOrDefaultAsync(ct);
}

// ── Request contracts ────────────────────────────────────────────────────────

// The audit actor is the authenticated principal (server-side), never a client value,
// so these request contracts carry NO operatorId. Authorization guarantees an
// authenticated user reaches any write.

public sealed record UpsertMonitoredJobRequest(
    string  Name,
    string? DisplayName,
    int     JobTypeId,
    int     PollingIntervalSeconds,
    bool    IsActive,
    string? Description);

public sealed record UpsertScanSourceRequest(
    string  Name,
    int     ScanTypeId,
    string? LogFolder         = null,
    string? SearchPatterns    = null,
    string? InputFolder       = null,
    bool    IncludeSubfolders = false,
    string? ConnectionName    = null,
    string? LogSourceUrl      = null,
    bool    IsActive          = true);

public sealed record UpsertScanCheckRuleRequest(
    string   CheckType,
    string?  SourceTable,
    string   TargetField,
    decimal? MinValue,
    decimal? MaxValue,
    string?  ExpectedValue,
    string?  WatermarkColumn,
    string?  SourceIdColumn,
    string   Severity,
    string?  Description,
    bool     IsActive = true,
    /// <summary>DB scans only — column holding a related row's identity (parent/FK key)
    /// for multi-row child updates. Stored as JobFailure.ReferenceId; exposed via
    /// {referenceId} placeholder. Null = not configured.</summary>
    string?  ReferenceIdColumn = null,
    /// <summary>DB scans only — column on the source row that holds the
    /// input file path. Read into JobFailure.SourceFilePath when matched.</summary>
    string?  FilePathColumn   = null,
    /// <summary>FS scans only — regex with capture group #1 = input file path
    /// extracted from the matching error line. Null = no extraction.</summary>
    string?  InputPathPattern = null,
    // ── FileContent scans only (CheckType=FileContent) ──────────────────────────
    /// <summary>Extractor/format name, e.g. "Xml". Required for FileContent rules.</summary>
    string?  ExtractorType           = null,
    /// <summary>Format-specific address of the value to test (XPath for XML).
    /// Null = filename match alone is the failure signal.</summary>
    string?  ExtractorLocator        = null,
    /// <summary>Format-specific address of the natural key for SourceId (XPath
    /// for XML). Null = fall back to filename without extension.</summary>
    string?  IdentifierLocator       = null,
    /// <summary>Predicate over the extracted value: Equals/NotEquals/Contains/
    /// NotContains. Null = no predicate (filename match fires unconditionally).</summary>
    string?  ExtractorPredicateType  = null,
    /// <summary>Right-hand operand for the predicate. Required with a predicate type.</summary>
    string?  ExtractorPredicateValue = null);

public sealed record UpsertClassificationRuleRequest(
    int     JobTypeId,
    int     ErrorTypeId,
    string  Pattern,
    decimal Confidence,
    int     Priority,
    bool    IsActive = true,
    // Suggestion provenance — set only when accepted from an /unconfigured
    // cluster; null for manual creation. Applied on CREATE only (ignored on update).
    string?  SuggestedBy = null,
    string?  SuggestedFromHash = null,
    decimal? SuggestedConfidence = null);

public sealed record UpsertJobClassificationRuleRequest(
    int     ErrorTypeId,
    string  Pattern,
    decimal Confidence,
    int     Priority,
    bool    IsActive = true);

public sealed record UpsertFixPolicyRuleRequest(
    int     JobTypeId,
    int     ErrorTypeId,
    string  ActionToApply,
    string  FixCategory,
    string  ActionType,
    string? ActionPayload,
    bool    IsAutoHealEligible,
    bool    Enabled,
    /// <summary>NULL = JobType-level default (applies to all jobs of JobTypeId).
    /// Set = MonitoredJob-scoped override that wins over the default for this one job.</summary>
    int?    MonitoredJobId = null,
    /// <summary>Ordered steps for Composite policies. Required when
    /// ActionType=Composite; must be null/empty otherwise. Controller normalises
    /// StepOrder to 1..N (gaps allowed in input).</summary>
    IReadOnlyList<FixPolicyStepDto>? Steps = null,
    // Suggestion provenance — set only when created in response to an
    // /unconfigured Case-B gap; null for manual creation. Applied on CREATE only.
    string?  SuggestedBy = null,
    string?  SuggestedFromHash = null,
    decimal? SuggestedConfidence = null);

public sealed record FixPolicyStepDto(
    int     StepOrder,
    string  ActionType,
    string  ActionPayload,
    string? Description);

public sealed record UpsertErrorTypeRequest(
    string  Code,
    string  DisplayName,
    string? Description,
    string  Severity,
    bool    IsActive = true);
