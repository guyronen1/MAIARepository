using System.Text.RegularExpressions;
using Maia.Core.Analysis;

namespace Maia.Infrastructure.Analysis;

/// <summary>
/// v1 cluster analyzer: normalize each message, count n-grams (n=2..5) by the
/// number of distinct failures they appear in, then greedily pick the
/// highest-value n-gram (frequency × length), claim the failures it covers,
/// and repeat over the still-unclaimed failures. The greedy set-cover gives
/// non-overlapping clusters (so "Task failed" and "ERROR Task failed" don't
/// both surface for the same failures) and naturally leaves single-occurrence
/// noise unclustered.
///
/// Deliberately simple — grep-and-count, no ML/embeddings (see CLAUDE.md). The
/// <see cref="IUnconfiguredClusterAnalyzer"/> seam lets v2 swap a smarter
/// implementation in without touching callers.
/// </summary>
public sealed class NgramClusterAnalyzer : IUnconfiguredClusterAnalyzer
{
    private const int MinN = 2;
    // Up to 7 so a phrase like "package execution completed with errors" (6
    // tokens) surfaces whole rather than truncating at the window edge. Score
    // is df × n, so a longer gram only wins when enough failures share it —
    // short high-frequency grams (e.g. "error task failed") still dominate.
    private const int MaxN = 7;
    private const int MinFailuresPerCluster = 2;   // single-occurrence = noise → uncategorized
    private const int MaxClusters = 10;
    private const int SampleSize = 3;

    private static readonly Regex TokenSplit = new(@"[^a-z0-9_]+", RegexOptions.Compiled);

    public string AnalyzerVersion => "ngram-v1";

    public Task<IReadOnlyList<UnclassifiedCluster>> AnalyzeUnclassifiedAsync(
        IReadOnlyList<UnclassifiedFailure> failures,
        CancellationToken ct = default)
    {
        var byId = failures
            .GroupBy(f => f.FailureId)
            .ToDictionary(g => g.Key, g => g.First().Message);

        // n-gram → set of distinct failure ids containing it (document frequency).
        var df  = new Dictionary<string, HashSet<int>>();
        var len = new Dictionary<string, int>();   // token count of each n-gram

        foreach (var f in failures)
        {
            var tokens = Tokenize(MessageNormalizer.Normalize(f.Message));
            if (tokens.Count < MinN) continue;

            var seenHere = new HashSet<string>();  // count each n-gram once per failure
            for (var n = MinN; n <= MaxN; n++)
            {
                for (var i = 0; i + n <= tokens.Count; i++)
                {
                    var gram = string.Join(' ', tokens.GetRange(i, n));
                    if (!seenHere.Add(gram)) continue;
                    if (!df.TryGetValue(gram, out var set)) { set = []; df[gram] = set; len[gram] = n; }
                    set.Add(f.FailureId);
                }
            }
        }

        // Greedy set-cover: repeatedly take the n-gram covering the most still-
        // unclaimed failures (weighted by length so longer/more-specific grams
        // win ties), claim them, until nothing clears the floor or we hit the cap.
        var claimed  = new HashSet<int>();
        var clusters = new List<UnclassifiedCluster>();

        while (clusters.Count < MaxClusters)
        {
            string? best = null;
            int bestScore = 0, bestN = 0, bestCount = 0;

            foreach (var (gram, ids) in df)
            {
                var count = ids.Count(id => !claimed.Contains(id));
                if (count < MinFailuresPerCluster) continue;

                var score = count * len[gram];
                // Prefer higher score, then longer n-gram, then larger raw count.
                if (score > bestScore
                    || (score == bestScore && len[gram] > bestN)
                    || (score == bestScore && len[gram] == bestN && count > bestCount))
                {
                    best = gram; bestScore = score; bestN = len[gram]; bestCount = count;
                }
            }

            if (best is null) break;

            var members = df[best].Where(id => !claimed.Contains(id)).OrderBy(id => id).ToList();
            foreach (var id in members) claimed.Add(id);

            var sampleIds = members.Take(SampleSize).ToList();
            var sampleMessages = sampleIds.Select(id => byId.TryGetValue(id, out var m) ? m : "").ToList();
            clusters.Add(new UnclassifiedCluster(
                SuggestedPattern: best,
                // Normalized form of the representative message, so the UI can
                // show "(normalized from …)" — what the analyzer actually
                // clustered on, with the prefix/timestamp/ids masked.
                NormalizedSample: sampleMessages.Count > 0 ? MessageNormalizer.Normalize(sampleMessages[0]) : "",
                FailureCount:     members.Count,
                SampleFailureIds: sampleIds,
                SampleMessages:   sampleMessages,
                AnalyzerVersion:  AnalyzerVersion,
                SuggestedFromHash: ClusterHash.Of(sampleIds),
                ConfidenceScore:  null));
        }

        return Task.FromResult<IReadOnlyList<UnclassifiedCluster>>(clusters);
    }

    /// <summary>
    /// Lowercase, drop the &lt;NUM&gt; / &lt;GUID&gt; placeholders (so they
    /// never form n-grams), split on punctuation/whitespace, and keep only
    /// tokens that have a letter and length ≥ 2. The letter requirement drops
    /// stray short numerics left by a malformed timestamp; keeping <c>_</c>
    /// preserves identifiers like <c>DTS_E_OLEDBERROR</c>.
    /// </summary>
    private static List<string> Tokenize(string normalized)
    {
        var cleaned = normalized
            .Replace("<NUM>", " ")
            .Replace("<GUID>", " ")
            .ToLowerInvariant();

        return TokenSplit.Split(cleaned)
            .Where(t => t.Length >= 2 && t.Any(char.IsLetter))
            .ToList();
    }
}
