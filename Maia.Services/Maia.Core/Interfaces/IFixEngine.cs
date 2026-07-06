using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

/// <summary>
/// What happened when the fix engine processed a recommendation. Caller
/// (ExecuteFixesUseCase) maps these to the right JobStatus transition:
///
///   Success            → JobStatus.Resolved
///   NoAutomatedAction  → if operator-approved: JobStatus.AwaitingManualAction,
///                        otherwise (auto-heal path): JobStatus.ManualRequired
///   Failed             → JobStatus.ManualRequired
///
/// Distinguishing NoAutomatedAction from Failed lets the audit trail and
/// status pipeline say "operator must perform action off-system" vs "the
/// system tried and the fix didn't work" — two genuinely different states.
/// </summary>
public enum FixOutcome
{
    /// <summary>The executor ran and reported success.</summary>
    Success,
    /// <summary>The matched policy is ActionType=Manual (or the fallback handler
    /// is the Manual category handler) — no automated step exists. Not a failure;
    /// the operator's approval IS the action.</summary>
    NoAutomatedAction,
    /// <summary>An automated step was attempted but did not succeed.</summary>
    Failed,
}

/// <summary>
/// The fix engine's <see cref="FixOutcome"/> plus an optional <see cref="Detail"/>
/// propagated up from the executor (e.g. the SQL error). ExecuteFixesUseCase writes
/// Detail into FixExecutionLog.ResultDetail. The implicit FixOutcome conversion keeps
/// detail-less returns terse (<c>return FixOutcome.Failed;</c> still compiles).
/// </summary>
public readonly record struct FixResult(FixOutcome Outcome, string? Detail = null)
{
    public static implicit operator FixResult(FixOutcome outcome) => new(outcome);
}

/// <summary>
/// Executes a concrete remediation action for a recommendation.
/// Each FixCategory dispatches to a different implementation strategy.
/// </summary>
public interface IFixEngine
{
    Task<FixResult> ExecuteAsync(AiRecommendation recommendation, CancellationToken ct = default);
}
