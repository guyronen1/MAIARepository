using Maia.Core.Results;

namespace Maia.Core.Interfaces;

/// <summary>
/// Single sweep of the ScanRunHistory retention policy. Called by the
/// background worker on a schedule, and by POST /api/admin/scan-history/cleanup
/// for on-demand operator use. Idempotent — running it twice in a row just
/// finds nothing to delete the second time.
/// </summary>
public interface IScanHistoryRetentionService
{
    Task<RetentionSweepResult> SweepAsync(CancellationToken ct = default);
}
