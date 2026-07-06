namespace Maia.Core.Entities;

public class OperatorAction
{
    public int ActionId { get; set; }
    public int RecommendationId { get; set; }
    public required string OperatorId { get; set; }
    public required string ActionTaken { get; set; }
    public DateTime ActionTimestamp { get; set; }

    public AiRecommendation? Recommendation { get; set; }
}
