using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Results;

namespace Maia.Core.Interfaces;

public interface IJobRepository
{
    Task<List<JobFailure>> GetByStatusAsync(JobStatus status, CancellationToken ct = default);
    Task<List<JobFailure>> GetUnclassifiedAsync(CancellationToken ct = default);
    Task<JobFailure?> GetByIdAsync(int failureId, CancellationToken ct = default);
    Task<JobFailure> SaveAsync(JobFailure job, CancellationToken ct = default);
    Task UpdateStatusAsync(int failureId, JobStatus status, CancellationToken ct = default);
    Task UpdateClassificationAsync(int failureId, ClassificationResult result, CancellationToken ct = default);
    /// <summary>
    /// Paged listing of failures. <paramref name="view"/> filters server-side:
    /// <c>active</c>, <c>unclassified</c>, <c>awaiting-action</c>, <c>auto-fixed</c>,
    /// <c>operator-fixed</c>, <c>resolved</c>, <c>manual-required</c>, <c>fix-failed</c>
    /// (Status=ManualRequired AND has a Success=false FixExecutionLog since
    /// today-midnight). Null/empty/unknown → unfiltered.
    /// </summary>
    /// <param name="sort">Sort key (whitelisted): id | job | errortype | detected | status.
    ///   Null/unknown → detected. <paramref name="dir"/> = "asc" | "desc" (default desc).</param>
    Task<PagedResult<JobFailure>> GetPagedAsync(int page, int pageSize, string? view = null, string? sort = null, string? dir = null, CancellationToken ct = default);

    /// <summary>
    /// Given a set of <c>FailureId</c>s, return the subset that has at least
    /// one <see cref="Entities.FixExecutionLog"/> row with <c>Success = false</c>
    /// since <paramref name="since"/> (typically server-local midnight).
    /// Batched: one query for the whole page, not one per row. Used by the
    /// failures-list endpoint to flag "Failed to Execute" rows.
    /// </summary>
    Task<HashSet<int>> GetIdsWithRecentFixFailureAsync(
        IReadOnlyCollection<int> failureIds, DateTime since, CancellationToken ct = default);

    /// <summary>
    /// Returns true when a non-resolved failure already exists for this job/table/column combo,
    /// so database scans don't create duplicate failures for persistent data issues.
    /// </summary>
    Task<bool> HasOpenFailureAsync(int monitoredJobId, string sourceTable, string targetField, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of SourceIds that currently have a non-resolved failure for this
    /// (job, StepName) — the per-row dedup key for SqlQuery scans. Lets a new row fire
    /// while an unrelated row's failure is still open (unlike the coarse per-rule
    /// <see cref="HasOpenFailureAsync"/>). Case-insensitive: source GUIDs round-trip in
    /// different cases. One batched query, not one per row.
    /// </summary>
    Task<HashSet<string>> GetOpenFailureSourceIdsAsync(int monitoredJobId, string stepName, CancellationToken ct = default);
}
