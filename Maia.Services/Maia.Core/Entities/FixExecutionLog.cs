using Maia.Core.Enums;

namespace Maia.Core.Entities;

public class FixExecutionLog
{
    public int FixId { get; set; }
    public int FailureId { get; set; }
    public int RecommendationId { get; set; }
    public required string ExecutedAction { get; set; }
    public TriggerType TriggerType { get; set; }
    public required string ExecutedBy { get; set; }
    public DateTime ExecutedAt { get; set; }
    public bool Success { get; set; }
    public string? ResultDetail { get; set; }

    public JobFailure? Failure { get; set; }
    public AiRecommendation? Recommendation { get; set; }
}
