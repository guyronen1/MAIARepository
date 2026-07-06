using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Maia.Core.Scanning;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Scanning;

public sealed class FileSystemScanStrategy(
    IDirectoryPipelineUseCase       pipeline,
    IJobRepository                  jobRepo,
    IScanWatermarkRepository        watermarks,
    IClassifyJobsUseCase            classify,
    IGenerateSuggestionsUseCase     suggest,
    ILogger<FileSystemScanStrategy> logger) : IScanStrategy
{
    /// <summary>Hard cap on failures created per file per keyword per scan.
    /// Protects against a pathologically error-filled log chunk spawning thousands of rows.</summary>
    private const int MaxFailuresPerKeywordPerScan = 100;

    /// <summary>Per-strategy-instance cache of compiled InputPathPattern regexes.
    /// Lifetime = scan scope (one strategy per DI scope per tick), so the cache
    /// turns over naturally and never grows unbounded across runs.</summary>
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new();

    public ScanType ScanType => ScanType.FileSystem;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, ScanSource source, CancellationToken ct = default)
    {
        if (source.LogFolder is null)
            throw new InvalidOperationException($"Source '{source.Name}' (job '{job.Name}') has no LogFolder configured for FileSystem scan.");

        // Filename pattern grammar: see FilenamePattern — '*' is the ONLY
        // wildcard, every other character is literal, no-'*' patterns are
        // case-insensitive substring. Operator splits multiple patterns by
        // comma; empty entries are dropped here. Whitespace-only entries
        // would be filtered later by FilenamePattern.Matches returning false.
        var patterns = (source.SearchPatterns ?? "*.log")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        if (patterns.Length == 0)
        {
            logger.LogWarning(
                "FileSystemScan '{Job}/{Source}': SearchPatterns is empty after trimming — no files will be scanned",
                job.Name, source.Name);
        }

        var keywordRules = source.ScanCheckRules
            .Where(r => r.IsActive && r.CheckType == CheckType.ErrorKeyword)
            .ToList();

        logger.LogInformation(
            "FileSystemScan '{Job}/{Source}': total ScanCheckRules={Total}, active ErrorKeyword rules={Keywords}",
            job.Name, source.Name, source.ScanCheckRules.Count, keywordRules.Count);

        var result = new ScanResult
        {
            JobName  = job.Name,
            ScanType = ScanType.FileSystem,
            Detail   = $"Source: {source.Name} | Folder: {source.LogFolder} | Patterns: {string.Join(", ", patterns)}"
        };

        if (keywordRules.Count == 0)
        {
            // No keyword rules — full pipeline mode (scan all log lines). One failing
            // pattern must not abort the others; remember the first error and surface
            // it after, so the scan-run is still recorded Failed (visible).
            Exception? patternError = null;
            foreach (var pattern in patterns)
            {
                try
                {
                    var r = await pipeline.ExecuteAsync(source.LogFolder, pattern, false, ct);
                    result.FailuresDetected += r.JobsCreated;
                    result.Classifications  += r.Classifications;
                    result.Recommendations  += r.Recommendations;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex,
                        "FileSystemScan '{Job}': pipeline pattern '{Pattern}' failed — skipping it, other patterns continue",
                        job.Name, pattern);
                    patternError ??= ex;
                }
            }
            if (patternError is not null)
                throw new InvalidOperationException(
                    $"Scan of job '{job.Name}' completed other patterns but pattern(s) failed. First error: {patternError.Message}",
                    patternError);
            return result;
        }

        // Keyword mode: flag any file whose lines contain one of the configured keywords
        if (!Directory.Exists(source.LogFolder))
        {
            logger.LogWarning("FileSystemScan '{Job}/{Source}': folder not found: {Folder}", job.Name, source.Name, source.LogFolder);
            return result;
        }

        var created = new List<JobFailure>();
        // A single unreadable file (e.g. IOException from ReadNewContentAsync on a
        // locked/rotating log) must NOT abort the whole source scan and orphan the
        // failures other files already produced (classify runs only after the loop).
        // Catch per file, keep scanning, surface the first error AFTER classify.
        Exception? fileError = null;

        // Enumerate ALL files once per scan tick; filter by pattern in code
        // using the FilenamePattern DSL (NOT Directory.GetFiles's native glob,
        // which (a) treats no-'*' as exact filename match, not substring,
        // (b) accepts '?' as a single-char wildcard, and (c) is case-sensitive
        // on Linux/macOS). This keeps behaviour identical across platforms and
        // matches the classification-rule pattern convention.
        // No-arg EnumerateFiles overload returns ALL files, avoiding the
        // Win32-`*` legacy quirk where "*" can match files-with-no-extension only.
        var allFiles = Directory.EnumerateFiles(source.LogFolder).ToList();

        foreach (var pattern in patterns)
        {
            var files = allFiles
                .Where(f => FilenamePattern.Matches(Path.GetFileName(f), pattern))
                .ToList();

            foreach (var file in files)
            {
              try
              {
                var (content, newOffset) = await ReadNewContentAsync(job.MonitoredJobId, file, ct);
                if (string.IsNullOrWhiteSpace(content))
                {
                    if (newOffset > 0)
                        await watermarks.UpdateFileOffsetAsync(job.MonitoredJobId, source.ScanSourceId, file, newOffset, ct);
                    continue;
                }

                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var rule in keywordRules)
                {
                    // Strip glob-style wildcards — Contains() already does substring match
                    var keyword = rule.TargetField.Trim('*').Trim();
                    if (string.IsNullOrEmpty(keyword)) continue;

                    // Every matching line in the new content becomes a failure.
                    // No HasOpenFailureAsync check — the watermark already prevents replays
                    // of old content, so leftover Failed-status rows from prior scans must
                    // not block new errors from being reported.
                    // Within this scan, dedup by exact line text so identical lines spammed
                    // in the same chunk don't create N identical failures.
                    var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var matchesForKeyword = 0;

                    foreach (var rawLine in lines)
                    {
                        if (matchesForKeyword >= MaxFailuresPerKeywordPerScan)
                        {
                            logger.LogWarning(
                                "FileSystemScan '{Job}': hit cap of {Cap} failures for keyword '{Keyword}' in {File} — remaining matches in this chunk skipped",
                                job.Name, MaxFailuresPerKeywordPerScan, keyword, Path.GetFileName(file));
                            break;
                        }

                        if (!rawLine.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var excerpt = rawLine.Trim();
                        if (excerpt.Length == 0) continue;
                        if (!seenInBatch.Add(excerpt))
                            continue; // identical line text already produced a failure this scan
                        if (excerpt.Length > 500) excerpt = excerpt[..500];

                        // Input-path extraction (composite-fix support). Captures
                        // the INPUT file the failing process was acting on — distinct
                        // from `file` which is the LOG file where we found the error.
                        // Null when the rule has no InputPathPattern configured, the
                        // regex didn't match, or the capture was relative without an
                        // InputFolder on the job. Logged at Info so operators can grep.
                        string? sourceFilePath = null;
                        if (!string.IsNullOrEmpty(rule.InputPathPattern))
                        {
                            sourceFilePath = ExtractInputPath(rawLine, rule.InputPathPattern, source.InputFolder);
                            if (sourceFilePath is null)
                                logger.LogInformation(
                                    "FileSystemScan '{Job}': InputPathPattern on rule {RuleId} did not capture in line — line snippet: {Excerpt}",
                                    job.Name, rule.CheckRuleId,
                                    excerpt[..Math.Min(80, excerpt.Length)]);
                        }

                        var failure = new JobFailure
                        {
                            JobId          = 0,
                            JobTypeId      = job.JobTypeId,          // identity from the job
                            MonitoredJobId = job.MonitoredJobId,
                            ScanSourceId   = source.ScanSourceId,    // which source produced it
                            StepName       = Path.GetFileName(file),
                            SourceId       = Path.GetFileName(file),
                            ErrorMessage   = $"[{keyword}] {Path.GetFileName(file)}: {excerpt}",
                            SourceLogPath  = file,
                            SourceFilePath = sourceFilePath,
                            Status         = JobStatus.Failed,
                            DetectedAt     = DateTime.Now,
                        };

                        failure = await jobRepo.SaveAsync(failure, ct);
                        created.Add(failure);
                        matchesForKeyword++;

                        logger.LogInformation(
                            "FileSystemScan '{Job}': keyword '{Keyword}' matched in {File} — FailureId {FailureId}",
                            job.Name, keyword, Path.GetFileName(file), failure.FailureId);
                    }
                }

                await watermarks.UpdateFileOffsetAsync(job.MonitoredJobId, source.ScanSourceId, file, newOffset, ct);
              }
              catch (Exception ex) when (ex is not OperationCanceledException)
              {
                  logger.LogError(ex,
                      "FileSystemScan '{Job}': file {File} failed — skipping it, other files continue",
                      job.Name, file);
                  fileError ??= ex;
              }
            }
        }

        result.FailuresDetected = created.Count;

        // Classify + suggest whatever was created BEFORE surfacing any file error, so a
        // late-failing file never leaves earlier files' failures unclassified.
        if (created.Count > 0)
        {
            var classifications = await classify.ExecuteAsync(created, ct);
            result.Classifications = classifications.Count;

            await suggest.ExecuteAsync(classifications, ct);
            result.Recommendations = classifications.Count;
        }

        if (fileError is not null)
            throw new InvalidOperationException(
                $"Scan of job '{job.Name}' completed other files but file(s) failed. First error: {fileError.Message}",
                fileError);

        return result;
    }

    /// <summary>
    /// Compile-then-match a rule's <see cref="ScanCheckRule.InputPathPattern"/>
    /// against a single matching line. Returns the resolved (absolute) input
    /// file path on success, null on any non-match path:
    ///   - regex is invalid (config bug) — swallowed, returns null
    ///   - regex matched but had no capture group
    ///   - regex matched with capture group #1 empty
    ///   - capture is relative and no InputFolder on the job
    ///   - regex match timed out (50ms hard cap, pathological pattern)
    ///
    /// Compiled regexes are cached on the strategy instance per-pattern so
    /// repeated lines for the same rule don't re-compile.
    /// </summary>
    private string? ExtractInputPath(string line, string pattern, string? inputFolder)
    {
        var rx = _regexCache.GetOrAdd(pattern, CompileSafely);
        if (rx is null) return null;

        Match match;
        try { match = rx.Match(line); }
        catch (RegexMatchTimeoutException) { return null; }

        if (!match.Success || match.Groups.Count < 2) return null;

        var captured = match.Groups[1].Value.Trim();
        if (captured.Length == 0) return null;

        if (Path.IsPathRooted(captured))
            return captured;

        if (string.IsNullOrEmpty(inputFolder))
        {
            logger.LogWarning(
                "InputPathPattern captured relative path '{Captured}' but job has no InputFolder configured — SourceFilePath will be null",
                captured);
            return null;
        }
        return Path.Combine(inputFolder, captured);
    }

    private static Regex? CompileSafely(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException) { return null; }
    }

    private async Task<(string Content, long NewOffset)> ReadNewContentAsync(
        int monitoredJobId, string filePath, CancellationToken ct)
    {
        var fromOffset = await watermarks.GetFileOffsetAsync(monitoredJobId, filePath, ct);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fromOffset > stream.Length)
            fromOffset = 0; // file was rotated or truncated

        if (fromOffset == stream.Length)
            return (string.Empty, fromOffset); // nothing new

        stream.Seek(fromOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var content      = await reader.ReadToEndAsync(ct);
        return (content, stream.Position);
    }
}
