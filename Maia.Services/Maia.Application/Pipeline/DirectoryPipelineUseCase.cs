using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Maia.Core.Scanning;
using Microsoft.Extensions.Logging;

namespace Maia.Application.Pipeline;

public sealed class DirectoryPipelineUseCase(
    IJobRepository jobs,
    IMonitoredJobRepository monitoredJobs,
    IScanWatermarkRepository watermarks,
    ILogParser parser,
    IClassifyJobsUseCase classify,
    IGenerateSuggestionsUseCase suggest,
    ILogger<DirectoryPipelineUseCase> logger) : IDirectoryPipelineUseCase
{
    public async Task<DirectoryPipelineResult> ExecuteAsync(
        string directoryPath,
        string searchPattern = "*.log",
        bool recursive = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path is required.", nameof(directoryPath));

        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException(directoryPath);

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        // Filter via the shared FilenamePattern DSL (same convention as
        // classification-rule patterns): '*' is the ONLY wildcard, every
        // other character is literal, no-'*' patterns are case-insensitive
        // SUBSTRING match. Enumerating all files with the no-pattern overload
        // sidesteps the Win32-`*` legacy quirk (matches no-extension files
        // only) and gives cross-platform-consistent semantics.
        var files = string.IsNullOrWhiteSpace(searchPattern)
            ? Directory.EnumerateFiles(directoryPath, "*", option).ToList()
            : Directory.EnumerateFiles(directoryPath, "*", option)
                .Where(f => FilenamePattern.Matches(Path.GetFileName(f), searchPattern))
                .ToList();

        var result = new DirectoryPipelineResult
        {
            DirectoryPath = directoryPath,
            FilesScanned  = files.Count,
        };

        var activeJobs = (await monitoredJobs.GetActiveAsync(ct))
            .Where(j => j.ScanSources.Any(s => s.IsActive && s.ScanType == ScanType.FileSystem))
            .ToList();

        var created = new List<JobFailure>();

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (match, matchedSource) = ResolveMonitoredJob(filePath, activeJobs);
                var monitoredJobId         = match?.MonitoredJobId;

                // Only read content appended since the last scan
                var (content, newOffset) = await ReadNewContentAsync(filePath, monitoredJobId, ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var lines      = parser.ParseLog(content);
                var errorLines = lines
                    .Where(l => l.Contains("error",     StringComparison.OrdinalIgnoreCase)
                             || l.Contains("exception", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Always advance watermark even if no errors in this chunk
                if (monitoredJobId.HasValue && matchedSource is not null)
                    await watermarks.UpdateFileOffsetAsync(monitoredJobId.Value, matchedSource.ScanSourceId, filePath, newOffset, ct);

                if (!errorLines.Any()) continue;

                var jobTypeId = match?.JobTypeId ?? 1;

                var failure = new JobFailure
                {
                    JobId          = 0,
                    JobTypeId      = jobTypeId,
                    MonitoredJobId = monitoredJobId,
                    StepName       = Path.GetFileNameWithoutExtension(filePath),
                    SourceId       = errorLines.First(),
                    ErrorMessage   = string.Join(Environment.NewLine, errorLines),
                    SourceLogPath  = filePath,
                    Status         = JobStatus.Failed,
                    DetectedAt     = DateTime.Now,
                };

                failure = await jobs.SaveAsync(failure, ct);
                created.Add(failure);

                logger.LogInformation(
                    "Saved JobFailure {FailureId} for file {FileName} (new bytes: {Bytes})",
                    failure.FailureId, filePath, newOffset);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process file {FilePath}", filePath);
            }
        }

        if (!created.Any())
        {
            logger.LogInformation("No new errors found in scanned files");
            return result;
        }

        var classifications = await classify.ExecuteAsync(created, ct);
        result.Classifications = classifications.Count;

        foreach (var c in classifications)
            await jobs.UpdateClassificationAsync(c.FailureId, c, ct);

        await suggest.ExecuteAsync(classifications, ct);
        result.Recommendations = classifications.Count;

        result.JobsCreated = created.Count;

        logger.LogInformation(
            "Pipeline done: {FilesScanned} files, {JobsCreated} failures, {Classifications} classified",
            result.FilesScanned, result.JobsCreated, result.Classifications);

        return result;
    }

    /// <summary>
    /// Reads only the bytes appended since the last watermark.
    /// If the file was rotated/truncated (new size &lt; offset), resets to the beginning.
    /// </summary>
    private async Task<(string Content, long NewOffset)> ReadNewContentAsync(
        string filePath, int? monitoredJobId, CancellationToken ct)
    {
        var fromOffset = monitoredJobId.HasValue
            ? await watermarks.GetFileOffsetAsync(monitoredJobId.Value, filePath, ct)
            : 0;

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fromOffset > stream.Length)
            fromOffset = 0; // file was rotated or truncated

        if (fromOffset == stream.Length)
            return (string.Empty, fromOffset); // nothing new

        stream.Seek(fromOffset, SeekOrigin.Begin);
        using var reader  = new StreamReader(stream, leaveOpen: true);
        var content       = await reader.ReadToEndAsync(ct);
        var newOffset     = stream.Position;

        return (content, newOffset);
    }

    private static (MonitoredJob? Job, ScanSource? Source) ResolveMonitoredJob(
        string filePath,
        List<MonitoredJob> activeJobs)
    {
        var fileDir  = Path.GetDirectoryName(filePath) ?? string.Empty;
        var fileName = Path.GetFileName(filePath);

        foreach (var job in activeJobs)
        {
            var fsSource = job.ScanSources
                .FirstOrDefault(s => s.IsActive
                    && s.ScanType == ScanType.FileSystem
                    && fileDir.Equals(s.LogFolder, StringComparison.OrdinalIgnoreCase));

            if (fsSource is null) continue;

            if (fsSource.SearchPatterns is null) return (job, fsSource);

            var patterns = fsSource.SearchPatterns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (patterns.Any(p => FilenamePattern.Matches(fileName, p)))
                return (job, fsSource);
        }
        return (null, null);
    }
}
