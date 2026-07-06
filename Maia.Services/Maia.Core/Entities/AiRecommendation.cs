using Maia.Core.Enums;

namespace Maia.Core.Entities;

public class AiRecommendation
{
    public int RecommendationId { get; set; }
    public int FailureId { get; set; }
    public int ErrorTypeId { get; set; }
    public required string SuggestedAction { get; set; }
    public FixCategory FixCategory { get; set; }
    public decimal ConfidenceScore { get; set; }
    public string? Explanation { get; set; }
    public DateTime RecommendedAt { get; set; }
    public bool AutoFixAvailable { get; set; }
    public bool? OperatorApproved { get; set; }
    public bool IsExecuted { get; set; }

    /// <summary>
    /// Atomic-claim columns to prevent concurrent drains (worker tick + approve
    /// endpoint + manual /execute-fixes) from double-executing the same
    /// recommendation. ClaimedBy is the owner identifier (host;pid;runId,
    /// matches lease shape); ClaimedAt is the lease start. Claims older than
    /// <c>ClaimTimeoutMinutes</c> are stealable so a crashed worker doesn't
    /// strand recs forever. Cleared by MarkExecutedAsync (success) and
    /// ReleaseClaimAsync (failure → eligible for retry after timeout).
    /// </summary>
    public string?  ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }

    public JobFailure? Failure { get; set; }
    public ErrorType? ErrorType { get; set; }
    public ICollection<OperatorAction> OperatorActions { get; set; } = [];
}
