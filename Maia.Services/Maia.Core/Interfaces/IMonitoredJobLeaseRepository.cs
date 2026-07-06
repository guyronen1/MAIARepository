using Maia.Core.Enums;

namespace Maia.Core.Interfaces;

/// <summary>
/// Per-job lease coordination so multiple worker instances can run side-by-side
/// without racing on the same MonitoredJob. The claim is a single atomic
/// UPDATE TOP (N) ... OUTPUT inserted.* over MonitoredJobLeases.
/// </summary>
public interface IMonitoredJobLeaseRepository
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> eligible jobs for this worker.
    /// Eligibility: MonitoredJob.IsActive AND NextEligibleAt &lt;= now AND (LeasedUntil IS NULL OR LeasedUntil &lt; now).
    /// Each returned <see cref="ClaimedJobLease"/> includes the lease-duration the claim was granted for.
    /// </summary>
    Task<IReadOnlyList<ClaimedJobLease>> ClaimAsync(
        string leasedBy, int batchSize, CancellationToken ct);

    /// <summary>
    /// Records the outcome and sets NextEligibleAt = now + pollingIntervalSeconds.
    /// Returns false if the caller no longer owns the lease (it was stolen) — in that
    /// case the row is left untouched.
    /// </summary>
    Task<bool> ReleaseAsync(
        int monitoredJobId,
        string leasedBy,
        JobRunOutcome outcome,
        int nextPollingIntervalSeconds,
        string? error,
        CancellationToken ct);

    /// <summary>
    /// Extends LeasedUntil by <paramref name="extendSeconds"/> if the caller still owns the lease.
    /// Returns false if the lease was stolen and the caller should abort. Optional — only needed
    /// when a single scan can legitimately exceed its lease duration.
    /// </summary>
    Task<bool> HeartbeatAsync(
        int monitoredJobId, string leasedBy, int extendSeconds, CancellationToken ct);

    /// <summary>
    /// Returns the subset of <paramref name="jobIds"/> that currently hold an active lease
    /// (LeasedUntil &gt; now). Used by manual scan triggers to skip jobs already being scanned
    /// by the background worker or another manual request, preventing duplicate failures.
    /// </summary>
    Task<IReadOnlySet<int>> GetActivelyLeasedJobIdsAsync(
        IEnumerable<int> jobIds, CancellationToken ct);
}

/// <summary>Result of a claim: the job to scan plus how long the lease is good for.</summary>
public sealed record ClaimedJobLease(
    int MonitoredJobId,
    int LeaseDurationSeconds,
    DateTime LeasedUntil);
