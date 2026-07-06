using Maia.Core.Entities;
using Maia.Core.Enums;

namespace Maia.Core.Interfaces;

/// <summary>
/// Outcome of a single fix-action executor: a success flag plus an optional
/// human-facing <see cref="Detail"/> (e.g. a SQL error message, "0 rows affected")
/// that flows up into FixExecutionLog.ResultDetail — so an operator can see WHY a
/// fix failed from the failure drawer instead of digging through server logs.
///
/// An implicit bool conversion keeps simple paths terse (<c>return true;</c> /
/// <c>return false;</c> leave Detail null → the caller falls back to a generic
/// message); use <see cref="Fail"/> to attach the real reason.
/// </summary>
public readonly record struct FixActionResult(bool Success, string? Detail = null)
{
    public static FixActionResult Ok(string? detail = null) => new(true, detail);
    public static FixActionResult Fail(string detail)       => new(false, detail);
    public static implicit operator FixActionResult(bool success) => new(success);
}

/// <summary>
/// Executes a specific type of automated fix action.
/// One implementation per FixActionType; DefaultFixEngine dispatches via IFixPolicyRepository.
/// </summary>
public interface IFixActionExecutor
{
    FixActionType ActionType { get; }

    /// <param name="payload">The action payload from FixPolicyRule (URL, SP name, script command).</param>
    /// <param name="recommendation">The recommendation being executed; provides context (FailureId, ErrorTypeId).</param>
    Task<FixActionResult> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default);
}
