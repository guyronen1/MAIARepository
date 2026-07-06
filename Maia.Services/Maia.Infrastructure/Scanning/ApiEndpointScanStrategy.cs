using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// GETs LogSourceUrl and treats non-2xx responses or bodies containing "error"/"exception" as failures.
/// </summary>
public sealed class ApiEndpointScanStrategy(
    IHttpClientFactory          httpFactory,
    IJobRepository              jobRepo,
    IClassifyJobsUseCase        classify,
    IGenerateSuggestionsUseCase suggest,
    ILogger<ApiEndpointScanStrategy> logger) : IScanStrategy
{
    public ScanType ScanType => ScanType.ApiEndpoint;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, ScanSource source, CancellationToken ct = default)
    {
        if (source.LogSourceUrl is null)
            throw new InvalidOperationException($"Source '{source.Name}' (job '{job.Name}') has no LogSourceUrl configured for ApiEndpoint scan.");

        var result = new ScanResult
        {
            JobName  = job.Name,
            ScanType = ScanType.ApiEndpoint,
            Detail   = $"URL: {source.LogSourceUrl}"
        };

        string?             statusStr    = null;
        string              responseBody = string.Empty;
        bool                isFailure;

        try
        {
            var http     = httpFactory.CreateClient();
            var response = await http.GetAsync(source.LogSourceUrl, ct);
            statusStr    = response.StatusCode.ToString();
            responseBody = await response.Content.ReadAsStringAsync(ct);

            isFailure = !response.IsSuccessStatusCode
                     || responseBody.Contains("error",     StringComparison.OrdinalIgnoreCase)
                     || responseBody.Contains("exception", StringComparison.OrdinalIgnoreCase)
                     || responseBody.Contains("failed",    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            statusStr    = "Unreachable";
            responseBody = ex.Message;
            isFailure    = true;
        }

        if (!isFailure)
            return result;

        var snippet = responseBody.Length > 500 ? responseBody[..500] : responseBody;
        var failure = new JobFailure
        {
            JobId          = 0,
            JobTypeId      = job.JobTypeId,          // identity from the job
            MonitoredJobId = job.MonitoredJobId,
            ScanSourceId   = source.ScanSourceId,    // which source produced it
            StepName       = "ApiEndpointCheck",
            SourceId       = source.LogSourceUrl,
            ErrorMessage   = $"API check failed: status={statusStr}, body={snippet}",
            SourceLogPath  = source.LogSourceUrl,
            Status         = JobStatus.Failed,
            DetectedAt     = DateTime.Now,
        };

        failure = await jobRepo.SaveAsync(failure, ct);
        result.FailuresDetected = 1;

        logger.LogInformation("ApiEndpointScan '{Job}/{Source}': failure detected at {Url} — status {Status}",
            job.Name, source.Name, source.LogSourceUrl, statusStr);

        var classifications = await classify.ExecuteAsync([failure], ct);
        result.Classifications = classifications.Count;

        await suggest.ExecuteAsync(classifications, ct);
        result.Recommendations = classifications.Count;

        return result;
    }
}
