using System.Globalization;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// Runs active database ScanCheckRules against their SourceTables.
/// Supports ColumnRange and ValueEquals checks.
/// When WatermarkColumn is set on a rule, each scan only reads rows whose
/// WatermarkColumn value exceeds the last stored watermark — so the same row
/// is never reported twice. The first scan processes all existing rows and
/// sets the watermark baseline.
/// The source's connection decides the engine (<see cref="DbConnections"/>): table
/// rules are generated through the matching <see cref="SqlDialect"/> (T-SQL or Oracle
/// SQL), SqlQuery rules run the operator's SQL on that engine's driver.
/// </summary>
public sealed class DatabaseScanStrategy(
    IConfiguration                config,
    IJobRepository                jobRepo,
    IScanWatermarkRepository      watermarks,
    IClassifyJobsUseCase          classify,
    IGenerateSuggestionsUseCase   suggest,
    ISqlQueryRunner               sqlRunner,
    ILogger<DatabaseScanStrategy> logger) : IScanStrategy
{
    private static readonly HashSet<CheckType> SupportedTypes =
        [CheckType.ColumnRange, CheckType.ValueEquals, CheckType.SqlQuery];

    // Code-side cap for SqlQuery (can't inject TOP into an arbitrary query/proc).
    private const int MaxSqlQueryRows = 500;

    // Per-value cap for DetailColumns text appended to ErrorMessage, so one
    // multi-KB XML/stack-trace column can't swamp the failures list.
    internal const int MaxDetailValueLength = 2000;

    // Rendered when a SqlQuery result set lacks a configured detail column —
    // visible on the failure so a typo'd column name is self-diagnosing.
    internal const string DetailColumnMissing = "(column not in result set)";

    /// <summary>
    /// One matching source row as surfaced by either query path.
    /// <see cref="Details"/> carries the operator's DetailColumns values in
    /// configured order (null = SQL NULL); empty when none configured.
    /// <see cref="ConditionValues"/> carries the actual value of each
    /// <see cref="ScanCheckRuleCondition"/>'s column, index-aligned with
    /// <see cref="OrderedConditions"/>; empty when the rule has none (and always
    /// empty on the SqlQuery path, which doesn't support conditions).
    /// </summary>
    private sealed record MatchedRow(
        string RowKey,
        object Value,
        string? WatermarkValue,
        string? SourceIdValue,
        string? ReferenceIdValue,
        string? FilePathValue,
        IReadOnlyList<KeyValuePair<string, string?>> Details,
        IReadOnlyList<string?> ConditionValues);

    private static readonly IReadOnlyList<KeyValuePair<string, string?>> NoDetails = [];
    private static readonly IReadOnlyList<string?> NoConditionValues = [];

    /// <summary>
    /// The rule's extra AND'd conditions in evaluation order. Every site that walks
    /// conditions (filter clause, SELECT aliases, reader, message tokens) MUST use
    /// this so index <c>i</c> means the same condition everywhere.
    /// </summary>
    internal static List<ScanCheckRuleCondition> OrderedConditions(ScanCheckRule rule)
        => rule.Conditions.OrderBy(c => c.ConditionOrder).ToList();

    public ScanType ScanType => ScanType.Database;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, ScanSource source, CancellationToken ct = default)
    {
        var rules = source.ScanCheckRules
            .Where(r => r.IsActive && SupportedTypes.Contains(r.CheckType))
            .ToList();

        if (rules.Count == 0)
            throw new InvalidOperationException(
                $"Job '{job.Name}' has no active database ScanCheckRules. " +
                "Add rules with CheckType 'ColumnRange' or 'ValueEquals'.");

        var missingTable = rules.Where(r => string.IsNullOrWhiteSpace(r.SourceTable)).ToList();
        if (missingTable.Count > 0)
            throw new InvalidOperationException(
                $"Job '{job.Name}': {missingTable.Count} rule(s) have no SourceTable — " +
                $"rule IDs: {string.Join(", ", missingTable.Select(r => r.CheckRuleId))}.");

        // Missing connection string or unrecognised {Name}_ProviderName fails the scan.
        var connection = DbConnections.Resolve(config, source.ConnectionName ?? "DefaultConnection");

        var result = new ScanResult
        {
            JobName  = job.Name,
            ScanType = ScanType.Database,
            Detail   = string.Join(", ", rules.Select(RuleDescription))
        };

        var created = new List<JobFailure>();
        // A single misconfigured rule (e.g. a SqlQuery whose WatermarkColumn isn't in
        // the SELECT, or a bad table/column) must NOT abort the whole source scan and
        // orphan failures other rules already created as unclassified. Catch per rule,
        // keep scanning the rest, then surface the first error AFTER classify so the
        // scan-run is still recorded Failed (visible) without losing classification.
        Exception? ruleError = null;

        foreach (var rule in rules)
        {
            if (!IsRuleValid(rule))
            {
                logger.LogWarning("ScanCheckRule {Id} on job '{Job}' is missing required value config (or has an invalid condition) — skipping",
                    rule.CheckRuleId, job.Name);
                continue;
            }
            if (rule.CheckType == CheckType.SqlQuery && rule.Conditions.Count > 0)
                // Controller forbids this; only reachable via hand-inserted rows.
                logger.LogWarning("ScanCheckRule {Id} on job '{Job}' is a SqlQuery with {Count} condition(s) — conditions are ignored on SqlQuery rules (put the compound logic in the query)",
                    rule.CheckRuleId, job.Name, rule.Conditions.Count);

          try
          {
            // Short, stable label used for BOTH the failure's StepName (nvarchar(200))
            // and the no-watermark dedup key. For SqlQuery the SourceTable IS the
            // (possibly multi-line, multi-KB) query, so it can't serve as either —
            // use the rule Description or a per-rule label. For table rules this is
            // just the table name, exactly as before.
            var stepName = rule.CheckType == CheckType.SqlQuery
                ? (string.IsNullOrWhiteSpace(rule.Description) ? $"SqlQuery #{rule.CheckRuleId}" : rule.Description!)
                : rule.SourceTable!;
            var conn = source.ConnectionName ?? "DefaultConnection";
            var sourceLogPath = rule.CheckType == CheckType.SqlQuery
                ? $"db://{conn}/query"
                : $"db://{conn}/{rule.SourceTable}";

            List<MatchedRow> rows;

            if (rule.CheckType == CheckType.SqlQuery)
            {
                // SqlQuery owns its own watermark + per-SourceId dedup INTERNALLY. The
                // operator's SQL/EXEC can't be safely rewritten to push a watermark
                // filter into the query (could be an EXEC), so both run in-memory on the
                // returned rows. See ScanSqlRuleAsync.
                rows = await ScanSqlRuleAsync(connection, job, rule, stepName, ct);
            }
            else
            {
                // Table rules (ColumnRange / ValueEquals): the watermark filter is pushed
                // into the generated SQL; coarse open-failure dedup only when no watermark.
                string? watermark = rule.WatermarkColumn is not null
                    ? await watermarks.GetDbWatermarkAsync(rule.CheckRuleId, ct)
                    : null;

                rows = await QueryMatchingRowsAsync(connection, rule.SourceTable!, rule, watermark, ct);

                // Advance the watermark to the highest WatermarkColumn value seen this scan.
                // When zero rows matched, take MAX over rows that satisfy the rule's filter —
                // NOT MAX over the whole table, because a future-dated healthy row would jump
                // the watermark past any current-dated unhealthy row inserted next.
                if (rule.WatermarkColumn is not null)
                {
                    var newWatermark = rows.Count > 0
                        ? HighestWatermark(rows, connection.Provider)
                        : await QueryFilteredMaxAsync(connection, rule.SourceTable!, rule.WatermarkColumn, rule, ct);

                    if (newWatermark is not null)
                        await watermarks.UpdateDbWatermarkAsync(rule.CheckRuleId, newWatermark, ct);
                }
                else if (rows.Count > 0)
                {
                    if (await jobRepo.HasOpenFailureAsync(job.MonitoredJobId, stepName, rule.TargetField, ct))
                    {
                        logger.LogDebug(
                            "DatabaseScan '{Job}': open failure already exists for '{Step}' — skipping",
                            job.Name, stepName);
                        continue;
                    }
                }
            }

            if (rows.Count == 0) continue;

            foreach (var row in rows)
            {
                var failure = new JobFailure
                {
                    JobId          = 0,
                    JobTypeId      = job.JobTypeId,          // identity from the job
                    MonitoredJobId = job.MonitoredJobId,
                    ScanSourceId   = source.ScanSourceId,    // which source produced it
                    ScanCheckRuleId = rule.CheckRuleId,      // which rule (View Source lookup)
                    StepName       = stepName,
                    SourceId       = row.SourceIdValue ?? row.RowKey,
                    ReferenceId    = row.ReferenceIdValue,   // null when rule.ReferenceIdColumn unset
                    ErrorMessage   = BuildRowMessage(rule, row.RowKey, row.Value, row.WatermarkValue, row.SourceIdValue, row.Details, row.ConditionValues),
                    SourceLogPath  = sourceLogPath,
                    SourceFilePath = row.FilePathValue,      // null when rule.FilePathColumn unset
                    Status         = JobStatus.Failed,
                    DetectedAt     = DateTime.Now,
                };

                failure = await jobRepo.SaveAsync(failure, ct);
                created.Add(failure);
            }

            logger.LogInformation(
                "DatabaseScan '{Job}': {Step} — {Count} row(s) matched rule {RuleId} ({CheckType})",
                job.Name, stepName, rows.Count, rule.CheckRuleId, rule.CheckType);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              // Misconfigured/failing rule — log, remember the first, keep scanning the rest.
              logger.LogError(ex,
                  "DatabaseScan '{Job}': rule {RuleId} ({CheckType}) failed — skipping it, other rules continue",
                  job.Name, rule.CheckRuleId, rule.CheckType);
              ruleError ??= ex;
          }
        }

        result.FailuresDetected = created.Count;

        // Classify + suggest whatever was created BEFORE surfacing any rule error, so a
        // late-failing rule never leaves earlier rules' failures unclassified.
        if (created.Count > 0)
        {
            var classifications = await classify.ExecuteAsync(created, ct);
            result.Classifications = classifications.Count;

            await suggest.ExecuteAsync(classifications, ct);
            result.Recommendations = classifications.Count;
        }

        // Surface the rule failure now (scan-run recorded Failed for visibility) — after
        // the good rules' failures are safely classified.
        if (ruleError is not null)
            throw new InvalidOperationException(
                $"Scan of job '{job.Name}' completed other rules but rule(s) failed. First error: {ruleError.Message}",
                ruleError);

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Table rules (ColumnRange / ValueEquals) are valid only when the parent check AND
    /// every extra condition have the config their kind needs. One invalid condition
    /// skips the WHOLE rule (mirrors how an invalid parent is treated) — firing on the
    /// parent alone would silently widen the match, which is the one thing a composite
    /// rule must never do.
    /// </summary>
    internal static bool IsRuleValid(ScanCheckRule rule) => rule.CheckType switch
    {
        CheckType.ColumnRange => (rule.MinValue.HasValue || rule.MaxValue.HasValue) && rule.Conditions.All(IsConditionValid),
        CheckType.ValueEquals => !string.IsNullOrWhiteSpace(rule.ExpectedValue) && rule.Conditions.All(IsConditionValid),
        // SqlQuery: the query (SourceTable) + the value column (TargetField) are all
        // that's needed — Option A, every returned row is a failure (no predicate).
        // Conditions are ignored on SqlQuery (warned in the loop), so not validated.
        CheckType.SqlQuery    => !string.IsNullOrWhiteSpace(rule.SourceTable) && !string.IsNullOrWhiteSpace(rule.TargetField),
        _                     => false
    };

    /// <summary>Phase 1 condition kinds are the two DB column predicates; each needs
    /// a column plus the value(s) its kind compares against.</summary>
    internal static bool IsConditionValid(ScanCheckRuleCondition c) =>
        !string.IsNullOrWhiteSpace(c.TargetField) && c.Kind switch
        {
            CheckType.ValueEquals => !string.IsNullOrWhiteSpace(c.ExpectedValue),
            CheckType.ColumnRange => c.MinValue.HasValue || c.MaxValue.HasValue,
            _                     => false,
        };

    private static string RuleDescription(ScanCheckRule r)
    {
        var head = r.CheckType switch
        {
            CheckType.ColumnRange => $"[{r.SourceTable}].[{r.TargetField}] ∈ [{r.MinValue?.ToString() ?? "−∞"}, {r.MaxValue?.ToString() ?? "+∞"}]",
            CheckType.ValueEquals => $"[{r.SourceTable}].[{r.TargetField}] = {r.ExpectedValue}",
            CheckType.SqlQuery    => $"SqlQuery → [{r.TargetField}]",
            _                     => $"[{r.SourceTable}].[{r.TargetField}]"
        };
        return r.Conditions.Count > 0 && r.CheckType != CheckType.SqlQuery
            ? $"{head} +{r.Conditions.Count} cond"
            : head;
    }

    /// <summary>
    /// Splits the rule's comma-separated DetailColumns into trimmed, non-blank,
    /// case-insensitively distinct column references in configured order.
    /// </summary>
    internal static IReadOnlyList<string> ParseDetailColumns(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    /// <summary>
    /// Builds the failure's ErrorMessage. The first line is the rule-derived
    /// synthetic message (byte-identical regardless of DetailColumns, so classifier
    /// tokens and the coverage heuristic are unaffected); each configured detail
    /// column then adds a "\nColumn: value" line, values capped at
    /// <see cref="MaxDetailValueLength"/>.
    /// </summary>
    internal static string BuildRowMessage(ScanCheckRule rule, string rowKey, object value, string? wmValue, string? srcValue,
        IReadOnlyList<KeyValuePair<string, string?>>? details = null,
        IReadOnlyList<string?>? conditionValues = null)
    {
        var head = BuildRowMessageHead(rule, rowKey, value, wmValue, srcValue, conditionValues);
        if (details is null || details.Count == 0) return head;

        var sb = new System.Text.StringBuilder(head);
        foreach (var (column, raw) in details)
        {
            var text = raw ?? "NULL";
            if (text.Length > MaxDetailValueLength)
                text = string.Concat(text.AsSpan(0, MaxDetailValueLength), "…");
            sb.Append('\n').Append(column).Append(": ").Append(text);
        }
        return sb.ToString();
    }

    private static string BuildRowMessageHead(ScanCheckRule rule, string rowKey, object value, string? wmValue, string? srcValue,
        IReadOnlyList<string?>? conditionValues)
    {
        var rowId = srcValue is not null  ? $"{rule.SourceIdColumn}={srcValue}"
                  : wmValue  is not null  ? $"{rule.WatermarkColumn}={wmValue}"
                  : $"row#{rowKey}";

        // One extra "[Field=Value]" token per AND'd condition, appended AFTER the
        // parent's token (which therefore stays the FIRST token — the classifier /
        // coverage-heuristic contract is unchanged) and BEFORE the Description.
        // ValueEquals conditions use the operator-typed ExpectedValue (canonical form,
        // same reasoning as the parent); ColumnRange conditions use the actual value.
        var condTokens = ConditionTokens(rule, conditionValues);

        return rule.CheckType switch
        {
            CheckType.ColumnRange =>
                $"[{rule.SourceTable}].[{rule.TargetField}] = {value} is outside " +
                $"range [{rule.MinValue?.ToString() ?? "−∞"}, {rule.MaxValue?.ToString() ?? "+∞"}] ({rowId})" +
                // Compact, space-free classifier token so an intuitive pattern
                // ("Amount") matches via substring, and the coverage heuristic's
                // synthetic keyword genuinely appears in the message. See DECISIONS.
                $" [{rule.TargetField}={value}]" + condTokens +
                (rule.Description is not null ? $" — {rule.Description}" : ""),
            CheckType.ValueEquals =>
                $"[{rule.SourceTable}].[{rule.TargetField}] = {value} matches error value {rule.ExpectedValue} ({rowId})" +
                // Token uses ExpectedValue (the operator-typed, canonical form) so a
                // literal "Field=Value" pattern matches at runtime without wildcards.
                $" [{rule.TargetField}={rule.ExpectedValue}]" + condTokens +
                (rule.Description is not null ? $" — {rule.Description}" : ""),
            // Predictable, classifier-matchable shape; Description leads when set.
            CheckType.SqlQuery =>
                $"{rule.Description ?? "SqlQuery match"}: [{rule.TargetField}] = {value} ({rowId})",
            _ =>
                $"[{rule.SourceTable}].[{rule.TargetField}] = {value} ({rowId})"
        };
    }

    private static string ConditionTokens(ScanCheckRule rule, IReadOnlyList<string?>? conditionValues)
    {
        if (rule.Conditions.Count == 0 || rule.CheckType == CheckType.SqlQuery) return string.Empty;
        var conds = OrderedConditions(rule);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < conds.Count; i++)
        {
            var c = conds[i];
            var shown = c.Kind == CheckType.ValueEquals
                ? c.ExpectedValue
                : (conditionValues is not null && i < conditionValues.Count ? conditionValues[i] : null) ?? "NULL";
            sb.Append(" [").Append(c.TargetField).Append('=').Append(shown).Append(']');
        }
        return sb.ToString();
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// CheckType.SqlQuery path. Runs the operator's query/EXEC verbatim through the
    /// ISqlQueryRunner seam; EVERY returned row is a candidate failure (Option A —
    /// the operator's WHERE/JOIN is the filter). Two dedup layers run IN-MEMORY here
    /// (the operator SQL can't be rewritten to push them into the query):
    ///   • Watermark (when WatermarkColumn set): keep only rows whose value exceeds
    ///     the stored mark; advance the mark to the highest seen. Parity with the
    ///     ColumnRange/ValueEquals watermark, just applied after the query.
    ///   • Per-SourceId dedup (when SourceIdColumn set): drop rows whose SourceId
    ///     already has an open failure — so a NEW row fires even while an unrelated
    ///     row's failure is still open (the gap the coarse per-rule dedup had).
    /// Both compose; with neither configured it falls back to the coarse per-rule
    /// open-failure dedup. Columns are read BY NAME (operator-defined result shape).
    /// </summary>
    private async Task<List<MatchedRow>> ScanSqlRuleAsync(
        ResolvedConnection connection, MonitoredJob job, ScanCheckRule rule, string stepName, CancellationToken ct)
    {
        var hasWatermark    = !string.IsNullOrWhiteSpace(rule.WatermarkColumn);
        var hasSourceId     = !string.IsNullOrWhiteSpace(rule.SourceIdColumn);
        var hasReferenceId  = !string.IsNullOrWhiteSpace(rule.ReferenceIdColumn);
        var detailColumns   = ParseDetailColumns(rule.DetailColumns);

        var resultRows = await sqlRunner.ExecuteAsync(connection.Provider, connection.ConnectionString, rule.SourceTable!, MaxSqlQueryRows, ct);
        if (resultRows.Count >= MaxSqlQueryRows)
            logger.LogWarning(
                "DatabaseScan '{Job}': SqlQuery rule {RuleId} hit the {Cap}-row cap — results may be truncated. " +
                "For a watermarked rule with a large result set, add 'ORDER BY {Wm} ASC' to the query so the cap reads oldest-first.",
                job.Name, rule.CheckRuleId, MaxSqlQueryRows, rule.WatermarkColumn ?? "<watermark>");

        var stored = hasWatermark ? await watermarks.GetDbWatermarkAsync(rule.CheckRuleId, ct) : null;

        object? maxWm = null;
        var candidates = new List<MatchedRow>();
        var rowIndex = 0;
        foreach (var row in resultRows)
        {
            rowIndex++;

            // Missing TargetField is a config error affecting every row — fail the
            // scan with a clear, actionable message rather than silently producing
            // nothing. The worker records it as a Failed scan-run for this source.
            if (!row.TryGetValue(rule.TargetField, out var targetVal))
                throw new InvalidOperationException(
                    $"SqlQuery rule {rule.CheckRuleId}: result set has no column '{rule.TargetField}' (TargetField). " +
                    $"Columns returned: {(row.Keys.Any() ? string.Join(", ", row.Keys) : "(none)")}.");

            var value = targetVal ?? "NULL";

            // SourceIdColumn optional; absent/empty/null → fall back to row index
            // downstream via `srcValue ?? rowKey`.
            string? srcVal = null;
            if (hasSourceId && row.TryGetValue(rule.SourceIdColumn!, out var sv) && sv is not null)
                srcVal = sv.ToString();

            // ReferenceIdColumn optional; absent/empty/null → ReferenceId stays null
            // (v1 silent-null: a missing/typo'd column silently yields empty {referenceId}).
            string? refVal = null;
            if (hasReferenceId && row.TryGetValue(rule.ReferenceIdColumn!, out var rv) && rv is not null)
                refVal = rv.ToString();

            string? wmCanonical = null;
            if (hasWatermark)
            {
                if (!row.TryGetValue(rule.WatermarkColumn!, out var wmRaw))
                    throw new InvalidOperationException(
                        $"SqlQuery rule {rule.CheckRuleId}: result set has no column '{rule.WatermarkColumn}' (WatermarkColumn). " +
                        $"Add it to the SELECT, or clear the Watermark Column. Columns returned: {string.Join(", ", row.Keys)}.");

                // Track the highest value over ALL returned rows so handled rows aren't
                // re-examined next tick, even ones we filter out below.
                if (wmRaw is not null && (maxWm is null || ValueGreater(wmRaw, maxWm)))
                    maxWm = wmRaw;

                // Incremental filter: skip rows at or below the stored mark.
                if (!IsAfter(wmRaw, stored)) continue;

                wmCanonical = wmRaw is not null ? Canonical(wmRaw) : null;
            }

            // DetailColumns read BY NAME like everything else on this path. A column
            // the operator's SELECT doesn't return renders a visible marker instead of
            // silently vanishing (deliberately NOT ReferenceId's silent-null — a
            // missing diagnostic column is a config error the operator should see).
            IReadOnlyList<KeyValuePair<string, string?>> details = NoDetails;
            if (detailColumns.Count > 0)
            {
                var list = new List<KeyValuePair<string, string?>>(detailColumns.Count);
                foreach (var col in detailColumns)
                {
                    var text = row.TryGetValue(col, out var dv)
                        ? (dv is null ? null : Canonical(dv))
                        : DetailColumnMissing;
                    list.Add(new KeyValuePair<string, string?>(col, text));
                }
                details = list;
            }

            candidates.Add(new MatchedRow(rowIndex.ToString(), value, wmCanonical, srcVal, refVal, null, details, NoConditionValues));
        }

        // Advance the watermark to the highest value seen this scan (if it advanced).
        if (hasWatermark && maxWm is not null && IsAfter(maxWm, stored))
            await watermarks.UpdateDbWatermarkAsync(rule.CheckRuleId, Canonical(maxWm), ct);

        if (candidates.Count == 0) return candidates;

        if (hasSourceId)
        {
            // Per-row dedup: drop candidates whose SourceId already has an open failure.
            var openIds = await jobRepo.GetOpenFailureSourceIdsAsync(job.MonitoredJobId, stepName, ct);
            if (openIds.Count > 0)
                candidates = candidates
                    .Where(c => c.SourceIdValue is null || !openIds.Contains(c.SourceIdValue))
                    .ToList();
        }
        else if (!hasWatermark)
        {
            // Neither a stable key nor a watermark — fall back to the coarse per-rule
            // dedup: skip the whole rule while any failure with this label is open.
            if (await jobRepo.HasOpenFailureAsync(job.MonitoredJobId, stepName, rule.TargetField, ct))
            {
                logger.LogDebug(
                    "DatabaseScan '{Job}': open failure already exists for '{Step}' — skipping",
                    job.Name, stepName);
                return [];
            }
        }

        return candidates;
    }

    // ── Watermark value comparison (in-memory, SqlQuery only) ──────────────────
    // Table rules push '[col] > @Watermark' into SQL and let the server compare by
    // native type. SqlQuery can't, so we compare here: DateTime and numerics compare
    // BY VALUE (not lexically — "9" vs "10"); anything else falls back to ordinal.

    /// <summary>True when <paramref name="value"/> is strictly newer than the stored
    /// watermark string. Null value → false; null/empty stored → true (first scan).</summary>
    private static bool IsAfter(object? value, string? storedWatermark)
    {
        if (value is null) return false;
        if (string.IsNullOrEmpty(storedWatermark)) return true;
        switch (value)
        {
            case DateTime dt:
                return !DateTime.TryParse(storedWatermark, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sdt) || dt > sdt;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double:
                var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return !decimal.TryParse(storedWatermark, NumberStyles.Any, CultureInfo.InvariantCulture, out var sd) || d > sd;
            default:
                return string.CompareOrdinal(value.ToString(), storedWatermark) > 0;
        }
    }

    /// <summary>True when a &gt; b for two values of the same watermark column.</summary>
    internal static bool ValueGreater(object a, object b)
    {
        if (a is DateTime da && b is DateTime db) return da > db;
        try { return Convert.ToDecimal(a, CultureInfo.InvariantCulture) > Convert.ToDecimal(b, CultureInfo.InvariantCulture); }
        catch { return string.CompareOrdinal(a.ToString(), b.ToString()) > 0; }
    }

    /// <summary>Sortable, round-trippable string form for the watermark store.
    /// DateTime uses the same ISO shape as the table path's CONVERT(..., 121).</summary>
    internal static string Canonical(object value) => value switch
    {
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        _           => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    /// <summary>
    /// The watermark to store after a table-rule scan that returned rows.
    /// SQL Server: the lexical max of the CONVERT(…, 121) strings (unchanged legacy
    /// behaviour). Oracle: the rows arrive ORDER BY the watermark column server-side,
    /// so the last non-null value is the true maximum by the column's native type —
    /// "10" correctly beats "9" for a NUMBER key. All-null → null (mark unchanged).
    /// </summary>
    private static string? HighestWatermark(List<MatchedRow> rows, DbProvider provider)
        => provider == DbProvider.Oracle
            ? rows.LastOrDefault(r => r.WatermarkValue is not null)?.WatermarkValue
            : rows.Max(r => r.WatermarkValue ?? string.Empty);

    private const int MaxTableRuleRows = 500;

    private static async Task<List<MatchedRow>> QueryMatchingRowsAsync(
        ResolvedConnection connection, string sourceTable, ScanCheckRule rule, string? watermark, CancellationToken ct)
    {
        var d = SqlDialect.For(connection.Provider);
        var (filterClause, filterParams) = BuildFilterClause(rule, d);
        var watermarkFilter = rule.WatermarkColumn is not null && watermark is not null
            ? $" AND {d.QuoteIdentifier(rule.WatermarkColumn)} > {d.Placeholder("Watermark")}"
            : string.Empty;

        // WatermarkColumn / SourceIdColumn / FilePathColumn / DetailColumns are extra
        // columns projected for tracking, identity, composite-fix path capture, and
        // operator-visible diagnostics.
        // All column names and the table come from admin config and are quoted by the
        // dialect. All filter values are always parameterised.
        // FilePathColumn and DetailColumns support a dotted "alias.Column" form —
        // quote only the column portion so a JOIN encoded in SourceTable still works.
        var quotedTable = d.QuoteTable(sourceTable);
        var detailColumns = ParseDetailColumns(rule.DetailColumns);
        var wmSelect  = rule.WatermarkColumn is not null
            ? $", {d.WatermarkText(d.QuoteIdentifier(rule.WatermarkColumn))} AS {d.Alias("_WatermarkVal")}"
            : string.Empty;
        var srcSelect = rule.SourceIdColumn is not null
            ? $", {d.IdText(d.QuoteIdentifier(rule.SourceIdColumn), 100)} AS {d.Alias("_SourceIdVal")}"
            : string.Empty;
        var refSelect = rule.ReferenceIdColumn is not null
            ? $", {d.IdText(d.QuoteIdentifier(rule.ReferenceIdColumn), 200)} AS {d.Alias("_ReferenceIdVal")}"
            : string.Empty;
        var fpSelect  = !string.IsNullOrEmpty(rule.FilePathColumn)
            ? $", {d.IdText(d.QuoteColumnRef(rule.FilePathColumn), 500)} AS {d.Alias("_FilePathVal")}"
            : string.Empty;
        // Date-typed detail columns render ISO rather than the engine's locale default
        // ("Oct 12 2026 9:14AM") — via style 121 on SQL Server, via the reader on Oracle.
        var detailSelect = string.Concat(detailColumns.Select((col, i) =>
            $", {d.DetailText(d.QuoteColumnRef(col))} AS {d.Alias($"_Detail{i}")}"));
        // Each AND'd condition's column is projected too (for the message token).
        // Aliased _C{i} so a condition may target the parent's column (or another
        // condition's) without a duplicate-column-name clash in the result set.
        var conds = OrderedConditions(rule);
        var condSelect = string.Concat(conds.Select((c, i) =>
            $", {d.ConditionText(d.QuoteIdentifier(c.TargetField))} AS {d.Alias($"_C{i}")}"));

        var sql = d.MatchingRowsQuery(
            targetColumn:  d.QuoteIdentifier(rule.TargetField),
            extraColumns:  wmSelect + srcSelect + refSelect + fpSelect + detailSelect + condSelect,
            table:         quotedTable,
            where:         filterClause + watermarkFilter,
            orderByColumn: rule.WatermarkColumn is not null ? d.QuoteIdentifier(rule.WatermarkColumn) : null,
            maxRows:       MaxTableRuleRows);

        var rows = new List<MatchedRow>();
        await using var conn = await DbConnections.OpenAsync(connection, ct);
        await using var cmd = DbConnections.CreateCommand(conn, sql);

        foreach (var (name, value) in filterParams)
            AddParameter(cmd, name, value);
        if (watermarkFilter.Length > 0)
            AddParameter(cmd, d.ParamName("Watermark"), watermark!);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Optional columns are read BY NAME via their aliases (the parent value at
        // ordinal 1 is the only unaliased column). This replaces the earlier positional
        // nextCol++ arithmetic, which silently depended on every optional column being
        // emitted in exactly the SELECT order above — one reordering away from reading
        // the watermark out of the source-id column.
        var ordinals = new Dictionary<string, int>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) ordinals[reader.GetName(i)] = i;
        // Text projections arrive as strings already; raw ones (Oracle detail columns)
        // are canonicalised so dates render ISO.
        string? ReadOptional(string alias)
            => ordinals.TryGetValue(alias, out var o) && DbConnections.ReadValue(reader, o) is { } v
                ? v as string ?? Canonical(v)
                : null;

        while (await reader.ReadAsync(ct))
        {
            var rowKey  = reader.GetString(0);
            var val     = DbConnections.ReadValue(reader, 1) ?? "NULL";
            var wmVal   = rule.WatermarkColumn is not null              ? ReadOptional("_WatermarkVal")   : null;
            var srcVal  = rule.SourceIdColumn is not null               ? ReadOptional("_SourceIdVal")    : null;
            var refVal  = rule.ReferenceIdColumn is not null            ? ReadOptional("_ReferenceIdVal") : null;
            var fpVal   = !string.IsNullOrEmpty(rule.FilePathColumn)    ? ReadOptional("_FilePathVal")    : null;

            IReadOnlyList<KeyValuePair<string, string?>> details = NoDetails;
            if (detailColumns.Count > 0)
            {
                var list = new List<KeyValuePair<string, string?>>(detailColumns.Count);
                for (var i = 0; i < detailColumns.Count; i++)
                    list.Add(new KeyValuePair<string, string?>(detailColumns[i], ReadOptional($"_Detail{i}")));
                details = list;
            }

            IReadOnlyList<string?> condValues = NoConditionValues;
            if (conds.Count > 0)
            {
                var list = new string?[conds.Count];
                for (var i = 0; i < conds.Count; i++) list[i] = ReadOptional($"_C{i}");
                condValues = list;
            }

            rows.Add(new MatchedRow(rowKey, val, wmVal, srcVal, refVal, fpVal, details, condValues));
        }

        return rows;
    }

    private static void AddParameter(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Bracket a single column reference, supporting an optional "alias.Column"
    /// form used when the operator put a JOIN into SourceTable. Examples:
    ///   "FilePath"       → "[FilePath]"
    ///   "j.FilePath"     → "j.[FilePath]"
    /// Only the column part is bracketed so the alias resolves naturally.
    /// SQL Server form; the Oracle form lives on <see cref="SqlDialect"/>.
    /// </summary>
    internal static string QuoteColumnRef(string columnRef) => SqlDialect.SqlServer.QuoteColumnRef(columnRef);

    /// <summary>
    /// MAX(watermark) over rows that satisfy the rule's filter clause.
    /// Used as the baseline when a scan returns zero matching rows, so the watermark only
    /// ever advances within the population the rule would actually report on.
    /// </summary>
    internal static async Task<string?> QueryFilteredMaxAsync(
        ResolvedConnection connection, string sourceTable, string watermarkColumn, ScanCheckRule rule, CancellationToken ct)
    {
        var d = SqlDialect.For(connection.Provider);
        var (filterClause, filterParams) = BuildFilterClause(rule, d);
        var sql = d.FilteredMaxQuery(d.QuoteIdentifier(watermarkColumn), d.QuoteTable(sourceTable), filterClause);

        await using var conn = await DbConnections.OpenAsync(connection, ct);
        await using var cmd = DbConnections.CreateCommand(conn, sql);
        foreach (var (name, value) in filterParams)
            AddParameter(cmd, name, value);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull || result is null ? null : result.ToString();
    }

    /// <summary>Bracket-quote a SQL identifier, escaping any embedded <c>]</c> as
    /// <c>]]</c> so a column/table name containing <c>]</c> cannot break out of the
    /// quoting. Admin-scoped config, but an unsanitized identifier is still worth
    /// escaping (defense-in-depth; the identifier is the one thing not parameterised).</summary>
    internal static string Bracket(string identifier) => SqlDialect.SqlServer.QuoteIdentifier(identifier);

    /// <summary>
    /// Converts "dbo.Files" → "[dbo].[Files]", or "Files" → "[Files]".
    /// Prevents the bracketing bug where [dbo.Files] is treated as a literal name.
    /// </summary>
    internal static string QuoteTable(string sourceTable) => SqlDialect.SqlServer.QuoteTable(sourceTable);

    /// <summary>T-SQL form of <see cref="BuildFilterClause(ScanCheckRule, SqlDialect)"/>.</summary>
    internal static (string Clause, List<(string Name, object Value)> Params) BuildFilterClause(ScanCheckRule rule)
        => BuildFilterClause(rule, SqlDialect.SqlServer);

    /// <summary>
    /// The rule's WHERE filter: the parent check's clause, then one AND'd clause per
    /// extra condition. Shared by <see cref="QueryMatchingRowsAsync"/> and
    /// <see cref="QueryFilteredMaxAsync"/>, so the watermark baseline is computed over
    /// exactly the population the composite rule reports on. The parent's parameters
    /// are <c>ExactVal</c> / <c>Min</c> / <c>Max</c> (T-SQL <c>@ExactVal</c>, Oracle
    /// <c>:p_ExactVal</c>); condition parameters are prefixed <c>C{i}_</c> so they can
    /// never collide.
    /// </summary>
    internal static (string Clause, List<(string Name, object Value)> Params) BuildFilterClause(ScanCheckRule rule, SqlDialect d)
    {
        var p = new List<(string, object)>();
        string Bind(string name, object value) { p.Add((d.ParamName(name), value)); return d.Placeholder(name); }

        string parentClause;
        if (rule.CheckType == CheckType.ValueEquals)
        {
            parentClause = $"({d.QuoteIdentifier(rule.TargetField)} = {Bind("ExactVal", rule.ExpectedValue!)})";
        }
        else
        {
            // ColumnRange — wrap in parentheses so callers can safely AND
            // additional conditions onto this clause without the precedence bug
            // where (a OR b) AND c parses as a OR (b AND c). The watermark filter
            // in QueryMatchingRowsAsync is exactly that "additional AND" case;
            // unparenthesized, it would bypass the OR's left branch entirely.
            var conditions = new List<string>();
            if (rule.MinValue.HasValue) conditions.Add($"{d.QuoteIdentifier(rule.TargetField)} < {Bind("Min", rule.MinValue.Value)}");
            if (rule.MaxValue.HasValue) conditions.Add($"{d.QuoteIdentifier(rule.TargetField)} > {Bind("Max", rule.MaxValue.Value)}");
            parentClause = $"({string.Join(" OR ", conditions)})";
        }

        if (rule.Conditions.Count == 0) return (parentClause, p);

        var parts = new List<string> { parentClause };
        var conds = OrderedConditions(rule);
        for (var i = 0; i < conds.Count; i++)
        {
            var c   = conds[i];
            var col = d.QuoteIdentifier(c.TargetField);
            if (c.Kind == CheckType.ValueEquals)
            {
                parts.Add($"({col} = {Bind($"C{i}_ExactVal", c.ExpectedValue!)})");
            }
            else
            {
                var cc = new List<string>();
                if (c.MinValue.HasValue) cc.Add($"{col} < {Bind($"C{i}_Min", c.MinValue.Value)}");
                if (c.MaxValue.HasValue) cc.Add($"{col} > {Bind($"C{i}_Max", c.MaxValue.Value)}");
                parts.Add($"({string.Join(" OR ", cc)})");
            }
        }
        return (string.Join(" AND ", parts), p);
    }
}
