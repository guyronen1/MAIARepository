using System.Diagnostics;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Maia.Application.Maintenance;

/// <summary>
/// Bounded retention sweep for ScanRunHistory. Reads config every invocation so
/// operators can change settings without restarting the API.
///
/// Config (under <c>"ScanHistory"</c>):
///   • <c>Enabled</c>              bool, default true  — kill switch
///   • <c>RetentionDays</c>        int,  default 30    — rows older than this are deleted
///   • <c>CleanupBatchSize</c>     int,  default 5000  — DELETE TOP (N) per round
///   • <c>InterBatchDelayMs</c>    int,  default 200   — pause between batches to release locks
///   • <c>MaxRowsPerSweep</c>      int,  default 1M    — runaway-loop hard cap
/// </summary>
public sealed class ScanHistoryRetentionService(
    IScanRunHistoryRepository                history,
    IConfiguration                           config,
    ILogger<ScanHistoryRetentionService>     logger) : IScanHistoryRetentionService
{
    public async Task<RetentionSweepResult> SweepAsync(CancellationToken ct = default)
    {
        var startedAt = DateTime.Now;
        var sw        = Stopwatch.StartNew();

        var enabled       = config.GetValue("ScanHistory:Enabled",          defaultValue: true);
        var retentionDays = config.GetValue("ScanHistory:RetentionDays",    defaultValue: 30);
        var batchSize     = config.GetValue("ScanHistory:CleanupBatchSize", defaultValue: 5000);
        var delayMs       = config.GetValue("ScanHistory:InterBatchDelayMs",defaultValue: 200);
        var maxPerSweep   = config.GetValue("ScanHistory:MaxRowsPerSweep",  defaultValue: 1_000_000);

        if (!enabled)
        {
            logger.LogInformation(
                "ScanHistoryRetention sweep skipped: cutoff=n/a deleted=0 durationMs={Ms} reason=disabled",
                sw.ElapsedMilliseconds);
            return new RetentionSweepResult(0, (int)sw.ElapsedMilliseconds, startedAt, Skipped: true);
        }

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var total  = 0;

        while (total < maxPerSweep)
        {
            var deleted = await history.DeleteOlderThanAsync(cutoff, batchSize, ct);
            if (deleted == 0) break;
            total += deleted;
            if (delayMs > 0)
                await Task.Delay(delayMs, ct);
        }

        sw.Stop();
        var durationMs = (int)sw.ElapsedMilliseconds;

        logger.LogInformation(
            "ScanHistoryRetention sweep complete: cutoff={Cutoff:yyyy-MM-dd HH:mm:ss} deleted={Rows} durationMs={Ms}",
            cutoff, total, durationMs);

        return new RetentionSweepResult(total, durationMs, cutoff, Skipped: false);
    }
}
