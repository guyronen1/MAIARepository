using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

/// <summary>
/// On-demand scan trigger for MonitoredJobs.
/// Delegates to the IScanStrategy registered for each job's ScanType —
/// no scan-type-specific logic lives here.
///
/// Manual scans go through <see cref="ExecuteAndRecordAsync"/> which writes a
/// <c>ScanRunHistory</c> row exactly like the background <c>MonitoringWorker</c>
/// does, so the dashboard's recent-activity strip surfaces both paths the same way.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireOperator")]   // manual scan / classify triggers
public class JobScanController(
    IMonitoredJobRepository      jobRepo,
    IMonitoredJobLeaseRepository leaseRepo,
    IEnumerable<IScanStrategy>   strategies,
    IClassifyJobsUseCase         classify,
    IGenerateSuggestionsUseCase  suggest,
    IExecuteFixesUseCase         execute,
    IScanRunHistoryRepository    historyRepo) : ControllerBase
{
    // Per-process identity for manual triggers — mirrors the worker's LeasedBy format
    // so audit queries can grep "host=...;runId=..." consistently across both paths.
    private static readonly string ManualLeasedByPrefix =
        $"manual;host={Environment.MachineName};pid={Environment.ProcessId}";
    /// <summary>Run the scan pipeline for a MonitoredJob by its ID.</summary>
    [HttpGet("{monitoredJobId:int}")]
    [HttpPost("{monitoredJobId:int}")]
    public async Task<IActionResult> ScanById(int monitoredJobId, CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(monitoredJobId, ct);
        if (job is null)
            return NotFound(new { Message = $"MonitoredJob {monitoredJobId} not found." });

        var leased = await leaseRepo.GetActivelyLeasedJobIdsAsync([monitoredJobId], ct);
        if (leased.Contains(monitoredJobId))
            return Conflict(new { Message = $"Job '{job.Name}' is already being scanned — try again once it finishes." });

        return await RunScanAsync(job, ct);
    }

    /// <summary>Run the scan pipeline for a MonitoredJob by its Name.</summary>
    [HttpGet("by-name/{name}")]
    [HttpPost("by-name/{name}")]
    public async Task<IActionResult> ScanByName(string name, CancellationToken ct)
    {
        var job = await jobRepo.GetByNameAsync(name, ct);
        if (job is null)
            return NotFound(new { Message = $"MonitoredJob '{name}' not found." });

        var leased = await leaseRepo.GetActivelyLeasedJobIdsAsync([job.MonitoredJobId], ct);
        if (leased.Contains(job.MonitoredJobId))
            return Conflict(new { Message = $"Job '{job.Name}' is already being scanned — try again once it finishes." });

        return await RunScanAsync(job, ct);
    }

    /// <summary>
    /// Run the scan pipeline for ALL active MonitoredJobs immediately.
    /// Jobs that are currently being scanned by the background worker (or another
    /// concurrent manual request) are skipped — Skipped=true in the response.
    /// </summary>
    [HttpPost("scan-all")]
    public async Task<IActionResult> ScanAll(CancellationToken ct)
    {
        var jobs    = await jobRepo.GetActiveAsync(ct);
        var leased  = await leaseRepo.GetActivelyLeasedJobIdsAsync(jobs.Select(j => j.MonitoredJobId), ct);
        var results = new List<object>();

        foreach (var job in jobs)
        {
            if (leased.Contains(job.MonitoredJobId))
            {
                results.Add(new { job.MonitoredJobId, job.Name, Skipped = true, Reason = "Already scanning" });
                continue;
            }

            try
            {
                var r = await RunJobSourcesAsync(job, ct);
                results.Add(new
                {
                    job.MonitoredJobId, job.Name, Skipped = false,
                    r.FailuresDetected, r.Classifications, r.Recommendations,
                    r.Detail
                });
            }
            catch (Exception ex)
            {
                results.Add(new { job.MonitoredJobId, job.Name, Skipped = false, Error = ex.Message });
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Re-classifies all Failed JobFailures that have no ErrorType assigned yet,
    /// then generates suggestions and executes fixes for the newly classified set.
    /// </summary>
    [HttpGet("classify-pending")]
    [HttpPost("classify-pending")]
    public async Task<IActionResult> ClassifyPending(CancellationToken ct)
    {
        var classifications = await classify.ExecuteAsync(ct);
        await suggest.ExecuteAsync(classifications, ct);
        await execute.ExecuteAsync(ct);

        return Ok(new
        {
            Classified   = classifications.Count,
            Suggestions  = classifications.Count,
            FixesQueued  = classifications.Count,
        });
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private async Task<IActionResult> RunScanAsync(MonitoredJob job, CancellationToken ct)
    {
        if (!job.ScanSources.Any(s => s.IsActive))
            return BadRequest(new { Message = $"Job '{job.Name}' has no active scan sources." });

        var agg = await RunJobSourcesAsync(job, ct);
        return Ok(agg);
    }

    /// <summary>
    /// Tier 2.5: run every active source of the job (sequentially) and aggregate the
    /// per-source ScanResults into one response. Each source writes its own
    /// ScanRunHistory row (with ScanSourceId), mirroring the worker. Best-effort per
    /// source (an exception on one source is recorded and the rest still run); client
    /// cancellation propagates. For today's single-source jobs the aggregate equals
    /// that one source's result.
    /// </summary>
    private async Task<ScanResult> RunJobSourcesAsync(MonitoredJob job, CancellationToken ct)
    {
        var firstSource = job.ScanSources.FirstOrDefault(s => s.IsActive);
        var agg = new ScanResult { JobName = job.Name, ScanType = firstSource?.ScanType ?? ScanType.FileSystem, Detail = string.Empty };
        var details = new List<string>();

        foreach (var source in job.ScanSources.Where(s => s.IsActive))
        {
            var strategy = strategies.FirstOrDefault(s => s.ScanType == source.ScanType);
            if (strategy is null)
            {
                details.Add($"{source.Name}: no strategy for {source.ScanType}");
                await RecordSourceHistoryAsync(job, source.ScanSourceId, JobRunOutcome.Failed,
                    $"No scan strategy for ScanType '{source.ScanType}'", null, DateTime.Now, ct);
                continue;
            }

            var startedAt = DateTime.Now;
            var outcome   = JobRunOutcome.Success;
            string? error = null;
            ScanResult? r = null;
            try
            {
                r = await strategy.ScanAsync(job, source, ct);
                agg.FailuresDetected             += r.FailuresDetected;
                agg.Classifications              += r.Classifications;
                agg.Recommendations              += r.Recommendations;
                agg.IdentifierExtractionFailures += r.IdentifierExtractionFailures;
                agg.OversizeFileSkips            += r.OversizeFileSkips;
                agg.PredicateUnevaluableSkips    += r.PredicateUnevaluableSkips;
                if (!string.IsNullOrEmpty(r.Detail)) details.Add($"{source.Name}: {r.Detail}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = JobRunOutcome.Timeout;
                error   = "Scan cancelled by client";
                await RecordSourceHistoryAsync(job, source.ScanSourceId, outcome, error, r, startedAt, ct);
                throw;   // client gone — propagate
            }
            catch (Exception ex)
            {
                outcome = JobRunOutcome.Failed;
                error   = ex.Message;
                details.Add($"{source.Name}: ERROR {ex.Message}");
            }
            await RecordSourceHistoryAsync(job, source.ScanSourceId, outcome, error, r, startedAt, ct);
        }

        agg.Detail = string.Join(" | ", details);
        return agg;
    }

    /// <summary>
    /// Appends one ScanRunHistory row for a manual source scan — mirrors the worker's
    /// per-source row. History-write failures are swallowed so they never poison the
    /// operator's scan response.
    /// </summary>
    private async Task RecordSourceHistoryAsync(
        MonitoredJob job, int scanSourceId, JobRunOutcome outcome, string? error,
        ScanResult? result, DateTime startedAt, CancellationToken ct)
    {
        var leasedBy    = $"{ManualLeasedByPrefix};runId={Guid.NewGuid():N}";
        var completedAt = DateTime.Now;
        try
        {
            var durationMs = (int)Math.Clamp((completedAt - startedAt).TotalMilliseconds, 0, int.MaxValue);
            await historyRepo.SaveAsync(new ScanRunHistory
            {
                MonitoredJobId   = job.MonitoredJobId,
                ScanSourceId     = scanSourceId,
                LeasedBy         = leasedBy,
                StartedAt        = startedAt,
                CompletedAt      = completedAt,
                DurationMs       = durationMs,
                Outcome          = outcome,
                Error            = error is null ? null : (error.Length > 2000 ? error[..2000] : error),
                FailuresDetected = result?.FailuresDetected ?? 0,
                Classifications  = result?.Classifications  ?? 0,
                Recommendations  = result?.Recommendations  ?? 0,
                IdentifierExtractionFailures = result?.IdentifierExtractionFailures ?? 0,
                OversizeFileSkips            = result?.OversizeFileSkips            ?? 0,
                PredicateUnevaluableSkips    = result?.PredicateUnevaluableSkips    ?? 0,
            }, ct);
        }
        catch { /* don't let history-write failures affect the scan response */ }
    }
}
