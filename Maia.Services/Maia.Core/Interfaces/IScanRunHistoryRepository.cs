using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Results;

namespace Maia.Core.Interfaces;

public interface IScanRunHistoryRepository
{
    Task SaveAsync(ScanRunHistory run, CancellationToken ct = default);

    Task<PagedResult<ScanRunHistory>> GetPagedAsync(
        int?           monitoredJobId,
        int?           scanSourceId,
        JobRunOutcome? outcome,
        DateTime?      fromDate,
        DateTime?      toDate,
        int            page,
        int            pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Bounded DELETE — removes up to <paramref name="batchSize"/> rows whose
    /// <see cref="ScanRunHistory.CompletedAt"/> is older than <paramref name="cutoff"/>.
    /// Returns the number of rows actually deleted; caller loops until 0.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, int batchSize, CancellationToken ct = default);
}
