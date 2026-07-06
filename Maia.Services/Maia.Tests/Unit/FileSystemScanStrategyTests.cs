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
/// Unit tests for FileSystemScanStrategy keyword mode over a real temp folder
/// (mocked repos). Pins a happy-path keyword match plus the per-file RESILIENCE
/// contract: one unreadable file (here simulated via a faulted SaveAsync) must
/// NOT abort the whole source scan or orphan the failures other files already
/// produced — the good file's failure is still created AND classified, and the
/// scan surfaces the error afterward (recorded Failed).
/// </summary>
public sealed class FileSystemScanStrategyTests : IDisposable
{
    private readonly string _dir;

    public FileSystemScanStrategyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maia-fs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly List<JobFailure> Saved = new();
        public readonly List<JobFailure> Classified = new();
        /// <summary>SourceId (filename) for which SaveAsync returns a faulted task.</summary>
        public string? ThrowForSourceId { get; init; }

        public FileSystemScanStrategy Build()
        {
            var jobRepo = new Mock<IJobRepository>();
            var next = 1;
            jobRepo.Setup(r => r.SaveAsync(It.IsAny<JobFailure>(), It.IsAny<CancellationToken>()))
                   .Returns((JobFailure f, CancellationToken _) =>
                   {
                       if (ThrowForSourceId is not null &&
                           string.Equals(f.SourceId, ThrowForSourceId, StringComparison.OrdinalIgnoreCase))
                           return Task.FromException<JobFailure>(new IOException("simulated locked file"));
                       f.FailureId = next++;
                       Saved.Add(f);
                       return Task.FromResult(f);
                   });

            var watermarks = new Mock<IScanWatermarkRepository>();
            watermarks.Setup(w => w.GetFileOffsetAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(0L);
            watermarks.Setup(w => w.UpdateFileOffsetAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

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

            return new FileSystemScanStrategy(
                Mock.Of<IDirectoryPipelineUseCase>(),   // unused in keyword mode
                jobRepo.Object, watermarks.Object, classify.Object, suggest.Object,
                NullLogger<FileSystemScanStrategy>.Instance);
        }
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private void WriteFile(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

    private (MonitoredJob, ScanSource) JobAndSource()
    {
        var rule = new ScanCheckRule
        {
            CheckRuleId = 1, MonitoredJobId = 1, ScanSourceId = 1,
            CheckType = CheckType.ErrorKeyword, TargetField = "ERROR", IsActive = true,
        };
        var job = new MonitoredJob { MonitoredJobId = 1, JobTypeId = 7, Name = "FsJob" };
        var source = new ScanSource
        {
            ScanSourceId = 1, MonitoredJobId = 1, Name = "Logs",
            LogFolder = _dir, SearchPatterns = "*.log", IncludeSubfolders = false,
            ScanCheckRules = new List<ScanCheckRule> { rule },
        };
        return (job, source);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KeywordMatch_CreatesFailure_AndClassifies()
    {
        WriteFile("app.log", "INFO start\nERROR boom happened\nINFO done\n");
        var h = new Harness();
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(1, result.FailuresDetected);
        var f = Assert.Single(h.Saved);
        Assert.Equal("app.log", f.SourceId);
        Assert.Contains("ERROR boom happened", f.ErrorMessage);
        Assert.Contains(h.Classified, c => c.SourceId == "app.log");
    }

    [Fact]
    public async Task NoKeywordMatch_NoFailure()
    {
        WriteFile("app.log", "INFO start\nINFO all good\n");
        var h = new Harness();
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
        Assert.Empty(h.Saved);
    }

    [Fact] // one file's processing throws — the other file must still be scanned + classified,
           // and the scan surfaces the error afterward (recorded Failed, not silently OK).
    public async Task OneFileThrows_OtherFileStillScannedAndClassified_ScanSurfacesError()
    {
        WriteFile("ok.log",    "ERROR from ok file\n");
        WriteFile("throw.log", "ERROR from throw file\n");
        var h = new Harness { ThrowForSourceId = "throw.log" };
        var (job, source) = JobAndSource();

        // Scan surfaces the file error (scan-run recorded Failed) ...
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Build().ScanAsync(job, source));

        // ... but the good file's failure was still created (not orphaned by the bad one) ...
        var f = Assert.Single(h.Saved);
        Assert.Equal("ok.log", f.SourceId);
        // ... and classified BEFORE the error surfaced.
        Assert.Contains(h.Classified, c => c.SourceId == "ok.log");
    }
}
