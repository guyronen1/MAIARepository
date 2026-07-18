using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Workers;

/// <summary>
/// Drives the scan pipeline by claiming MonitoredJob leases from the DB and
/// running scans for each claimed job in parallel. Multiple instances can run
/// concurrently — the DB lease serialises per-job execution.
/// </summary>
public sealed class MonitoringWorker(
    IServiceScopeFactory      scopeFactory,
    IWorkerControlService     control,
    IConfiguration            config,
    ILogger<MonitoringWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private const int MaxParallelism = 4;
    private const int ClaimBatchSize = MaxParallelism;

    private readonly string _leasedBy =
        $"host={Environment.MachineName};pid={Environment.ProcessId};runId={Guid.NewGuid():N}";

    // Orphaned-unclassified sweep cadence + age gate (see IReclassifyOrphanedFailuresUseCase).
    // One knob drives both: a failure must be older than this to be swept (so in-flight
    // scans aren't touched), and the sweep runs at most this often. Clamped ≥ 1 min.
    private readonly TimeSpan _orphanMinAge = TimeSpan.FromMinutes(
        Math.Max(1, config.GetValue<int?>("Scan:OrphanReclassifyMinutes") ?? 10));
    private DateTime _lastOrphanSweep = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MonitoringWorker started as {LeasedBy}", _leasedBy);

        // Startup drain — runs once before the polling loop to clear approvals or
        // auto-heals that accumulated while this process was down.
        await DrainPendingFixesAsync("startup", stoppingToken);

        // Startup sweep — a crash last run may have stranded failures saved past their
        // watermark without classification. Recover them immediately (force past the
        // throttle) rather than waiting for the first cadence tick.
        await SweepOrphanedUnclassifiedAsync(force: true, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (control.IsPaused)
            {
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            try
            {
                // Throttled safety-net sweep (runs even on ticks that claim nothing —
                // the crash that stranded failures may have been another instance).
                await SweepOrphanedUnclassifiedAsync(force: false, stoppingToken);

                IReadOnlyList<ClaimedJobLease> claimed;
                using (var scope = scopeFactory.CreateScope())
                {
                    var leases = scope.ServiceProvider.GetRequiredService<IMonitoredJobLeaseRepository>();
                    claimed = await leases.ClaimAsync(_leasedBy, ClaimBatchSize, stoppingToken);
                }

                if (claimed.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                logger.LogInformation("Claimed {Count} job(s) for scan", claimed.Count);

                await Parallel.ForEachAsync(
                    claimed,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxParallelism,
                        CancellationToken      = stoppingToken,
                    },
                    async (lease, ct) => await RunOneJobAsync(lease, ct));

                // Post-tick drain — one call after the parallel batch completes. Picks up
                // auto-heals generated during this tick plus any operator approvals that
                // arrived while we were scanning. Gated on claimed.Count > 0 so idle ticks
                // don't burn cycles on an empty pending query.
                await DrainPendingFixesAsync("post-tick", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MonitoringWorker tick failed");
                // Don't hot-loop on a persistent failure
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        logger.LogInformation("MonitoringWorker stopped");
    }

    /// <summary>
    /// Recovers orphaned-unclassified failures (see IReclassifyOrphanedFailuresUseCase).
    /// Throttled to <see cref="_orphanMinAge"/> cadence unless <paramref name="force"/>
    /// (startup). Its own scope + try so a failure here never breaks the scan loop.
    /// </summary>
    private async Task SweepOrphanedUnclassifiedAsync(bool force, CancellationToken ct)
    {
        if (!force && DateTime.Now - _lastOrphanSweep < _orphanMinAge) return;
        _lastOrphanSweep = DateTime.Now;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var sweep = scope.ServiceProvider.GetRequiredService<IReclassifyOrphanedFailuresUseCase>();
            await sweep.ExecuteAsync(_orphanMinAge, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MonitoringWorker: orphaned-unclassified sweep failed");
        }
    }

    private async Task DrainPendingFixesAsync(string trigger, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var execute = scope.ServiceProvider.GetRequiredService<IExecuteFixesUseCase>();
            await execute.ExecuteAsync(ct);
            logger.LogDebug("MonitoringWorker: {Trigger} drain complete", trigger);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MonitoringWorker: {Trigger} drain failed", trigger);
        }
    }

    private async Task RunOneJobAsync(ClaimedJobLease lease, CancellationToken hostCt)
    {
        // Per-job timeout = lease duration. If the scan exceeds it, we cancel
        // and another worker will eventually steal the (now-expired) lease.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        jobCts.CancelAfter(TimeSpan.FromSeconds(lease.LeaseDurationSeconds));

        // Fresh DI scope per job so each scan has its own DbContext, repos, strategies.
        using var scope = scopeFactory.CreateScope();
        var jobRepo     = scope.ServiceProvider.GetRequiredService<IMonitoredJobRepository>();
        var leaseRepo   = scope.ServiceProvider.GetRequiredService<IMonitoredJobLeaseRepository>();
        var historyRepo = scope.ServiceProvider.GetRequiredService<IScanRunHistoryRepository>();
        var strategies  = scope.ServiceProvider.GetServices<IScanStrategy>();

        var jobOutcome = JobRunOutcome.Success;   // rolled-up across sources (worst wins)
        string? jobError = null;                   // first error surfaced, for the lease row
        var pollingIntervalSeconds = 300;

        try
        {
            var job = await jobRepo.GetByIdAsync(lease.MonitoredJobId, jobCts.Token);
            if (job is null)
            {
                // Pre-source failure: record on the lease (LastRunOutcome); ScanRunHistory
                // captures actual source executions only, so no history row here.
                jobOutcome = JobRunOutcome.Failed;
                jobError   = $"MonitoredJob {lease.MonitoredJobId} not found at scan time";
                logger.LogWarning(jobError);
                return;
            }

            pollingIntervalSeconds = job.PollingIntervalSeconds;

            var sources = job.ScanSources.Where(s => s.IsActive).ToList();
            if (sources.Count == 0)
            {
                logger.LogWarning("MonitoringWorker: job '{Name}' has no active scan sources — nothing to scan", job.Name);
                return;   // outcome stays Success; nothing executed → no history row
            }

            // Tier 2.5: sources run SEQUENTIALLY under the single per-job lease. One
            // ScanRunHistory row per source. A source exception is best-effort (record
            // that source Failed, continue); a timeout (shared jobCts) ends the tick —
            // remaining sources have no budget and get no row.
            foreach (var source in sources)
            {
                if (jobCts.IsCancellationRequested) break;

                var srcStartedAt   = DateTime.Now;
                var srcOutcome     = JobRunOutcome.Success;
                string? srcError   = null;
                ScanResult? result = null;

                try
                {
                    var strategy = strategies.FirstOrDefault(s => s.ScanType == source.ScanType);
                    if (strategy is null)
                    {
                        srcOutcome = JobRunOutcome.Failed;
                        srcError   = $"No scan strategy for ScanType '{source.ScanType}'";
                        logger.LogWarning("{Error} on source '{Source}' of job '{Name}'", srcError, source.Name, job.Name);
                    }
                    else
                    {
                        logger.LogInformation(
                            "MonitoringWorker: [{ScanType}] scan for '{Name}/{Source}' (lease {Seconds}s)",
                            source.ScanType, job.Name, source.Name, lease.LeaseDurationSeconds);

                        result = await strategy.ScanAsync(job, source, jobCts.Token);

                        logger.LogInformation(
                            "Source '{Name}/{Source}' [{ScanType}]: {Failures} failures, " +
                            "{Classifications} classified, {Recommendations} recommendations — {Detail}",
                            job.Name, source.Name, source.ScanType,
                            result.FailuresDetected, result.Classifications, result.Recommendations, result.Detail);
                    }
                }
                catch (OperationCanceledException) when (!hostCt.IsCancellationRequested)
                {
                    srcOutcome = JobRunOutcome.Timeout;
                    srcError   = $"Source exceeded lease duration ({lease.LeaseDurationSeconds}s)";
                    logger.LogWarning("Scan timed out for source '{Source}' of job {JobId}", source.Name, lease.MonitoredJobId);
                }
                catch (Exception ex)
                {
                    srcOutcome = JobRunOutcome.Failed;
                    srcError   = ex.Message;
                    logger.LogError(ex, "Scan failed for source '{Source}' of job {JobId}", source.Name, lease.MonitoredJobId);
                }

                await WriteSourceHistoryAsync(
                    historyRepo, lease.MonitoredJobId, source.ScanSourceId,
                    srcStartedAt, srcOutcome, srcError, result, hostCt);

                // Roll up worst outcome for the lease: Timeout > Failed > Success.
                if (srcOutcome == JobRunOutcome.Timeout) jobOutcome = JobRunOutcome.Timeout;
                else if (srcOutcome == JobRunOutcome.Failed && jobOutcome != JobRunOutcome.Timeout) jobOutcome = JobRunOutcome.Failed;
                if (srcError is not null && jobError is null) jobError = srcError;

                if (srcOutcome == JobRunOutcome.Timeout) break;   // stop-on-timeout
            }
        }
        catch (OperationCanceledException) when (!hostCt.IsCancellationRequested)
        {
            jobOutcome = JobRunOutcome.Timeout;
            jobError ??= $"Job exceeded lease duration ({lease.LeaseDurationSeconds}s)";
            logger.LogWarning("Scan timed out for job {JobId}", lease.MonitoredJobId);
        }
        catch (Exception ex)
        {
            jobOutcome = JobRunOutcome.Failed;
            jobError ??= ex.Message;
            logger.LogError(ex, "Scan failed for job {JobId}", lease.MonitoredJobId);
        }
        finally
        {
            // Release ONCE with the rolled-up outcome. Host token (not jobCts) so the
            // outcome is recorded even after a timeout.
            try
            {
                var stillOurs = await leaseRepo.ReleaseAsync(
                    lease.MonitoredJobId, _leasedBy, jobOutcome,
                    pollingIntervalSeconds, jobError, hostCt);

                if (!stillOurs)
                    logger.LogWarning(
                        "Lease for job {JobId} was stolen before release — results recorded but lease state untouched",
                        lease.MonitoredJobId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to release lease for job {JobId}", lease.MonitoredJobId);
            }
        }
    }

    /// <summary>One ScanRunHistory row per source that ran/attempted. Own try so a
    /// history-write failure never breaks the source loop. result == null means the
    /// source failed before producing counts (no strategy / threw).</summary>
    private async Task WriteSourceHistoryAsync(
        IScanRunHistoryRepository historyRepo, int monitoredJobId, int scanSourceId,
        DateTime startedAt, JobRunOutcome outcome, string? error, ScanResult? result, CancellationToken ct)
    {
        try
        {
            var completedAt = DateTime.Now;
            var durationMs  = (int)Math.Clamp((completedAt - startedAt).TotalMilliseconds, 0, int.MaxValue);
            await historyRepo.SaveAsync(new ScanRunHistory
            {
                MonitoredJobId   = monitoredJobId,
                ScanSourceId     = scanSourceId,
                LeasedBy         = _leasedBy,
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write ScanRunHistory for source {SourceId} of job {JobId}", scanSourceId, monitoredJobId);
        }
    }
}
