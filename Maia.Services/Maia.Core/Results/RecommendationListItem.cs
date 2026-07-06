using Maia.Core.Entities;

namespace Maia.Core.Results;

/// <summary>
/// A recommendation enriched with a snapshot of its current matching FixPolicyRule
/// (looked up at query time, NOT a stored FK).
///
/// <para><c>FixPolicyRuleId</c> / <c>PolicyIsAutoHealEligible</c> are both <c>null</c>
/// when no enabled policy matches — whether because no row exists at all, or because
/// the row exists with <c>Enabled = false</c>. Callers must treat both cases the same.</para>
///
/// <para>TODO (separate task): the policy lookup currently ignores JobTypeId
/// (matches today's execution path in DefaultFixEngine/SqlFixPolicyRepository).
/// When that bug is fixed, this projection must change in lockstep.</para>
/// </summary>
public sealed record RecommendationListItem(
    AiRecommendation Recommendation,
    int?             FixPolicyRuleId,
    bool?            PolicyIsAutoHealEligible,
    int              PolicyStepCount,
    // ActionType from the winning FixPolicyRule — the field the drawer reads
    // to decide "Approve" vs "Acknowledge" (not FixCategory, which is intent,
    // not mechanism). Null when no enabled policy matches.
    string?          PolicyActionType);

// PolicyStepCount is 0 for non-composite policies AND when no policy matches.
// Non-zero → UI renders a "Composite (N steps)" badge on the rec card and
// expands the step list via the existing getFixPolicyRuleById endpoint
// (which now eager-loads Steps too). Lazy expand keeps the list payload small.
