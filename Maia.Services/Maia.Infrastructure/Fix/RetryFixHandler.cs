using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

public sealed class RetryFixHandler(ILogger<RetryFixHandler> logger) : IFixHandler
{
    public FixCategory Category => FixCategory.Retry;

    public async Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Retrying Failure {FailureId} — action: {Action}",
            recommendation.FailureId, recommendation.SuggestedAction);

        // TODO: trigger actual job-retry mechanism (DTSX re-run, SQL Agent retry, etc.)
        await Task.CompletedTask;
        return true;
    }
}
