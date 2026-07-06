using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Microsoft.Extensions.Logging;

namespace Maia.Application.Remediation;

public sealed class GenerateSuggestionsUseCase(
    IRecommendationRepository recommendations,
    IFixCatalogue catalogue,
    ILogger<GenerateSuggestionsUseCase> logger) : IGenerateSuggestionsUseCase
{
    private static readonly FixCatalogueEntry DefaultEntry =
        new("Manual investigation required.", FixCategory.Manual, -0.2, false);

    public async Task ExecuteAsync(
        IEnumerable<ClassificationResult> results,
        CancellationToken ct = default)
    {
        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();

            if (await recommendations.ExistsForFailureAsync(result.FailureId, ct))
            {
                logger.LogDebug(
                    "Recommendation already exists for Failure {FailureId} — skipping",
                    result.FailureId);
                continue;
            }

            // Pass MonitoredJobId so the catalogue picks a per-job override
            // (when configured) instead of the JobType-level default. The
            // resulting rec's frozen AutoFixAvailable snapshot reflects the
            // policy that will actually execute, not the JobType fallback.
            var entry = await catalogue.GetEntryAsync(
                result.ErrorTypeCode, result.JobTypeId, result.MonitoredJobId, ct) ?? DefaultEntry;

            var recommendation = new AiRecommendation
            {
                FailureId       = result.FailureId,
                ErrorTypeId     = result.ErrorTypeId,
                SuggestedAction = entry.SuggestedAction,
                FixCategory     = entry.Category,
                ConfidenceScore = (decimal)Math.Clamp(result.Confidence + entry.ConfidenceBoost, 0, 1),
                Explanation     = $"Detected: {result.RawError}",
                AutoFixAvailable = entry.AutoHeal,
                RecommendedAt   = DateTime.Now,
            };

            await recommendations.SaveAsync(recommendation, ct);

            logger.LogInformation(
                "Suggestion saved for Failure {FailureId}: [{Category}] {Action}",
                result.FailureId, entry.Category, entry.SuggestedAction);
        }
    }
}
