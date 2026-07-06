using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

public sealed class ManualFixHandler(ILogger<ManualFixHandler> logger) : IFixHandler
{
    public FixCategory Category => FixCategory.Manual;

    public async Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Failure {FailureId} requires manual intervention — auto-fix skipped",
            recommendation.FailureId);

        await Task.CompletedTask;
        return false;
    }
}
