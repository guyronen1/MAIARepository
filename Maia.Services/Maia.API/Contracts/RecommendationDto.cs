using Maia.Core.Entities;
using Maia.Core.Results;

namespace Maia.API.Contracts;

public sealed record RecommendationDto(
    int      RecommendationId,
    int      FailureId,
    string   SuggestedAction,
    string   FixCategory,
    decimal  ConfidenceScore,
    string?  Explanation,
    bool     AutoFixAvailable,
    bool?    OperatorApproved,
    bool     IsExecuted,
    DateTime RecommendedAt,
    string?  ErrorTypeCode,
    int      ErrorTypeId,
    int?     JobTypeId,
    int?     FixPolicyRuleId,
    bool?    PolicyIsAutoHealEligible,
    int      PolicyStepCount,
    string?  PolicyActionType)
{
    public static RecommendationDto From(
        AiRecommendation r,
        int?             fixPolicyRuleId          = null,
        bool?            policyIsAutoHealEligible = null,
        int              policyStepCount          = 0,
        string?          policyActionType         = null) => new(
        r.RecommendationId,
        r.FailureId,
        r.SuggestedAction,
        r.FixCategory.ToString(),
        r.ConfidenceScore,
        r.Explanation,
        r.AutoFixAvailable,
        r.OperatorApproved,
        r.IsExecuted,
        r.RecommendedAt,
        r.ErrorType?.Code,
        r.ErrorTypeId,
        r.Failure?.JobTypeId,
        fixPolicyRuleId,
        policyIsAutoHealEligible,
        policyStepCount,
        policyActionType);

    public static RecommendationDto From(RecommendationListItem item) =>
        From(item.Recommendation, item.FixPolicyRuleId, item.PolicyIsAutoHealEligible,
             item.PolicyStepCount, item.PolicyActionType);
}
