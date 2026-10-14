using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Scanning;
using System.Text.RegularExpressions;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.Scanning;
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
    IScanWatermarkSeeder                seeder,
    IConfiguration                      config,
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

    // Statements that return no result set over ODP.NET (procedure calls, PL/SQL blocks).
    private static readonly Regex OracleNonQuery =
        new(@"^\s*(EXEC|EXECUTE|CALL|BEGIN|DECLARE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Engine of the source's connection (<c>{Name}_ProviderName</c>). Non-Database
    /// sources and unrecognised values report SqlServer — the source save already
    /// rejects an unknown provider, so that only covers hand-edited config.
    /// </summary>
    private DbProvider ProviderOf(ScanSource source) =>
        DbConnections.TryParseProvider(DbConnections.ProviderSetting(config, source.ConnectionName ?? "DefaultConnection"), out var p)
            ? p
            : DbProvider.SqlServer;

    /// <summary>
    /// SqlQuery-scoped validation (CheckType.SqlQuery), mirroring the FileContent
    /// validator. Cheap + save-time — no SQL parsing/execution (same trust model as
    /// the existing SourceTable: a wrong query fails clearly at scan time). For
    /// CheckType.SqlQuery, SourceTable holds the operator-written query / "EXEC
    /// sp_Name …" (SQL Server) or a SELECT (Oracle — 400 OracleQueryNotSelect for a
    /// procedure call / PL/SQL block). Returns a 400 on inconsistent config, else null.
    /// </summary>
    private IActionResult? ApplyAndValidateSqlQuery(UpsertScanCheckRuleRequest req, ScanCheckRule rule, CheckType checkType, DbProvider provider)
    {
        if (checkType != CheckType.SqlQuery) return null;

        if (string.IsNullOrWhiteSpace(rule.SourceTable))
            return BadRequest(new { error = "SourceQueryRequired",
                message = provider == DbProvider.Oracle
                    ? "SqlQuery rules require a Source Query — an Oracle SELECT statement."
                    : "SqlQuery rules require a Source Query — a SELECT statement or 'EXEC sp_Name @p=…'." });

        if (provider == DbProvider.Oracle && OracleNonQuery.IsMatch(rule.SourceTable))
            return BadRequest(new { error = "OracleQueryNotSelect",
                message = "This source is an Oracle connection: the Source Query must be a SELECT (or WITH … SELECT). " +
                          "Procedure calls and PL/SQL blocks return no rows to scan — select from a view or a pipelined function instead." });

        if (string.IsNullOrWhiteSpace(rule.TargetField))
            return BadRequest(new { error = "TargetFieldRequired",
                message = "SqlQuery rules require a TargetField — the result-set column whose value is shown on each failure." });

        // Option A: every returned row is a failure (the operator's WHERE is the
        // filter). The range/equality predicate and file-path fields don't apply —
        // null them so stale UI values can't leak onto a SqlQuery rule.
        //
        // WatermarkColumn, SourceIdColumn, ReferenceIdColumn and DetailColumns ARE
        // kept: SqlQuery does incremental watermarking + per-SourceId dedup in-memory
        // (parity with ValueEquals), and reads detail columns BY NAME from the result
        // set. All are optional and name result-set columns; we can't validate their
        // presence here (the result shape is only known at scan time — a missing
        // watermark/target column fails the scan with a clear message; a missing
        // detail column renders a visible "(column not in result set)" marker).
        rule.MinValue         = null;
        rule.MaxValue         = null;
        rule.ExpectedValue    = null;
        rule.FilePathColumn   = null;
        rule.InputPathPattern = null;
        return null;
    }

    /// <summary>
    /// Value validation for the table-rule kinds added 2026-10-14 (NotEquals, Contains,
    /// InList) — 400 <c>CheckValueRequired</c> / <c>InvalidInList</c>, so a rule the scan
    /// would silently skip can't be saved. InList is normalised to "1, 2, 3". The
    /// unused range bounds are nulled so stale UI values can't leak. ValueEquals and
    /// ColumnRange keep their existing (scan-time) behaviour.
    /// </summary>
    private IActionResult? ApplyAndValidateTableCheck(ScanCheckRule rule, CheckType checkType)
    {
        if (checkType is not (CheckType.NotEquals or CheckType.Contains or CheckType.InList)) return null;

        if (checkType == CheckType.InList)
        {
            if (!DbCheckKinds.TryParseInList(rule.ExpectedValue, out var values))
                return BadRequest(new { error = "InvalidInList",
                    message = $"In List needs 1–{DbCheckKinds.MaxInListItems} comma-separated numbers, e.g. '1, 2, 3'." });
            rule.ExpectedValue = DbCheckKinds.FormatInList(values);
        }
        else if (string.IsNullOrWhiteSpace(rule.ExpectedValue))
            return BadRequest(new { error = "CheckValueRequired",
                message = checkType == CheckType.Contains
                    ? "Contains needs the text to search for."
                    : "Not Equal needs the value the column should have." });

        rule.MinValue = null;
        rule.MaxValue = null;
        return null;
    }

    /// <summary>
    /// Composite scan rule (Phase 1 — DB table rules): validates and normalises the
    /// request's extra AND'd conditions. Mirrors
    /// <c>FixPolicyConfigController.ValidateCompositePayload</c>: parse → per-item
    /// checks naming the offending order → stable sort → re-pack to 1..N. Returns a
    /// 400 on inconsistent config, else null with <paramref name="normalised"/> set
    /// (empty when the request carries no conditions).
    ///   • ConditionsNotSupported — parent isn't a Database table rule (<see cref="DbCheckKinds.IsTableCheck"/>)
    ///   • ConditionKindMismatch  — a condition's Kind isn't a table-rule kind
    ///   • ConditionIncomplete    — missing column, ExpectedValue (InList: not numbers) or bounds (ColumnRange)
    /// </summary>
    private IActionResult? ApplyAndValidateConditions(
        UpsertScanCheckRuleRequest req, CheckType checkType, out List<ScanCheckRuleCondition> normalised)
    {
        normalised = [];
        if (req.Conditions is not { Count: > 0 }) return null;

        if (!DbCheckKinds.IsTableCheck(checkType))
            return BadRequest(new { error = "ConditionsNotSupported",
                message = checkType == CheckType.SqlQuery
                    ? "SqlQuery rules cannot have conditions — put the compound logic in the query's WHERE/JOIN."
                    : $"Conditions are supported on Database table rules only in this version (rule is {checkType})." });

        var parsed = new List<ScanCheckRuleCondition>(req.Conditions.Count);
        foreach (var c in req.Conditions)
        {
            if (!Enum.TryParse<CheckType>(c.Kind, ignoreCase: true, out var kind)
                || !DbCheckKinds.IsTableCheck(kind))
                return BadRequest(new { error = "ConditionKindMismatch",
                    message = $"Condition at order {c.ConditionOrder} has Kind '{c.Kind}'; a Database rule accepts ValueEquals, NotEquals, Contains, InList or ColumnRange." });

            if (string.IsNullOrWhiteSpace(c.TargetField))
                return BadRequest(new { error = "ConditionIncomplete",
                    message = $"Condition at order {c.ConditionOrder} has no column (TargetField)." });

            if (!DbCheckKinds.HasRequiredValue(kind, c.ExpectedValue, c.MinValue, c.MaxValue))
                return BadRequest(new { error = "ConditionIncomplete",
                    message = kind switch
                    {
                        CheckType.ColumnRange => $"Condition at order {c.ConditionOrder} ({c.TargetField}) is ColumnRange but has neither MinValue nor MaxValue.",
                        CheckType.InList      => $"Condition at order {c.ConditionOrder} ({c.TargetField}) is InList but its value isn't 1–{DbCheckKinds.MaxInListItems} comma-separated numbers.",
                        _                     => $"Condition at order {c.ConditionOrder} ({c.TargetField}) is {kind} but has no ExpectedValue.",
                    } });

            var expected = kind == CheckType.InList && DbCheckKinds.TryParseInList(c.ExpectedValue, out var listValues)
                ? DbCheckKinds.FormatInList(listValues)
                : c.ExpectedValue;

            // Null the fields the kind doesn't use so stale UI values can't leak.
            parsed.Add(new ScanCheckRuleCondition
            {
                ConditionOrder = c.ConditionOrder,
                Kind           = kind,
                TargetField    = c.TargetField.Trim(),
                ExpectedValue  = DbCheckKinds.UsesExpectedValue(kind) ? expected : null,
                MinValue       = kind == CheckType.ColumnRange ? c.MinValue : null,
                MaxValue       = kind == CheckType.ColumnRange ? c.MaxValue : null,
                PredicateType  = null,
                Description    = string.IsNullOrWhiteSpace(c.Description) ? null : c.Description,
            });
        }

        // Re-pack orders to 1..N (stable on the operator's input order) so the UI never
        // has to renumber after a delete and the unique (CheckRuleId, ConditionOrder)
        // index can't trip. Duplicate input orders are tolerated, like fix-policy steps.
        var order = 0;
        foreach (var c in parsed.OrderBy(c => c.ConditionOrder).ToList())
            c.ConditionOrder = ++order;
        normalised = parsed.OrderBy(c => c.ConditionOrder).ToList();
        return null;
    }

    /// <summary>Compact one-line summary of a rule's conditions for audit text —
    /// <c>"(none)"</c> or e.g. <c>"1:ValueEquals Region=EU AND 2:ColumnRange Amount[10, 100]"</c>.
    /// The " AND " joiner states the semantics; order shows reorders in a diff.</summary>
    internal static string SummariseConditions(IEnumerable<ScanCheckRuleCondition> conditions)
    {
        var ordered = conditions.OrderBy(c => c.ConditionOrder).ToList();
        return ordered.Count == 0
            ? "(none)"
            : string.Join(" AND ", ordered.Select(c => c.Kind switch
            {
                CheckType.ValueEquals => $"{c.ConditionOrder}:ValueEquals {c.TargetField}={c.ExpectedValue}",
                CheckType.NotEquals   => $"{c.ConditionOrder}:NotEquals {c.TargetField}<>{c.ExpectedValue}",
                CheckType.Contains    => $"{c.ConditionOrder}:Contains {c.TargetField}~'{c.ExpectedValue}'",
                CheckType.InList      => $"{c.ConditionOrder}:InList {c.TargetField} in ({c.ExpectedValue})",
                _                     => $"{c.ConditionOrder}:{c.Kind} {c.TargetField}[{c.MinValue?.ToString() ?? "−∞"}, {c.MaxValue?.ToString() ?? "+∞"}]",
            }));
    }

    /// <summary>
    /// Normalises and validates <see cref="UpsertScanCheckRuleRequest.DetailColumns"/>:
    /// trims each comma-separated token, drops blanks, and rejects tokens that can't be
    /// a plain (optionally alias-qualified) column name — brackets, semicolons, or
    /// embedded whitespace. The scan strategy brackets every identifier so these aren't
    /// an injection risk; this is purely so a typo surfaces as a 400 at save time
    /// rather than as a failed scan run later. Returns the normalised CSV or null.
    /// </summary>
    private static IActionResult? NormalizeDetailColumns(string? raw, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var tokens = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var bad = tokens.FirstOrDefault(t => t.IndexOfAny(['[', ']', ';']) >= 0 || t.Any(char.IsWhiteSpace));
        if (bad is not null)
            return new BadRequestObjectResult(new { error = "InvalidDetailColumns",
                message = $"Detail Columns entry '{bad}' is not a plain column name. Use comma-separated column names (optionally alias.Column), without brackets, spaces or semicolons." });

        normalized = tokens.Length == 0 ? null : string.Join(", ", tokens);
        return null;
    }

    /// <summary>
    /// "Start scanning from now": after a successful create, seed the rule's DB
    /// watermark at the current MAX (see <see cref="IScanWatermarkSeeder"/>). Runs
    /// AFTER SaveChanges and never fails the request: a seed failure is returned as a
    /// warning (the rule stays saved; its first scan processes full history, i.e.
    /// today's behaviour). Returns the audit note + the warning (if any).
    /// </summary>
    private async Task<(string AuditNote, string[]? Warnings)> SeedRuleIfRequestedAsync(
        UpsertScanCheckRuleRequest req, ScanCheckRule rule, ScanSource source, CancellationToken ct)
    {
        if (!req.SeedWatermarkFromNow) return (string.Empty, null);
        var r = await seeder.SeedRuleFromNowAsync(rule, source, ct);
        if (!r.Applicable) return (string.Empty, null);
        var note = $", SeedWatermarkFromNow=true ({(r.Warning is null ? r.Detail : "failed: " + r.Warning)})";
        return (note, r.Warning is null ? null : [r.Warning]);
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
        var activeSources = await db.ScanSources
            .Where(s => s.MonitoredJobId == jobId && s.IsActive)
            .ToListAsync(ct);
        if (activeSources.Count != 1)
            return BadRequest(new { error = "AmbiguousSourceForRule",
                message = activeSources.Count == 0
                    ? "This job has no active scan source. Add a source first, then add rules to it."
                    : "This job has multiple sources. Add the rule to a specific source via /config/scan-sources/{id}/scan-rules." });

        if (NormalizeDetailColumns(req.DetailColumns, out var detailColumns) is { } dcError) return dcError;
        var rule = new ScanCheckRule
        {
            MonitoredJobId   = jobId,
            ScanSourceId     = activeSources[0].ScanSourceId,
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
            DetailColumns    = detailColumns,
            InputPathPattern = req.InputPathPattern,
            Severity         = Enum.Parse<Severity>(req.Severity),
            Description      = req.Description,
            IsActive         = true,
        };
        if (ApplyAndValidateFileContent(req, rule, checkType) is { } fcError) return fcError;
        if (ApplyAndValidateSqlQuery(req, rule, checkType, ProviderOf(activeSources[0])) is { } sqError) return sqError;
        if (ApplyAndValidateTableCheck(rule, checkType) is { } tcError) return tcError;
        if (ApplyAndValidateConditions(req, checkType, out var conditions) is { } condError) return condError;

        // Conditions ride along on the header insert — one SaveChanges, EF fixes up CheckRuleId.
        rule.Conditions = conditions;
        db.ScanCheckRules.Add(rule);
        await db.SaveChangesAsync(ct);

        var (seedNote, warnings) = await SeedRuleIfRequestedAsync(req, rule, activeSources[0], ct);
        var condNote = conditions.Count > 0 ? $", {conditions.Count} condition(s): {SummariseConditions(conditions)}" : string.Empty;

        await WriteAuditAsync(
            entityType: "ScanCheckRule",
            entityId:   rule.CheckRuleId.ToString(),
            eventType:  "ScanRuleCreated",
            actor:      Actor,
            detail:     $"Created ScanCheckRule for MonitoredJob {jobId} (CheckType={rule.CheckType}, TargetField='{rule.TargetField}', Severity={rule.Severity}{condNote}{seedNote})",
            ct: ct);

        return Ok(new { rule.CheckRuleId, warnings });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("scan-rules/{id:int}")]
    public async Task<IActionResult> UpdateScanRule(int id, [FromBody] UpsertScanCheckRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Include Conditions: replace-all persistence below removes the existing rows.
        var rule = await db.ScanCheckRules
            .Include(r => r.Conditions)
            .FirstOrDefaultAsync(r => r.CheckRuleId == id, ct);
        if (rule is null) return NotFound();

        var beforeConditions       = SummariseConditions(rule.Conditions);
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
        var beforeDetailColumns    = rule.DetailColumns;
        var beforeInputPathPattern = rule.InputPathPattern;
        var beforeExtractorType    = rule.ExtractorType;
        var beforeExtractorLocator = rule.ExtractorLocator;
        var beforeIdentifierLocator= rule.IdentifierLocator;
        var beforePredicateType    = rule.ExtractorPredicateType;
        var beforePredicateValue   = rule.ExtractorPredicateValue;
        var beforeSeverity         = rule.Severity;
        var beforeDescription      = rule.Description;
        var beforeIsActive         = rule.IsActive;

        // Owning source: its connection's engine shapes SqlQuery validation, and the
        // "start from now" seed below runs against it.
        var ruleSource = await db.ScanSources.FindAsync([rule.ScanSourceId], ct);
        if (ruleSource is null) return NotFound();

        var checkType = Enum.Parse<CheckType>(req.CheckType);
        if (NormalizeDetailColumns(req.DetailColumns, out var detailColumns) is { } dcError) return dcError;
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
        rule.DetailColumns    = detailColumns;
        rule.InputPathPattern = req.InputPathPattern;
        rule.Severity         = Enum.Parse<Severity>(req.Severity);
        rule.Description      = req.Description;
        rule.IsActive         = req.IsActive;
        if (ApplyAndValidateFileContent(req, rule, checkType) is { } fcError) return fcError;
        if (ApplyAndValidateSqlQuery(req, rule, checkType, ProviderOf(ruleSource)) is { } sqError) return sqError;
        if (ApplyAndValidateTableCheck(rule, checkType) is { } tcError) return tcError;
        if (ApplyAndValidateConditions(req, checkType, out var conditions) is { } condError) return condError;

        // Conditions: replace-all (same as FixPolicyRuleSteps). Diffing per-condition
        // would be cleaner but the audit summary below already shows before → after,
        // and a null/empty request clears existing conditions by design.
        db.ScanCheckRuleConditions.RemoveRange(rule.Conditions);
        rule.Conditions = conditions;

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
            ("DetailColumns",    beforeDetailColumns,    rule.DetailColumns),
            ("InputPathPattern", beforeInputPathPattern, rule.InputPathPattern),
            ("ExtractorType",    beforeExtractorType,    rule.ExtractorType),
            ("ExtractorLocator", beforeExtractorLocator, rule.ExtractorLocator),
            ("IdentifierLocator",beforeIdentifierLocator,rule.IdentifierLocator),
            ("ExtractorPredicateType",  beforePredicateType,  rule.ExtractorPredicateType),
            ("ExtractorPredicateValue", beforePredicateValue, rule.ExtractorPredicateValue),
            ("Severity",         beforeSeverity,         rule.Severity),
            ("Description",      beforeDescription,      rule.Description),
            ("IsActive",         beforeIsActive,         rule.IsActive),
            ("Conditions",       beforeConditions,       SummariseConditions(rule.Conditions)));
        // "Start scanning from now" on edit: insert-only, so it only acts where no
        // watermark exists yet (e.g. a WatermarkColumn was just added, or a file-based
        // rule's source has unseen files). Returns 200 + warnings when it ran.
        var (seedNote, warnings) = (string.Empty, (string[]?)null);
        if (req.SeedWatermarkFromNow)
            (seedNote, warnings) = await SeedRuleIfRequestedAsync(req, rule, ruleSource, ct);

        await WriteAuditAsync(
            entityType: "ScanCheckRule",
            entityId:   id.ToString(),
            eventType:  "ScanRuleUpdated",
            actor:      Actor,
            detail:     (diff.Length > 0 ? diff : "No changes") + seedNote,
            ct: ct);

        return seedNote.Length > 0 ? Ok(new { warnings }) : NoContent();
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
        if (NormalizeDetailColumns(req.DetailColumns, out var detailColumns) is { } dcError) return dcError;
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
            DetailColumns     = detailColumns,
            InputPathPattern  = req.InputPathPattern,
            Severity          = Enum.Parse<Severity>(req.Severity),
            Description       = req.Description,
            IsActive          = true,
        };
        if (ApplyAndValidateFileContent(req, rule, checkType) is { } fcError) return fcError;
        if (ApplyAndValidateSqlQuery(req, rule, checkType, ProviderOf(source)) is { } sqError) return sqError;
        if (ApplyAndValidateTableCheck(rule, checkType) is { } tcError) return tcError;
        if (ApplyAndValidateConditions(req, checkType, out var conditions) is { } condError) return condError;

        rule.Conditions = conditions;
        db.ScanCheckRules.Add(rule);
        await db.SaveChangesAsync(ct);

        var (seedNote, warnings) = await SeedRuleIfRequestedAsync(req, rule, source, ct);
        var condNote = conditions.Count > 0 ? $", {conditions.Count} condition(s): {SummariseConditions(conditions)}" : string.Empty;

        await WriteAuditAsync("ScanCheckRule", rule.CheckRuleId.ToString(), "ScanRuleCreated", Actor,
            $"Created ScanCheckRule for ScanSource {sourceId} (CheckType={rule.CheckType}, TargetField='{rule.TargetField}', Severity={rule.Severity}{condNote}{seedNote})", ct);
        return Ok(new { rule.CheckRuleId, warnings });
    }
}
