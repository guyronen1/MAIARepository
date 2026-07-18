using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Microsoft.Extensions.Logging;

namespace Maia.Application.Classification;

/// <summary>
/// See <see cref="IReclassifyOrphanedFailuresUseCase"/>. Reuses the same
/// classify + suggest use cases the scan strategies call, so a recovered failure
/// follows the identical path it would have on a clean scan.
/// </summary>
public sealed class ReclassifyOrphanedFailuresUseCase(
    IJobRepository jobs,
    IClassifyJobsUseCase classify,
    IGenerateSuggestionsUseCase suggest,
    ILogger<ReclassifyOrphanedFailuresUseCase> logger) : IReclassifyOrphanedFailuresUseCase
{
    public async Task<int> ExecuteAsync(TimeSpan minAge, CancellationToken ct = default)
    {
        // DetectedAt is server-local (matches the scan strategies), so the cutoff is too.
        var cutoff  = DateTime.Now - minAge;
        var orphans = await jobs.GetUnclassifiedOlderThanAsync(cutoff, ct);
        if (orphans.Count == 0) return 0;

        logger.LogWarning(
            "Orphaned-unclassified sweep: {Count} failure(s) were saved without classification " +
            "and are older than {Minutes:0} min — a prior scan likely crashed or timed out between " +
            "saving them (watermark already advanced) and classifying. Recovering them now.",
            orphans.Count, minAge.TotalMinutes);

        // Same two steps the strategy runs post-loop. classify skips rows that match no
        // rule (they stay unclassified and are retried next sweep); suggest skips
        // failures that already have a recommendation (idempotent under concurrency).
        var classifications = await classify.ExecuteAsync(orphans, ct);
        await suggest.ExecuteAsync(classifications, ct);

        logger.LogInformation(
            "Orphaned-unclassified sweep: recovered {Classified} of {Total} stranded failure(s).",
            classifications.Count, orphans.Count);

        return classifications.Count;
    }
}
