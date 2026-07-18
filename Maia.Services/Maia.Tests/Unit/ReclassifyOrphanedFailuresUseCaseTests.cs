using Maia.Application.Classification;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// The orphaned-unclassified safety-net sweep (Task 2 / audit finding #2). Pins:
/// the age cutoff is (now − minAge) so in-flight scans aren't touched; stranded
/// failures are classified AND suggested (mirroring the strategy's post-loop step);
/// an empty sweep does no downstream work; and the returned count is the number
/// actually classified (not merely found).
/// </summary>
public class ReclassifyOrphanedFailuresUseCaseTests
{
    private readonly Mock<IJobRepository>              _jobs    = new();
    private readonly Mock<IClassifyJobsUseCase>        _classify = new();
    private readonly Mock<IGenerateSuggestionsUseCase> _suggest  = new();

    private ReclassifyOrphanedFailuresUseCase CreateSut() =>
        new(_jobs.Object, _classify.Object, _suggest.Object,
            NullLogger<ReclassifyOrphanedFailuresUseCase>.Instance);

    private static JobFailure Orphan(int id) => new()
    {
        FailureId = id, JobId = id, JobTypeId = 1,
        MonitoredJobId = 1, ScanSourceId = 1,
        Status = JobStatus.Failed, ErrorTypeId = null,
        SourceLogPath = $"c:/logs/{id}.log",
        ErrorMessage = $"[ERR] orphan {id}",
        DetectedAt = DateTime.Now.AddMinutes(-30),
    };

    private static ClassificationResult Classified(int id) => new()
    {
        FailureId = id, JobId = id, JobTypeId = 1,
        ErrorTypeId = 2, ErrorTypeCode = "DbConnection",
        RawError = "orphan", Confidence = 0.9,
    };

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_StrandedFailures_ClassifiesAndSuggests()
    {
        var orphans = new List<JobFailure> { Orphan(1), Orphan(2) };
        _jobs.Setup(j => j.GetUnclassifiedOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(orphans);

        var classifications = new List<ClassificationResult> { Classified(1), Classified(2) };
        _classify.Setup(c => c.ExecuteAsync(orphans, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(classifications);

        var recovered = await CreateSut().ExecuteAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(2, recovered);
        _classify.Verify(c => c.ExecuteAsync(orphans, It.IsAny<CancellationToken>()), Times.Once);
        // Suggest must run on exactly the classifications — this is the step a crashed
        // scan skipped, and the whole reason the failures were stranded.
        _suggest.Verify(s => s.ExecuteAsync(classifications, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sweep_NoOrphans_DoesNothingDownstream()
    {
        _jobs.Setup(j => j.GetUnclassifiedOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<JobFailure>());

        var recovered = await CreateSut().ExecuteAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(0, recovered);
        _classify.Verify(c => c.ExecuteAsync(It.IsAny<IEnumerable<JobFailure>>(), It.IsAny<CancellationToken>()), Times.Never);
        _suggest.Verify(s => s.ExecuteAsync(It.IsAny<IEnumerable<ClassificationResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_CutoffIsNowMinusMinAge_ExcludesInFlightFailures()
    {
        DateTime captured = default;
        _jobs.Setup(j => j.GetUnclassifiedOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .Callback<DateTime, CancellationToken>((cutoff, _) => captured = cutoff)
             .ReturnsAsync(new List<JobFailure>());

        var before = DateTime.Now;
        await CreateSut().ExecuteAsync(TimeSpan.FromMinutes(15));
        var after = DateTime.Now;

        // Cutoff = now − 15min. Anything detected after the cutoff (i.e. a scan's
        // just-created rows) is excluded from the sweep.
        Assert.InRange(captured,
            before.AddMinutes(-15).AddSeconds(-2),
            after.AddMinutes(-15).AddSeconds(2));
    }

    [Fact]
    public async Task Sweep_ReturnsClassifiedCount_NotFoundCount()
    {
        // Three stranded, but only one matches a classification rule (the other two
        // stay unclassified and will be retried next sweep). Return reflects real recovery.
        var orphans = new List<JobFailure> { Orphan(1), Orphan(2), Orphan(3) };
        _jobs.Setup(j => j.GetUnclassifiedOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(orphans);
        _classify.Setup(c => c.ExecuteAsync(orphans, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new List<ClassificationResult> { Classified(1) });

        var recovered = await CreateSut().ExecuteAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, recovered);
        _suggest.Verify(s => s.ExecuteAsync(
            It.Is<IEnumerable<ClassificationResult>>(r => r.Count() == 1), It.IsAny<CancellationToken>()), Times.Once);
    }
}
