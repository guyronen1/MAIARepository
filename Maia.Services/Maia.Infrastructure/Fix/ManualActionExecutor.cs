using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

/// <summary>
/// No-op executor for Manual policies — logs that operator action is required and returns false.
/// </summary>
public sealed class ManualActionExecutor(ILogger<ManualActionExecutor> logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.Manual;

    public Task<FixActionResult> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        logger.LogWarning(
            "Failure {FailureId} requires manual operator intervention — automated fix skipped",
            recommendation.FailureId);
        return Task.FromResult(FixActionResult.Fail(
            "Manual policy — no automated action; operator must complete off-system."));
    }
}
