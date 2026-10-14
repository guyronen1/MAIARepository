using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Scanning;
using Maia.Infrastructure.Fix;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// "Start scanning from now" — see <see cref="IScanWatermarkSeeder"/>.
/// Runs inside the config save request, so every external touch (source DB query,
/// folder enumeration) is bounded by <see cref="ExecutorTimeouts.Default"/> and any
/// failure is reported as a <c>Warning</c> on the result, never thrown: the
/// rule/source save has already succeeded and must not be rolled back because the
/// seed couldn't run.
///
/// Mirrors the strategies' own notion of "position" so the seeded row is exactly
/// what a scan would have written had it just run and found nothing new:
///  • DB table rules → <c>MAX(WatermarkColumn)</c> over rule-filtered rows
///    (<see cref="DatabaseScanStrategy.QueryFilteredMaxAsync"/>, the same baseline
///    the strategy uses on a zero-row scan) — NOT wall-clock, because the column
///    may be an integer ID or a datetime in the source DB's clock.
///  • DB SqlQuery rules → the operator's query is run and the max of the
///    WatermarkColumn result column taken in-memory (same comparison as the scan).
///  • FileSystem sources → each currently matching file's byte length.
///  • FileContent sources → each file's current LastWriteTime.
/// File paths are produced by the same <c>Directory.EnumerateFiles</c> calls the
/// strategies use so the (MonitoredJobId, FilePath) key matches byte-for-byte.
/// </summary>
public sealed class ScanWatermarkSeeder(
    IConfiguration               config,
    IScanWatermarkRepository     watermarks,
    ISqlQueryRunner              sqlRunner,
    ILogger<ScanWatermarkSeeder> logger) : IScanWatermarkSeeder
{
    private const int MaxSqlQueryRows = 500; // same cap as DatabaseScanStrategy

    public async Task<WatermarkSeedResult> SeedRuleFromNowAsync(ScanCheckRule rule, ScanSource source, CancellationToken ct = default)
    {
        // File-based rules have no per-rule watermark: their position lives on the
        // SOURCE (per-file byte offset / mtime). Ticking the box on such a rule seeds
        // the owning source — insert-only, so a source already seeded at create time
        // is a no-op and only files that appeared since get a row.
        if (rule.CheckType == CheckType.ErrorKeyword) return await SeedSourceFromNowAsync(source, ScanType.FileSystem,  ct);
        if (rule.CheckType == CheckType.FileContent)  return await SeedSourceFromNowAsync(source, ScanType.FileContent, ct);
        if (!DbCheckKinds.IsDatabaseCheck(rule.CheckType))
            return WatermarkSeedResult.NotApplicable($"{rule.CheckType} rules have no watermark.");
        if (string.IsNullOrWhiteSpace(rule.WatermarkColumn))
            return WatermarkSeedResult.NotApplicable("rule has no WatermarkColumn — nothing to seed.");
        if (string.IsNullOrWhiteSpace(rule.SourceTable))
            return WatermarkSeedResult.Failed("Watermark not seeded: the rule has no Source Table / Query.");

        var connName   = source.ConnectionName ?? "DefaultConnection";
        var connection = DbConnections.TryResolve(config, connName, out var connError);
        if (connection is null)
            return WatermarkSeedResult.Failed($"Watermark not seeded: {connError} The first scan will process all existing rows.");

        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);
        try
        {
            string? max;
            string? capNote = null;
            if (rule.CheckType == CheckType.SqlQuery)
            {
                var rows = await sqlRunner.ExecuteAsync(connection.Provider, connection.ConnectionString, rule.SourceTable!, MaxSqlQueryRows, cts.Token);
                object? best = null;
                foreach (var row in rows)
                {
                    if (!row.TryGetValue(rule.WatermarkColumn!, out var v))
                        return WatermarkSeedResult.Failed(
                            $"Watermark not seeded: the query's result set has no column '{rule.WatermarkColumn}' (WatermarkColumn). The first scan will process all returned rows.");
                    if (v is null) continue;
                    if (best is null || DatabaseScanStrategy.ValueGreater(v, best)) best = v;
                }
                max = best is null ? null : DatabaseScanStrategy.Canonical(best);
                if (rows.Count >= MaxSqlQueryRows)
                    capNote = $" The query returned at least {MaxSqlQueryRows} rows (the scan cap), so the seeded value may be below the true maximum; if the first scan still reports old rows, add 'ORDER BY {rule.WatermarkColumn} DESC' to the query.";
            }
            else
            {
                max = await DatabaseScanStrategy.QueryFilteredMaxAsync(connection, rule.SourceTable!, rule.WatermarkColumn!, rule, cts.Token);
            }

            if (max is null)
                return new WatermarkSeedResult(true, 0, 0, "no matching rows exist yet — nothing to seed; the first scan is already incremental");

            var added = await watermarks.TryAddDbWatermarkAsync(rule.CheckRuleId, max, ct);
            logger.LogInformation(
                "WatermarkSeed: rule {RuleId} ({CheckType}) {Outcome} at {WatermarkColumn}={Value}",
                rule.CheckRuleId, rule.CheckType, added ? "seeded" : "already had a watermark, left untouched", rule.WatermarkColumn, max);
            return added
                ? new WatermarkSeedResult(true, 1, 0, $"{rule.WatermarkColumn}={max}", capNote is null ? null : "Watermark seeded." + capNote)
                : new WatermarkSeedResult(true, 0, 1, "existing watermark kept (not overwritten)");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("WatermarkSeed: rule {RuleId} timed out after {Timeout}s querying '{Conn}'", rule.CheckRuleId, ExecutorTimeouts.Default.TotalSeconds, connName);
            return WatermarkSeedResult.Failed($"Watermark not seeded: querying '{connName}' timed out after {ExecutorTimeouts.Default.TotalSeconds:0}s. The first scan will process all existing rows.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "WatermarkSeed: rule {RuleId} failed against '{Conn}'", rule.CheckRuleId, connName);
            return WatermarkSeedResult.Failed($"Watermark not seeded: {ex.Message} The first scan will process all existing rows.");
        }
    }

    public async Task<WatermarkSeedResult> SeedSourceFromNowAsync(ScanSource source, ScanType scanType, CancellationToken ct = default)
    {
        if (scanType is not (ScanType.FileSystem or ScanType.FileContent))
            return WatermarkSeedResult.NotApplicable($"{scanType} sources have no file position to seed (DB rules seed per rule).");
        if (string.IsNullOrWhiteSpace(source.LogFolder))
            return WatermarkSeedResult.Failed("Watermark not seeded: the source has no folder.");
        if (!Directory.Exists(source.LogFolder))
            return WatermarkSeedResult.Failed($"Watermark not seeded: folder '{source.LogFolder}' was not found. Existing files will be processed in full on the first scan once the folder exists.");

        var seeded = 0; var skipped = 0; var errors = 0; string? firstError = null;
        try
        {
            if (scanType == ScanType.FileSystem)
            {
                // Same enumeration + pattern DSL as FileSystemScanStrategy (top-level only).
                var patterns = (source.SearchPatterns ?? "*.log")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();
                var files = Directory.EnumerateFiles(source.LogFolder)
                    .Where(f => patterns.Any(p => FilenamePattern.Matches(Path.GetFileName(f), p)));

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var length = new FileInfo(file).Length;
                        if (await watermarks.TryAddFileOffsetAsync(source.MonitoredJobId, source.ScanSourceId, file, length, ct)) seeded++; else skipped++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { errors++; firstError ??= $"{Path.GetFileName(file)}: {ex.Message}"; }
                }
            }
            else
            {
                // Same enumeration as FileContentScanStrategy. Rules (and so filename
                // patterns) may not exist yet at source-create time, so every file gets a
                // row — a row for a file no rule ever matches is inert.
                var option = source.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(source.LogFolder, "*", option))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var mtime = File.GetLastWriteTime(file);
                        if (await watermarks.TryAddContentWatermarkAsync(source.MonitoredJobId, source.ScanSourceId, file, mtime, ct)) seeded++; else skipped++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { errors++; firstError ??= $"{Path.GetFileName(file)}: {ex.Message}"; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "WatermarkSeed: source {SourceId} could not enumerate '{Folder}'", source.ScanSourceId, source.LogFolder);
            return WatermarkSeedResult.Failed($"Watermark not seeded: could not read folder '{source.LogFolder}' ({ex.Message}). The first scan will process all existing files.");
        }

        logger.LogInformation(
            "WatermarkSeed: source {SourceId} ({ScanType}) seeded {Seeded} file(s), kept {Skipped} existing, {Errors} unreadable in '{Folder}'",
            source.ScanSourceId, scanType, seeded, skipped, errors, source.LogFolder);

        var detail  = $"{seeded} file(s) seeded, {skipped} existing kept" + (errors > 0 ? $", {errors} unreadable" : "");
        var warning = errors > 0
            ? $"Watermark seeded for {seeded} file(s), but {errors} could not be read (first: {firstError}). Those files will be processed in full on the first scan."
            : null;
        return new WatermarkSeedResult(true, seeded, skipped, detail, warning);
    }
}
