namespace Maia.Core.Analysis;

/// <summary>
/// One unclassified failure fed to a cluster analyzer. Kept deliberately
/// minimal (id + raw message) so analyzers are pure functions of their input
/// — no DB dependency, trivially unit-testable.
/// </summary>
public sealed record UnclassifiedFailure(int FailureId, string Message);

/// <summary>
/// A suggested grouping of unclassified failures. <see cref="SuggestedPattern"/>
/// is a candidate <c>ClassificationRule</c> pattern (case-insensitive substring;
/// the operator reviews/edits before saving). <see cref="ConfidenceScore"/> is
/// null for v1 (the n-gram analyzer does not score — faking a number would
/// mislead); v2 analyzers (embedding/LLM) populate it.
/// </summary>
public sealed record UnclassifiedCluster(
    string SuggestedPattern,
    string NormalizedSample,
    int FailureCount,
    IReadOnlyList<int> SampleFailureIds,
    IReadOnlyList<string> SampleMessages,
    string AnalyzerVersion,
    string SuggestedFromHash,
    double? ConfidenceScore);

/// <summary>
/// Groups unclassified failures into suggested classification patterns.
/// V1 implementation is <c>NgramClusterAnalyzer</c> (frequency over normalized
/// n-grams). The interface is the v2 seam: embedding/LLM analyzers register
/// alongside and the controller swaps them via DI — same pattern as
/// <c>IFixActionExecutor</c> / <c>IScanStrategy</c>. <see cref="AnalyzerVersion"/>
/// is recorded as rule provenance when an operator accepts a suggestion.
/// </summary>
public interface IUnconfiguredClusterAnalyzer
{
    /// <summary>Stable identifier of this analyzer + version, e.g. "ngram-v1".</summary>
    string AnalyzerVersion { get; }

    /// <summary>
    /// Cluster the given unclassified failures into suggested patterns,
    /// highest-value first, capped to a sensible number of clusters. Failures
    /// that don't fall into a multi-occurrence cluster are simply omitted
    /// (the caller treats them as "uncategorized" = total − clustered).
    /// </summary>
    Task<IReadOnlyList<UnclassifiedCluster>> AnalyzeUnclassifiedAsync(
        IReadOnlyList<UnclassifiedFailure> failures,
        CancellationToken ct = default);
}
