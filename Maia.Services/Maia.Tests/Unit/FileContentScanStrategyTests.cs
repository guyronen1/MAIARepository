using System.Text;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Integration tests for FileContentScanStrategy with the REAL XmlContentExtractor
/// and in-memory fakes for the repo / use-cases / watermark store. Pins the v1
/// contract everything above (controllers, DTOs, frontend) wires against:
/// both operator use cases, watermark dedup, identifier fallback + counter,
/// oversize skip + counter, and mixed-rule coexistence.
/// </summary>
public class FileContentScanStrategyTests
{
    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class InMemoryWatermarkRepo : IScanWatermarkRepository
    {
        private readonly Dictionary<(int, string), DateTime> _content = new();
        public int UpsertCount { get; private set; }

        public Task<DateTime?> GetContentWatermarkAsync(int jobId, string path, CancellationToken ct = default)
            => Task.FromResult(_content.TryGetValue((jobId, path), out var v) ? v : (DateTime?)null);

        public Task UpsertContentWatermarkAsync(int jobId, int scanSourceId, string path, DateTime mtime, CancellationToken ct = default)
        {
            _content[(jobId, path)] = mtime;
            UpsertCount++;
            return Task.CompletedTask;
        }

        // Unused by FileContent scans.
        public Task<long> GetFileOffsetAsync(int j, string p, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateFileOffsetAsync(int j, int s, string p, long o, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetDbWatermarkAsync(int r, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateDbWatermarkAsync(int r, string v, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class Harness
    {
        public readonly List<JobFailure> Saved = new();
        public readonly List<JobFailure> Classified = new();
        public readonly InMemoryWatermarkRepo Watermarks = new();
        public readonly FileContentScanStrategy Strategy;
        /// <summary>SourceId for which SaveAsync returns a faulted task (simulates an
        /// unexpected non-oversize per-file error).</summary>
        public string? ThrowForSourceId { get; init; }

        public Harness()
        {
            var jobRepo = new Mock<IJobRepository>();
            var next = 1;
            jobRepo.Setup(r => r.SaveAsync(It.IsAny<JobFailure>(), It.IsAny<CancellationToken>()))
                   .Returns((JobFailure f, CancellationToken _) =>
                   {
                       if (ThrowForSourceId is not null &&
                           string.Equals(f.SourceId, ThrowForSourceId, StringComparison.OrdinalIgnoreCase))
                           return Task.FromException<JobFailure>(new IOException("simulated unreadable file"));
                       f.FailureId = next++;
                       Saved.Add(f);
                       return Task.FromResult(f);
                   });

            var classify = new Mock<IClassifyJobsUseCase>();
            classify.Setup(c => c.ExecuteAsync(It.IsAny<IEnumerable<JobFailure>>(), It.IsAny<CancellationToken>()))
                    .Returns((IEnumerable<JobFailure> fs, CancellationToken _) =>
                    {
                        Classified.AddRange(fs);
                        return Task.FromResult((IReadOnlyList<ClassificationResult>)Array.Empty<ClassificationResult>());
                    });

            var suggest = new Mock<IGenerateSuggestionsUseCase>();
            suggest.Setup(s => s.ExecuteAsync(It.IsAny<IEnumerable<ClassificationResult>>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

            Strategy = new FileContentScanStrategy(
                jobRepo.Object, Watermarks, classify.Object, suggest.Object,
                new IFileContentExtractor[] { new XmlContentExtractor(NullLogger<XmlContentExtractor>.Instance) },
                NullLogger<FileContentScanStrategy>.Instance);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"maia-fc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Write(string name, string content)
        {
            var p = System.IO.Path.Combine(Path, name);
            File.WriteAllText(p, content);
            return p;
        }
        public string WriteBytes(string name, byte[] bytes)
        {
            var p = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
    }

    // ── Builders ───────────────────────────────────────────────────────────────

    private static ScanCheckRule FcRule(
        int id, string pattern,
        string? locator = null, string? idLocator = null,
        ScanPredicateType? predType = null, string? predVal = null,
        string? desc = null, FileFormat? format = FileFormat.Xml,
        CheckType checkType = CheckType.FileContent)
        => new()
        {
            CheckRuleId             = id,
            MonitoredJobId          = 1,
            ScanSourceId            = 1,                // rules now belong to the source
            CheckType               = checkType,
            TargetField             = pattern,
            ExtractorType           = format,
            ExtractorLocator        = locator,
            IdentifierLocator       = idLocator,
            ExtractorPredicateType  = predType,
            ExtractorPredicateValue = predVal,
            Description             = desc,
            IsActive                = true,
        };

    // Tier 2.5: the job carries only identity (JobTypeId / MonitoredJobId / Name);
    // a single shared instance is fine since the strategy never mutates it.
    private static readonly MonitoredJob TheJob = new()
    {
        MonitoredJobId = 1,
        Name           = "FCJob",
        JobTypeId      = 1,
    };

    // The source carries the scan config + the rules. ScanAsync(job, source).
    private static ScanSource Source(string folder, bool recursive, params ScanCheckRule[] rules)
        => new()
        {
            ScanSourceId      = 1,
            MonitoredJobId    = 1,
            Name              = "FileContent",
            ScanTypeId        = 4,
            LogFolder         = folder,
            IncludeSubfolders = recursive,
            ScanCheckRules    = rules.ToList(),
        };

    private const string InvoiceError =
        "<file><header><invoiceId>INV-2026-001</invoiceId></header><status><code>ERROR</code></status></file>";
    private const string InvoiceOk =
        "<file><header><invoiceId>INV-2026-002</invoiceId></header><status><code>OK</code></status></file>";
    private const string WarningOrder =
        "<order id=\"ORD-88134\"><warning>quarantined</warning></order>";

    // ── Use case 1: filename signals failure ────────────────────────────────────

    [Fact]
    public async Task UseCase1_FilenameOnly_NoLocators_FiresWithFilenameSourceId()
    {
        using var dir = new TempDir();
        dir.Write("WARNING_20260606.xml", WarningOrder);
        dir.Write("ok_data.xml", InvoiceOk);  // doesn't match *WARNING*

        var h = new Harness();
        var rule = FcRule(1, "*WARNING*.xml", desc: "Found WARNING file");
        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));

        Assert.Equal(1, result.FailuresDetected);
        var f = Assert.Single(h.Saved);
        Assert.Equal("WARNING_20260606", f.SourceId);                 // filename without ext
        Assert.Equal("WARNING_20260606.xml", f.StepName);
        Assert.EndsWith("WARNING_20260606.xml", f.SourceFilePath);
        Assert.Equal(f.SourceFilePath, f.SourceLogPath);              // both point at the file
        Assert.Contains("Found WARNING file", f.ErrorMessage);
        Assert.Contains("(file: WARNING_20260606.xml)", f.ErrorMessage);
    }

    [Fact]
    public async Task UseCase1_FilenameMatch_WithIdentifier_UsesExtractedId()
    {
        using var dir = new TempDir();
        dir.Write("WARNING_x.xml", WarningOrder);

        var h = new Harness();
        var rule = FcRule(1, "*WARNING*.xml", idLocator: "/order/@id", desc: "Quarantined order");
        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));

        Assert.Equal(1, result.FailuresDetected);
        Assert.Equal("ORD-88134", Assert.Single(h.Saved).SourceId);
        Assert.Equal(0, result.IdentifierExtractionFailures);
    }

    // ── Use case 2: content predicate ───────────────────────────────────────────

    [Fact]
    public async Task UseCase2_ContentPredicate_FiresOnlyWhenSatisfied()
    {
        using var dir = new TempDir();
        dir.Write("invoice-error.xml", InvoiceError);
        dir.Write("invoice-ok.xml", InvoiceOk);

        var h = new Harness();
        var rule = FcRule(1, "*.xml",
            locator: "/file/status/code", predType: ScanPredicateType.Equals, predVal: "ERROR",
            idLocator: "/file/header/invoiceId", desc: "Invoice with error status");
        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));

        Assert.Equal(1, result.FailuresDetected);                     // only the ERROR one
        var f = Assert.Single(h.Saved);
        Assert.Equal("INV-2026-001", f.SourceId);
        Assert.Contains("Invoice with error status: ERROR", f.ErrorMessage);
        Assert.Contains("(file: invoice-error.xml)", f.ErrorMessage);
    }

    [Theory]
    [InlineData(ScanPredicateType.Equals,      "ERROR", true)]
    [InlineData(ScanPredicateType.Equals,      "error", true)]   // case-insensitive
    [InlineData(ScanPredicateType.NotEquals,   "OK",    true)]
    [InlineData(ScanPredicateType.NotEquals,   "ERROR", false)]
    [InlineData(ScanPredicateType.Contains,    "ERR",   true)]
    [InlineData(ScanPredicateType.NotContains, "OK",    true)]
    [InlineData(ScanPredicateType.NotContains, "ERR",   false)]
    public async Task Predicate_AllTypes(ScanPredicateType type, string value, bool shouldFire)
    {
        using var dir = new TempDir();
        dir.Write("data.xml", InvoiceError);  // /file/status/code == "ERROR"

        var h = new Harness();
        var rule = FcRule(1, "*.xml", locator: "/file/status/code", predType: type, predVal: value);
        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));

        Assert.Equal(shouldFire ? 1 : 0, result.FailuresDetected);
    }

    // ── Watermark dedup ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Watermark_SecondRunUnchanged_ProducesNoNewFailures()
    {
        using var dir = new TempDir();
        dir.Write("WARNING_a.xml", WarningOrder);
        dir.Write("WARNING_b.xml", WarningOrder);

        var h = new Harness();
        var rule = FcRule(1, "*WARNING*.xml", idLocator: "/order/@id");

        var first = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));
        Assert.Equal(2, first.FailuresDetected);
        Assert.Equal(2, h.Watermarks.UpsertCount);   // one watermark per examined file

