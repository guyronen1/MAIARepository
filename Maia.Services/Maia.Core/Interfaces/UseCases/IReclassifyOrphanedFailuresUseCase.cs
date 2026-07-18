namespace Maia.Core.Interfaces.UseCases;

/// <summary>
/// Safety-net sweep that recovers "orphaned-unclassified" failures: rows saved
/// with <c>Status=Failed</c> and no <c>ErrorTypeId</c> that the scan strategy never
/// classified because a crash/timeout hit between saving them (and advancing the
/// watermark past them) and the post-loop classify/suggest step. Because the
/// watermark has moved on, no future scan re-reads them — without this sweep they
/// stay unclassified, un-suggested, and invisible to the pipeline forever.
///
/// Classifies each stranded failure and generates its suggestion, exactly as the
/// scan strategy would have. Idempotent and concurrency-safe: re-classifying sets
/// the same <c>ErrorTypeId</c>, and suggestion generation skips failures that
/// already have a recommendation — so overlapping with an in-flight scan can't
/// create duplicates.
/// </summary>
public interface IReclassifyOrphanedFailuresUseCase
{
    /// <summary>
    /// Sweep failures detected longer ago than <paramref name="minAge"/> (the age
    /// gate excludes rows a scan just created and is about to classify). Returns the
    /// number of failures classified this pass (0 when none were stranded, or when
    /// the stranded rows match no classification rule).
    /// </summary>
    Task<int> ExecuteAsync(TimeSpan minAge, CancellationToken ct = default);
}
