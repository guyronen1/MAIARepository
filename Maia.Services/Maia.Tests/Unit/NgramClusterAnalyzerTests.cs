using Maia.Core.Analysis;
using Maia.Infrastructure.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Maia.Tests.Unit;

public class NgramClusterAnalyzerTests(ITestOutputHelper output)
{
    private readonly NgramClusterAnalyzer _sut = new();

    // The actual unclassified failures pulled from the dev DB (2026-06-06).
    private static IReadOnlyList<UnclassifiedFailure> RealSample() =>
    [
        new(59,  "[Error] app-20260503.log: 2026-05-01 12:00:05] ERROR Task failed: Data Flow Task  55555"),
        new(61,  "[Error] app-20260505.log: 5-01 17:00:05] ERROR Task failed: VERIFY4-214119-55555"),
        new(64,  "[Error] app-20260505.log: [2026-05-01 22:00:06] INFO  Package execution completed with errors."),
        new(68,  "[Error] app-202605025.log: [2026-05-01 08:00:05] ERROR Task failed: Data Flow Task"),
        new(69,  "[Error] app-202605025.log: [2026-05-01 08:00:06] INFO  Package execution completed with errors."),
        new(77,  "[Error] app-20260505.log: [2026-05-01 18:00:05] ERROR Task failed: DASHBOARD-VERIFY-111519"),
        new(82,  "[Error] app-20260528.log: [2026-05-01 08:00:06] INFO  Package execution completed with errors."),
        new(84,  "[Error] app-20260528.log: [2026-05-01 10:00:06] INFO  Package execution completed with errors."),
        new(85,  "[Error] app-20260528.log: [2026-05-01 12:00:05] ERROR Task failed: VERIFY-212920-3333"),
        new(86,  "[Error] app-20260528.log: [2026-05-01 14:00:05] ERROR Task failed: VERIFY2-213232-4444"),
        new(87,  "[Error] app-20260528.log: [2026-05-01 15:00:05] ERROR Task failed: VERIFY3-213500-5555"),
        new(88,  "[Error] app-20260528.log: [2026-05-01 16:00:05] ERROR Task failed: VERIFY4-214119-9999"),
        new(89,  "[Error] app-20260528.log: [2026-05-01 17:00:05] ERROR Task failed: VERIFY4-214119-55555"),
        new(91,  "[Error] app-20260528.log: [2026-05-01 22:00:06] INFO  Package execution completed with errors."),
        new(92,  "[Error] app-20260528.log: [2026-05-01 18:00:05] ERROR Task failed: DASHBOARD-VERIFY-111519"),
        new(96,  "[Error] app-20260528.log: [2026-05-01 22:00:06] INFO  Package execution completed with errors."),
        new(97,  "[Error] app-20260528.log: [2026-05-01 18:00:05] ERROR Task failed: DASHBOARD-VERIFY-111519"),
        new(165, "[dbo.Files].[NumberOfEntities] = 0 is outside range [1.0000, 999999.0000] (Id=6252C121-AAAA-4433-B79A-0008C7821615) - Order amount must be positive and below 1M"),
        new(187, "[Exception] InputFiles-20260501.txt: [2026-05-01 08:00:05] ERROR FileNotFoundException File Not Found"),
        new(595, "[Error] app-202605026.log: [2026-05-01 10:00:05] ERROR DTS_E_OLEDBERROR  An OLE DB error has occurred. Error code: 0x80004005."),
        new(1617,"[Exception] app-20260601.log: [2026-05-01 11:00:05] Exception : file extracted failed"),
    ];

    [Fact]
    public async Task RealSample_ProducesCleanClusters_PrintsForReview()
    {
        var sample   = RealSample();
        var clusters = await _sut.AnalyzeUnclassifiedAsync(sample);

        var clustered = clusters.Sum(c => c.FailureCount);
        output.WriteLine($"=== {sample.Count} unclassified failures → {clusters.Count} clusters " +
                         $"({clustered} clustered, {sample.Count - clustered} uncategorized) ===");
        foreach (var c in clusters)
        {
            output.WriteLine($"\n[{c.FailureCount}x] pattern: \"{c.SuggestedPattern}\"  (analyzer={c.AnalyzerVersion}, conf={c.ConfidenceScore?.ToString() ?? "null"})");
            output.WriteLine($"      sample ids: {string.Join(", ", c.SampleFailureIds)}");
            foreach (var m in c.SampleMessages) output.WriteLine($"        · {m}");
        }

        // Every cluster meets the floor; analyzer stamps version + null confidence.
        Assert.All(clusters, c =>
        {
            Assert.True(c.FailureCount >= 2);
            Assert.Equal("ngram-v1", c.AnalyzerVersion);
            Assert.Null(c.ConfidenceScore);
        });
        // The two dominant patterns should surface.
        Assert.Contains(clusters, c => c.SuggestedPattern.Contains("task failed"));
        Assert.Contains(clusters, c => c.SuggestedPattern.Contains("completed with errors"));
        // Set-cover: no failure appears in two clusters.
        var all = clusters.SelectMany(c => c.SampleFailureIds).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public async Task SingleOccurrenceMessages_ProduceNoClusters()
    {
        var clusters = await _sut.AnalyzeUnclassifiedAsync(
        [
            new(1, "unique alpha message"),
            new(2, "different beta content"),
            new(3, "third gamma entirely"),
        ]);
        Assert.Empty(clusters);   // nothing repeats → all uncategorized
    }

    [Fact]
    public async Task SharedPhrase_FormsOneCluster()
    {
        var clusters = await _sut.AnalyzeUnclassifiedAsync(
        [
            new(1, "connection pool timed out badly"),
            new(2, "connection pool timed out again"),
            new(3, "connection pool timed out here"),
        ]);
        var c = Assert.Single(clusters);
        Assert.Equal(3, c.FailureCount);
        Assert.Contains("connection pool timed out", c.SuggestedPattern);
    }

    [Fact]
    public async Task OverlappingNgrams_DoNotDoubleCountFailures()
    {
        // Both messages share "alpha beta gamma delta"; the longer gram should
        // claim them once — not also surface "alpha beta" as a second cluster.
        var clusters = await _sut.AnalyzeUnclassifiedAsync(
        [
            new(1, "alpha beta gamma delta one"),
            new(2, "alpha beta gamma delta two"),
        ]);
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].FailureCount);
    }

    [Fact]
    public async Task EmptyInput_ReturnsNoClusters()
        => Assert.Empty(await _sut.AnalyzeUnclassifiedAsync([]));
}