        var second = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));
        Assert.Equal(0, second.FailuresDetected);     // unchanged files skipped
        Assert.Equal(2, h.Saved.Count);               // no new rows saved overall
    }

    // ── Identifier fallback + counter ────────────────────────────────────────────

    [Fact]
    public async Task IdentifierExtractionFails_FallsBackToFilename_AndCounts()
    {
        using var dir = new TempDir();
        dir.Write("WARNING_z.xml", WarningOrder);   // has /order/@id but NOT /order/missing

        var h = new Harness();
        var rule = FcRule(1, "*WARNING*.xml", idLocator: "/order/missing");
        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));

        Assert.Equal(1, result.FailuresDetected);
        Assert.Equal("WARNING_z", Assert.Single(h.Saved).SourceId);  // filename fallback
        Assert.Equal(1, result.IdentifierExtractionFailures);
    }

    // ── Predicate set but value not extractable → counted, no failure ────────────

    [Fact]
    public async Task PredicateValueNotExtractable_CountsAndDoesNotFire()
    {
        using var dir = new TempDir();
        dir.Write("data.xml", InvoiceError);   // has /file/status/code, NOT /file/status/missing

        var h = new Harness();
        // Valid XPath that matches nothing in the file → predicate can't be evaluated.
        var rule = FcRule(1, "*.xml",
            locator: "/file/status/missing", predType: ScanPredicateType.Equals, predVal: "ERROR");
        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));

        Assert.Equal(0, result.FailuresDetected);
        Assert.Equal(1, result.PredicateUnevaluableSkips);
    }

    // ── Oversize skip + counter + watermark still written ────────────────────────

    [Fact]
    public async Task OversizeFile_SkippedCountedAndWatermarked()
    {
        using var dir = new TempDir();
        // Force a parse (IdentifierLocator set) on a >5MB file.
        var big = dir.WriteBytes("WARNING_big.xml", new byte[XmlContentExtractor.MaxFileSizeBytes + 16]);

        var h = new Harness();
        var rule = FcRule(1, "*WARNING*.xml", idLocator: "/order/@id");
        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule));

        Assert.Equal(0, result.FailuresDetected);
        Assert.Equal(1, result.OversizeFileSkips);
        Assert.NotNull(await h.Watermarks.GetContentWatermarkAsync(1, big));  // watermarked despite skip
    }

    // ── Mixed rules: non-FileContent ignored, all FileContent applied ────────────

    [Fact]
    public async Task MixedRules_IgnoresNonFileContent_AppliesAllFileContent()
    {
        using var dir = new TempDir();
        dir.Write("invoice-error.xml", InvoiceError);
        dir.Write("WARNING_q.xml", WarningOrder);

        var h = new Harness();
        var contentRule = FcRule(1, "*.xml",
            locator: "/file/status/code", predType: ScanPredicateType.Equals, predVal: "ERROR",
            idLocator: "/file/header/invoiceId", desc: "Error invoice");
        var filenameRule = FcRule(2, "*WARNING*.xml", idLocator: "/order/@id", desc: "Warning file");
        // A non-FileContent rule on the same job — must be ignored by this strategy.
        var keywordRule = FcRule(3, "*.xml", desc: "noise", checkType: CheckType.ErrorKeyword);

        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, contentRule, filenameRule, keywordRule));

        // invoice-error.xml → contentRule (ERROR); WARNING_q.xml → both *.xml content
        // rule (code missing → predicate value not extractable → skip) AND filenameRule.
        Assert.Equal(2, result.FailuresDetected);
        var ids = h.Saved.Select(f => f.SourceId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "INV-2026-001", "ORD-88134" }, ids);
    }

    // ── Per-file resilience ──────────────────────────────────────────────────────

    [Fact] // one file's processing throws (non-oversize) — the other file must still fire
           // AND classify, and the scan surfaces the error afterward (recorded Failed).
    public async Task OneFileThrows_OtherFileStillFiresAndClassifies_ScanSurfacesError()
    {
        using var dir = new TempDir();
        dir.Write("WARNING_ok.xml", WarningOrder);
        dir.Write("WARNING_throw.xml", WarningOrder);

        // Filename-only rule → SourceId = filename without extension. Faulted SaveAsync
        // on "WARNING_throw" simulates an unexpected per-file error (not oversize).
        var h = new Harness { ThrowForSourceId = "WARNING_throw" };
        var rule = FcRule(1, "*WARNING*.xml", desc: "Found WARNING file");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, rule)));

        // The good file's failure was still created (not orphaned) and classified.
        var f = Assert.Single(h.Saved);
        Assert.Equal("WARNING_ok", f.SourceId);
        Assert.Contains(h.Classified, c => c.SourceId == "WARNING_ok");
    }

    // ── Scenario dump for human review (asserts + writes a readable table) ────────

    [Fact]
    public async Task Scenarios_DumpRealOperatorOutput()
    {
        using var dir = new TempDir();
        dir.Write("invoice-error.xml", InvoiceError);
        dir.Write("invoice-ok.xml", InvoiceOk);
        dir.Write("WARNING_20260606.xml", WarningOrder);

        var h = new Harness();
        var contentRule  = FcRule(1, "invoice-*.xml",
            locator: "/file/status/code", predType: ScanPredicateType.Equals, predVal: "ERROR",
            idLocator: "/file/header/invoiceId", desc: "Invoice with error status");
        var filenameRule = FcRule(2, "*WARNING*.xml", idLocator: "/order/@id", desc: "Found WARNING file");

        var result = await h.Strategy.ScanAsync(TheJob, Source(dir.Path, false, contentRule, filenameRule));

        var sb = new StringBuilder();
        sb.AppendLine($"FailuresDetected={result.FailuresDetected}  IdExtractionFailures={result.IdentifierExtractionFailures}  OversizeSkips={result.OversizeFileSkips}");
        sb.AppendLine("SourceId       | StepName              | ErrorMessage");
        sb.AppendLine("---------------+-----------------------+-------------------------------------------------");
        foreach (var f in h.Saved.OrderBy(f => f.SourceId))
            sb.AppendLine($"{f.SourceId,-14} | {f.StepName,-21} | {f.ErrorMessage}");
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "maia-fc-scenarios-output.txt"), sb.ToString());

        // invoice-error fires (ERROR), invoice-ok does not (OK), WARNING fires.
        Assert.Equal(2, result.FailuresDetected);
        Assert.Contains(h.Saved, f => f.SourceId == "INV-2026-001");
        Assert.Contains(h.Saved, f => f.SourceId == "ORD-88134");
        Assert.DoesNotContain(h.Saved, f => f.SourceId == "INV-2026-002");
    }
}
