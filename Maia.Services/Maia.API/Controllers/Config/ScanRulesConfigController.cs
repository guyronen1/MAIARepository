using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maia.API.Controllers;

/// <summary>
/// ScanCheckRule CRUD, both the transitional job-scoped create and the canonical
/// source-scoped create. Split out of ConfigController; see <see cref="ConfigControllerBase"/>.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = "RequireOperator")]
public class ScanRulesConfigController(
    IDbContextFactory<MaiaDbContext>    dbFactory,
    IEnumerable<IFileContentExtractor>  extractors,
    IAuditRepository                    audit,
    ICurrentUserAccessor                currentUser,
    ILogger<ScanRulesConfigController>  logger)
    : ConfigControllerBase(audit, currentUser, logger)
{
    // FileContent extractors keyed by format — used to validate a rule's
    // locator syntax at save time (each extractor owns its locator grammar).
    private readonly Dictionary<FileFormat, IFileContentExtractor> _extractors =
        extractors.ToDictionary(e => e.Format);

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
    public async Task<IActionResult> DeleteScanRule(int id, CancellationToken ct)
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
}
