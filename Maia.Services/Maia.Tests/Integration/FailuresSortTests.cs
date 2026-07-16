using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.DataAccess.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// Pins the server-side sort on <c>SqlJobRepository.GetPagedAsync(sort, dir)</c>
/// (backs the failures-list sortable headers, item 7): whitelisted keys, both
/// directions, default = newest-first, and a deterministic FailureId tiebreaker.
/// </summary>
public class FailuresSortTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;

    private const int JobTypeId = 40;
    private static readonly DateTime T = new(2026, 7, 10, 9, 0, 0);

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "JT" });
        _db.MonitoredJobs.Add(new MonitoredJob { MonitoredJobId = 401, Name = "Alpha", JobTypeId = JobTypeId });
        _db.MonitoredJobs.Add(new MonitoredJob { MonitoredJobId = 402, Name = "Bravo", JobTypeId = JobTypeId });
        _db.ErrorTypes.Add(new ErrorType { ErrorTypeId = 41, Code = "Timeout",    DisplayName = "Timeout",    Severity = Severity.Medium });
        _db.ErrorTypes.Add(new ErrorType { ErrorTypeId = 42, Code = "FileLocked", DisplayName = "File locked", Severity = Severity.Medium });

        // F1 oldest/Alpha/Timeout, F2 newest/Bravo/FileLocked, F3 mid/Alpha/FileLocked.
        AddFailure(1, T.AddHours(1), 401, 41, JobStatus.Failed);
        AddFailure(2, T.AddHours(3), 402, 42, JobStatus.Resolved);
        AddFailure(3, T.AddHours(2), 401, 42, JobStatus.ManualRequired);
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task<int[]> IdsAsync(string? sort, string? dir)
    {
        var repo  = new SqlJobRepository(_factory);
        var paged = await repo.GetPagedAsync(1, 50, view: null, sort: sort, dir: dir);
        return paged.Items.Select(f => f.FailureId).ToArray();
    }

    [Fact]
    public async Task DefaultSort_IsNewestFirst() =>
        Assert.Equal(new[] { 2, 3, 1 }, await IdsAsync(null, null));

    [Fact]
    public async Task UnknownSortKey_FallsBackToNewestFirst() =>
        Assert.Equal(new[] { 2, 3, 1 }, await IdsAsync("bogus", "asc"));

    [Fact]
    public async Task SortById_BothDirections()
    {
        Assert.Equal(new[] { 1, 2, 3 }, await IdsAsync("id", "asc"));
        Assert.Equal(new[] { 3, 2, 1 }, await IdsAsync("id", "desc"));
    }

    [Fact]
    public async Task SortByDetected_Ascending_IsOldestFirst() =>
        Assert.Equal(new[] { 1, 3, 2 }, await IdsAsync("detected", "asc"));

    [Fact]
    public async Task SortByJob_Ascending_WithFailureIdTiebreak() =>
        // Alpha (F1, F3) before Bravo (F2); Alpha ties broken by FailureId asc.
        Assert.Equal(new[] { 1, 3, 2 }, await IdsAsync("job", "asc"));

    [Fact]
    public async Task SortByErrorType_Ascending() =>
        // FileLocked (F2, F3) before Timeout (F1); tie broken by FailureId asc.
        Assert.Equal(new[] { 2, 3, 1 }, await IdsAsync("errortype", "asc"));

    private void AddFailure(int id, DateTime detectedAt, int jobId, int errorTypeId, JobStatus status) =>
        _db.JobFailures.Add(new JobFailure {
            FailureId      = id,
            JobTypeId      = JobTypeId,
            MonitoredJobId = jobId,
            ErrorTypeId    = errorTypeId,
            SourceLogPath  = "n/a",
            Status         = status,
            DetectedAt     = detectedAt,
        });

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
