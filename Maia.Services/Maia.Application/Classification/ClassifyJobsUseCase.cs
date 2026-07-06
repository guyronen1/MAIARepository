using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Microsoft.Extensions.Logging;

namespace Maia.Application.Classification;

public sealed class ClassifyJobsUseCase(
    IJobRepository jobs,
    IClassificationStrategy classifier,
    ILogger<ClassifyJobsUseCase> logger) : IClassifyJobsUseCase
{
    /// <summary>Classifies all failed jobs that have not yet been classified (ErrorTypeId is null).</summary>
    public async Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(CancellationToken ct = default)
    {
        var unclassified = await jobs.GetUnclassifiedAsync(ct);
        return await ClassifyManyAsync(unclassified, ct);
    }

    /// <summary>Classifies a specific set of job failures (e.g. just-created ones).</summary>
    public async Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(
        IEnumerable<JobFailure> jobList,
        CancellationToken ct = default)
        => await ClassifyManyAsync(jobList, ct);

    private async Task<IReadOnlyList<ClassificationResult>> ClassifyManyAsync(
        IEnumerable<JobFailure> jobList, CancellationToken ct)
    {
        var results = new List<ClassificationResult>();

        foreach (var job in jobList)
        {
            ct.ThrowIfCancellationRequested();

            // Classify against the failure's captured ErrorMessage — the strategy already
            // selected the relevant line/row. Re-reading the whole log file here would
            // make the classifier pick the FIRST matching pattern across the entire file,
            // not the line that actually triggered THIS failure.
            var logContent = job.ErrorMessage ?? string.Empty;

            var result = await classifier.ClassifyAsync(job, logContent, ct);

            if (result is not null)
            {
                await jobs.UpdateClassificationAsync(job.FailureId, result, ct);
                results.Add(result);
                logger.LogInformation(
                    "Job {JobId} classified as {ErrorTypeCode} (confidence {Confidence:P0})",
                    job.JobId, result.ErrorTypeCode, result.Confidence);
            }
        }

        return results;
    }
}
