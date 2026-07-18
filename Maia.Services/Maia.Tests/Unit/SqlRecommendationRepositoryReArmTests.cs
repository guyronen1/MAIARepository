using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.DataAccess.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Pins <see cref="SqlRecommendationRepository.ReArmForRetryAsync"/> — the operator-Retry
/// re-arm write moved off the controller's raw DbContext (Task 3, audit finding #5).
/// It must clear IsExecuted + claim, approve the rec, AND flip the failure back to
/// Failed, all together, so the drain's claim guard (Failure.Status == Failed) lets it
/// through on the same request.
/// </summary>
public class SqlRecommendationRepositoryReArmTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;
    private SqlRecommendationRepository _repo = null!;

    private const int FailureId = 7001;
    private const int RecId     = 7002;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobFailures.Add(new JobFailure
        {
            FailureId = FailureId, JobTypeId = 1, MonitoredJobId = 1, ScanSourceId = 1,
            SourceId = "row-1", SourceLogPath = @"c:\logs\a.log",
            Status = JobStatus.ManualRequired,     // where a failed executor leaves it
        });
        _db.AIRecommendations.Add(new AiRecommendation
        {
            RecommendationId = RecId, FailureId = FailureId, ErrorTypeId = 1,
            SuggestedAction = "retry", FixCategory = FixCategory.Retry, ConfidenceScore = 0.9m,
            IsExecuted = true,             // executor ran and failed
            OperatorApproved = true,
            ClaimedBy = "host;pid;run",    // stale claim from the failed drain
            ClaimedAt = DateTime.Now.AddMinutes(-1),
        });
        await _db.SaveChangesAsync();

        _repo = new SqlRecommendationRepository(_factory);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task ReArm_ClearsExecutionAndClaim_AndFlipsFailureToFailed()
    {
        var ok = await _repo.ReArmForRetryAsync(RecId);

        Assert.True(ok);

        await using var verify = _factory.CreateDbContext();
        var rec = await verify.AIRecommendations.Include(r => r.Failure)
            .FirstAsync(r => r.RecommendationId == RecId);

        Assert.False(rec.IsExecuted);
        Assert.True(rec.OperatorApproved);
        Assert.Null(rec.ClaimedBy);
        Assert.Null(rec.ClaimedAt);
        Assert.Equal(JobStatus.Failed, rec.Failure!.Status);   // drain-eligible again
    }

    [Fact]
    public async Task ReArm_UnknownRecommendation_ReturnsFalse()
    {
        var ok = await _repo.ReArmForRetryAsync(999999);
        Assert.False(ok);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
