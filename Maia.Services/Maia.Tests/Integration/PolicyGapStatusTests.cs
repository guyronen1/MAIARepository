using System.Text.Json;
using Maia.API.Controllers;
using Maia.Core.Analysis;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// Pins the Case B (/unconfigured policy-gaps) contract that a policy gap is an
/// OPEN failure only. Regression for the bug where GetPolicyGaps had no Status
/// filter (unlike Case A), so a Resolved failure still counted as a gap and
/// marking it resolved didn't clear it.
/// </summary>
public class PolicyGapStatusTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;

    // Above seed-data ranges.
    private const int JobTypeId      = 60;
    private const int MonitoredJobId = 6600;
    private const int GapErrorType   = 70;   // no fix policy → gap-eligible
    private const int CoveredError   = 71;   // has a fix policy → never a gap

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "TestJobType" });
        _db.MonitoredJobs.Add(new MonitoredJob { MonitoredJobId = MonitoredJobId, Name = "TestJob", JobTypeId = JobTypeId });
        _db.ErrorTypes.Add(new ErrorType { ErrorTypeId = GapErrorType, Code = "GapType",     DisplayName = "Gap",     Severity = Severity.Medium });
        _db.ErrorTypes.Add(new ErrorType { ErrorTypeId = CoveredError, Code = "CoveredType", DisplayName = "Covered", Severity = Severity.Medium });
        // A fix policy that covers CoveredError (JobType default) — so failures of
        // that type are never a gap, even when open.
        _db.FixPolicyRules.Add(new FixPolicyRule {
            JobTypeId = JobTypeId, ErrorTypeId = CoveredError, MonitoredJobId = null,
            ActionToApply = "noop", FixCategory = FixCategory.Manual,
            ActionType = FixActionType.Manual, Enabled = true });
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task GetPolicyGaps_ExcludesResolvedFailures_CountsOnlyOpen()
    {
        // F1: OPEN (Failed), GapType, no policy → SHOULD be a gap.
        AddClassifiedFailure(1, JobStatus.Failed, GapErrorType);
        // F2: RESOLVED, GapType, no policy → must NOT be a gap (the bug).
        AddClassifiedFailure(2, JobStatus.Resolved, GapErrorType);
        // F3: AwaitingManualAction, GapType, no policy → already actioned, NOT a gap.
        AddClassifiedFailure(3, JobStatus.AwaitingManualAction, GapErrorType);
        // F4: OPEN (Failed) but CoveredError has a policy → NOT a gap.
        AddClassifiedFailure(4, JobStatus.Failed, CoveredError);
        // F5: a second OPEN GapType failure → the gap count should be 2 (F1 + F5).
        AddClassifiedFailure(5, JobStatus.Failed, GapErrorType);
        await _db.SaveChangesAsync();

        var controller = new UnconfiguredController(_factory, Mock.Of<IUnconfiguredClusterAnalyzer>());
        var result = await controller.GetPolicyGaps("all");

        var ok   = Assert.IsType<OkObjectResult>(result);
        // Serialize with camelCase to mirror the ASP.NET MVC pipeline (the anon
        // object mixes shorthand PascalCase members with explicit camelCase ones).
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Only the open GapType failures (F1 + F5) count; resolved/acknowledged
        // and policy-covered ones are excluded.
        Assert.Equal(2, root.GetProperty("totalGaps").GetInt32());

        var gaps = root.GetProperty("gaps");
        Assert.Equal(1, gaps.GetArrayLength());                       // one (ErrorType, Job) group
        var gap = gaps[0];
        Assert.Equal(GapErrorType, gap.GetProperty("errorTypeId").GetInt32());
        Assert.Equal(2, gap.GetProperty("count").GetInt32());
        // Sample anchors on the lowest open failure id (1), never the resolved one (2).
        Assert.Equal(1, gap.GetProperty("sampleFailureId").GetInt32());
    }

    private void AddClassifiedFailure(int id, JobStatus status, int errorTypeId)
    {
        _db.JobFailures.Add(new JobFailure {
            FailureId      = id,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            ErrorTypeId    = errorTypeId,
            SourceLogPath  = "n/a",
            Status         = status,
            DetectedAt     = DateTime.Now.AddHours(-1),
        });
        _db.AIRecommendations.Add(new AiRecommendation {
            RecommendationId = id,
            FailureId        = id,
            ErrorTypeId      = errorTypeId,
            SuggestedAction  = "test",
            FixCategory      = FixCategory.Manual,
            RecommendedAt    = DateTime.Now.AddHours(-1),
        });
    }

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
