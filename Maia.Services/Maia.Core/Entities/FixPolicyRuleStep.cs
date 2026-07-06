using Maia.Core.Enums;

namespace Maia.Core.Entities;

/// <summary>
/// One ordered step within a Composite FixPolicyRule. Each step is dispatched
/// to the matching IFixActionExecutor (SqlScript / Script / CopyFile / etc.)
/// in StepOrder ascending. Best-effort execution: any step's failure routes
/// the recommendation's failure to ManualRequired, but remaining steps still
/// run so partial recovery is maximised.
///
/// Constraints (validated at the controller + DB):
///   - ActionType cannot be Manual or Composite (no Manual steps inside a
///     composite — the composite IS automated by definition; no nesting).
///   - ActionPayload is required (single-action policies allow null payloads
///     for Manual; composite steps never do).
///   - (RuleId, StepOrder) is unique — see UX_FixPolicyRuleSteps_RuleId_StepOrder.
/// </summary>
public class FixPolicyRuleStep
{
    public int StepId { get; set; }
    public int RuleId { get; set; }

    /// <summary>1-based step order within the rule; lookup orders by this asc.</summary>
    public int StepOrder { get; set; }

    public FixActionType ActionType { get; set; }

    /// <summary>Executor payload; placeholder resolver substitutes {failureId},
    /// {sourceId}, {sourceFilePath}, {sourceLogPath}, {jobFolder}, {inputFolder}.</summary>
    public required string ActionPayload { get; set; }

    /// <summary>Operator-facing label shown in the rec card step list + audit.</summary>
    public string? Description { get; set; }

    public FixPolicyRule? Rule { get; set; }
}
