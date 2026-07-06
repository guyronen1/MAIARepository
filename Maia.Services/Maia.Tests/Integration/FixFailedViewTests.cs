using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.DataAccess.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// Pin the contract of the dashboard "Fix Failures Today" KPI + its drill-down
/// (<c>view=fix-failed</c> in `GET /api/data/failures`) + the per-row marker
/// (<c>GetIdsWithRecentFixFailureAsync</c>). All three surfaces must agree:
/// the predicate is "Status=ManualRequired AND has at least one Success=false
/// FixExecutionLog row since the supplied cutoff (today-midnight)."
/// </summary>
public class FixFailedViewTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;

    // Above seed-data ranges (JobTypes 1-4, ErrorTypes 1-6, MonitoredJob 1).
    private const int JobTypeId      = 50;
    private const int MonitoredJobId = 5500;
    private static readonly DateTime TodayStart = DateTime.Today;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "TestJobType" });
        _db.MonitoredJobs.Add(new MonitoredJob {
            MonitoredJobId = MonitoredJobId, Name = "TestJob", JobTypeId = JobTypeId });
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagedAsync_FixFailedView_OnlyReturnsManualRequiredWithRecentFailedLog()
    {
        // F1: ManualRequired + failed log today → SHOULD match
        AddFailure(1, JobStatus.ManualRequired);
        AddFixLog(1, success: false, when: TodayStart.AddHours(2));

        // F2: ManualRequired but failed log was yesterday → must NOT match
        AddFailure(2, JobStatus.ManualRequired);
        AddFixLog(2, success: false, when: TodayStart.AddHours(-3));

        // F3: ManualRequired but log was SUCCESSFUL → must NOT match (the
        //     predicate requires Success=false specifically)
        AddFailure(3, JobStatus.ManualRequired);
        AddFixLog(3, success: true, when: TodayStart.AddHours(2));

        // F4: Failed log today but status is still Failed → must NOT match
        //     (the predicate requires ManualRequired specifically — once a
        //     failure is in Failed state the operator's mental model is "the
        //     system hasn't tried yet," even if a stray log row exists)
        AddFailure(4, JobStatus.Failed);
        AddFixLog(4, success: false, when: TodayStart.AddHours(1));

        // F5: ManualRequired but no log at all → must NOT match (the rec was
        //     rejected before the engine ran, for example)
        AddFailure(5, JobStatus.ManualRequired);

        // F6: ManualRequired + multiple failed logs today → SHOULD match once
        AddFailure(6, JobStatus.ManualRequired);
        AddFixLog(6, success: false, when: TodayStart.AddHours(1));
        AddFixLog(6, success: false, when: TodayStart.AddHours(2));
        await _db.SaveChangesAsync();

        var repo  = new SqlJobRepository(_factory);
        var paged = await repo.GetPagedAsync(1, 50, "fix-failed");

        var ids = paged.Items.Select(f => f.FailureId).OrderBy(i => i).ToList();
        Assert.Equal(new[] { 1, 6 }, ids);
    }

    [Fact]
    public async Task GetIdsWithRecentFixFailureAsync_ReturnsOnlyIdsWithFailedLogSinceCutoff()
    {
        // Same matrix as above but the marker doesn't filter on Status — its
        // job is "did a fix attempt FAIL today?" — Status filtering happens
        // separately (the marker is also rendered on plain "Failed" rows in
        // the All view, IF an executor failed against them today).
        AddFailure(10, JobStatus.ManualRequired);
        AddFixLog(10, success: false, when: TodayStart.AddHours(2));

        AddFailure(11, JobStatus.ManualRequired);
        AddFixLog(11, success: false, when: TodayStart.AddHours(-1));      // yesterday

        AddFailure(12, JobStatus.ManualRequired);
        AddFixLog(12, success: true,  when: TodayStart.AddHours(2));       // succeeded

        AddFailure(13, JobStatus.Failed);
        AddFixLog(13, success: false, when: TodayStart.AddHours(2));       // failed today, status still Failed

        AddFailure(14, JobStatus.ManualRequired);                          // no log

        await _db.SaveChangesAsync();

        var repo = new SqlJobRepository(_factory);
        var ids  = await repo.GetIdsWithRecentFixFailureAsync(
            new[] { 10, 11, 12, 13, 14 }, TodayStart);

        // Marker is Status-agnostic — 10 (ManualRequired) AND 13 (Failed)
        // both qualify because both had a failed log today.
        Assert.Equal(new[] { 10, 13 }, ids.OrderBy(i => i).ToArray());
    }

    [Fact]
    public async Task GetIdsWithRecentFixFailureAsync_EmptyInput_ReturnsEmpty()
    {
        var repo = new SqlJobRepository(_factory);
        var ids  = await repo.GetIdsWithRecentFixFailureAsync(Array.Empty<int>(), TodayStart);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetIdsWithRecentFixFailureAsync_DeduplicatesFailureIds()
    {
        // A single failure with multiple failed logs today must appear ONCE
        // in the output set. Operator's mental model: "this failure has had
        // fix failures today," not "this failure has 5 fix failures."
        AddFailure(20, JobStatus.ManualRequired);
        AddFixLog(20, success: false, when: TodayStart.AddHours(1));
        AddFixLog(20, success: false, when: TodayStart.AddHours(2));
        AddFixLog(20, success: false, when: TodayStart.AddHours(3));
        await _db.SaveChangesAsync();

        var repo = new SqlJobRepository(_factory);
        var ids  = await repo.GetIdsWithRecentFixFailureAsync(new[] { 20 }, TodayStart);

        Assert.Single(ids);
        Assert.Contains(20, ids);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void AddFailure(int id, JobStatus status) =>
        _db.JobFailures.Add(new JobFailure {
            FailureId      = id,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            SourceLogPath  = "n/a",
            Status         = status,
            DetectedAt     = TodayStart.AddHours(-12),
        });

    private void AddFixLog(int failureId, bool success, DateTime when) =>
        _db.FixExecutionLogs.Add(new FixExecutionLog {
            FailureId        = failureId,
            RecommendationId = failureId,    // not under test
            ExecutedAction   = "test",
            TriggerType      = TriggerType.OperatorApproved,
            ExecutedBy       = "test",
            Success          = success,
            ExecutedAt       = when,
        });

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
