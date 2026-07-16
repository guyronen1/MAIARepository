using Maia.Core.Entities;

namespace Maia.API.Contracts;

/// <summary>
/// One operator decision (Approve / Reject / Retry) on a recommendation, with
/// enough joined context (recommendation + failure + job) that the history
/// screen can render a self-contained row without extra round-trips.
/// </summary>
public sealed record OperatorActionDto(
    int      ActionId,
    DateTime ActionTimestamp,
    string   OperatorId,
    string   ActionTaken,
    int      RecommendationId,
    string?  SuggestedAction,
    string?  FixCategory,
    // Whether the recommendation ultimately executed — the "what happened
    // after the decision" signal next to the decision itself.
    bool     IsExecuted,
    int?     FailureId,
    string?  ErrorTypeCode,
    string?  MonitoredJobName,
    // The failure's CURRENT status (live, not a snapshot) so the history row
    // shows where the failure ended up (Resolved / ManualRequired / ...).
    string?  FailureStatus)
{
    public static OperatorActionDto From(OperatorAction a) => new(
        a.ActionId,
        a.ActionTimestamp,
        a.OperatorId,
        a.ActionTaken,
        a.RecommendationId,
        a.Recommendation?.SuggestedAction,
        a.Recommendation?.FixCategory.ToString(),
        a.Recommendation?.IsExecuted ?? false,
        a.Recommendation?.FailureId,
        a.Recommendation?.ErrorType?.Code,
        a.Recommendation?.Failure?.MonitoredJob?.Name,
        a.Recommendation?.Failure?.Status.ToString());
}
