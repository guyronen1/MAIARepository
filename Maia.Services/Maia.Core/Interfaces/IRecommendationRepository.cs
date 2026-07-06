using Maia.Core.Entities;
using Maia.Core.Results;

namespace Maia.Core.Interfaces;

public interface IRecommendationRepository
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> pending recommendations
    /// for the caller, returning the claimed rows with their <c>Failure</c> nav
    /// pre-loaded. Eligibility:
    ///   <list type="bullet">
    ///     <item><c>!IsExecuted</c></item>
    ///     <item><c>OperatorApproved == true</c> OR <c>AutoFixAvailable</c></item>
    ///     <item><c>Failure.Status == Failed</c> — once the failure transitions to
    ///       <c>ManualRequired</c> / <c>AwaitingManualAction</c> / <c>Resolved</c>,
    ///       the rec is no longer eligible. Closes an existing bug where a failed
    ///       executor left the rec !IsExecuted and the next drain re-ran it
    ///       indefinitely.</item>
    ///     <item>Unclaimed (<c>ClaimedBy IS NULL</c>) OR claim is stale
    ///       (<c>ClaimedAt &lt; now - claimTimeout</c>) — stolen-claim semantics
    ///       so a crashed worker doesn't strand recs forever.</item>
    ///   </list>
    /// Atomic via <c>UPDATE TOP(N) ... OUTPUT inserted.* WITH (READPAST, UPDLOCK,
    /// ROWLOCK)</c> — same pattern as <see cref="IMonitoredJobLeaseRepository"/>.
    /// Concurrent drains see disjoint sets of recs; no double-execution.
    /// </summary>
    Task<List<AiRecommendation>> ClaimPendingAsync(
        string claimedBy, int batchSize, TimeSpan claimTimeout, CancellationToken ct = default);

    /// <summary>
    /// Clears the claim on a recommendation so it can be re-claimed by a later
    /// drain. Called when the executor fails — the rec stays <c>!IsExecuted</c>
    /// but unclaimed, so after the claim timeout it's eligible for retry
    /// (unless the failure transitioned away from <c>Status=Failed</c>, in
    /// which case the claim eligibility filter excludes it anyway).
    /// </summary>
    Task ReleaseClaimAsync(int recommendationId, CancellationToken ct = default);

    Task SaveAsync(AiRecommendation recommendation, CancellationToken ct = default);

    /// <summary>
    /// Marks the recommendation as executed AND clears any active claim.
    /// Called on the success path (and on operator-approved-manual which the
    /// use case treats as "actioned, stop offering it").
    /// </summary>
    Task MarkExecutedAsync(int recommendationId, CancellationToken ct = default);

    Task<PagedResult<RecommendationListItem>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);
    Task<bool> ExistsForFailureAsync(int failureId, CancellationToken ct = default);

    /// <summary>
    /// Sets the operator decision on a recommendation. <c>true</c> = approved (eligible for
    /// execution by <see cref="UseCases.IExecuteFixesUseCase"/>), <c>false</c> = rejected.
    /// Returns <c>true</c> if a row was updated, <c>false</c> if no recommendation exists with that id.
    /// </summary>
    Task<bool> SetApprovalAsync(int recommendationId, bool approved, CancellationToken ct = default);
}
