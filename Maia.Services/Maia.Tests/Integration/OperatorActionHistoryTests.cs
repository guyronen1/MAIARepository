using System.Text.Json;
using Maia.API.Controllers;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// Pins the contract of <c>GET /api/data/operator-actions</c> — the decision
/// HISTORY behind the Operator Actions screen (distinct from the pending queue
/// on /recommendations). Rows come back newest-first with joined context
/// (recommendation + failure + job + error type), filterable by decision and
/// free text, with server-side paging.
/// </summary>
public class OperatorActionHistoryTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;

    // Above seed-data ranges.
    private const int JobTypeId      = 80;
    private const int MonitoredJobId = 8800;
    private const int ErrorTypeId    = 90;
    private static readonly DateTime T0 = new(2026, 7, 10, 8, 0, 0);

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "TestJobType" });
        _db.MonitoredJobs.Add(new MonitoredJob { MonitoredJobId = MonitoredJobId, Name = "B2B Import", JobTypeId = JobTypeId });
        _db.ErrorTypes.Add(new ErrorType { ErrorTypeId = ErrorTypeId, Code = "FileLocked", DisplayName = "File locked", Severity = Severity.Medium });

        // Failure 1 ended Resolved; its rec executed. Failure 2 sits in ManualRequired.
        AddFailure(1, JobStatus.Resolved);
        AddFailure(2, JobStatus.ManualRequired);
        AddRecommendation(100, failureId: 1, action: "Retry the import job", isExecuted: true);
        AddRecommendation(200, failureId: 2, action: "Copy source file back", isExecuted: false);

        // Three decisions, deliberately inserted out of timestamp order.
        AddAction(1000, recId: 100, "Approve", "alice", T0.AddMinutes(10));
        AddAction(1001, recId: 200, "Reject",  "bob",   T0.AddMinutes(30));   // newest
        AddAction(1002, recId: 100, "Retry",   "alice", T0.AddMinutes(20));
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOperatorActions_ReturnsNewestFirst_WithJoinedContext()
    {
        var root  = await QueryAsync();
        var items = root.GetProperty("items");

        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, items.GetArrayLength());

        // Newest first: 1001 (T+30) → 1002 (T+20) → 1000 (T+10).
        Assert.Equal(1001, items[0].GetProperty("actionId").GetInt32());
        Assert.Equal(1002, items[1].GetProperty("actionId").GetInt32());
        Assert.Equal(1000, items[2].GetProperty("actionId").GetInt32());

        // Joined context on the newest row (Reject by bob on rec 200 / failure 2).
        var top = items[0];
        Assert.Equal("bob",                   top.GetProperty("operatorId").GetString());
        Assert.Equal("Reject",                top.GetProperty("actionTaken").GetString());
        Assert.Equal(200,                     top.GetProperty("recommendationId").GetInt32());
        Assert.Equal("Copy source file back", top.GetProperty("suggestedAction").GetString());
        Assert.Equal(2,                       top.GetProperty("failureId").GetInt32());
        Assert.Equal("FileLocked",            top.GetProperty("errorTypeCode").GetString());
        Assert.Equal("B2B Import",            top.GetProperty("monitoredJobName").GetString());
        Assert.Equal("ManualRequired",        top.GetProperty("failureStatus").GetString());
        Assert.False(top.GetProperty("isExecuted").GetBoolean());

        // The approved rec's row reflects that it executed and the failure resolved.
        var approved = items[2];
        Assert.True(approved.GetProperty("isExecuted").GetBoolean());
        Assert.Equal("Resolved", approved.GetProperty("failureStatus").GetString());
    }

    [Fact]
    public async Task GetOperatorActions_FiltersByActionTaken()
    {
        var root  = await QueryAsync(actionTaken: "Approve");
        var items = root.GetProperty("items");

        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1000, items[0].GetProperty("actionId").GetInt32());
    }

    [Fact]
    public async Task GetOperatorActions_FreeTextMatchesActionErrorTypeAndJobName()
    {
        // Matches SuggestedAction text ("Copy source file back") → rec 200's row only.
        var byAction = await QueryAsync(q: "Copy source");
        Assert.Equal(1, byAction.GetProperty("totalCount").GetInt32());

        // Matches the job name → every row (all recs hang off the same job).
        var byJob = await QueryAsync(q: "B2B");
        Assert.Equal(3, byJob.GetProperty("totalCount").GetInt32());

        // Matches the ErrorType code → every row (shared error type).
        var byCode = await QueryAsync(q: "FileLocked");
        Assert.Equal(3, byCode.GetProperty("totalCount").GetInt32());

        // No match → empty.
        var none = await QueryAsync(q: "zzz-no-such-thing");
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task GetOperatorActions_PagesServerSide()
    {
        var page1 = await QueryAsync(page: 1, pageSize: 2);
        Assert.Equal(3, page1.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page1.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());

        var page2 = await QueryAsync(page: 2, pageSize: 2);
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        // Oldest action lands on the last page under newest-first ordering.
        Assert.Equal(1000, page2.GetProperty("items")[0].GetProperty("actionId").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> QueryAsync(
        string? operatorId = null, string? actionTaken = null, string? q = null,
        int page = 1, int pageSize = 50)
    {
        var controller = new DataController(
            Mock.Of<IJobRepository>(),
            Mock.Of<IRecommendationRepository>(),
            Mock.Of<IMonitoredJobRepository>(),
            Mock.Of<IScanRunHistoryRepository>(),
            Mock.Of<IWorkerControlService>(),
            _factory);

        var result = await controller.GetOperatorActions(
            operatorId, actionTaken, fromDate: null, toDate: null, q, page, pageSize);

        var ok   = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return JsonDocument.Parse(json).RootElement;
    }

    private void AddFailure(int id, JobStatus status) =>
        _db.JobFailures.Add(new JobFailure {
            FailureId      = id,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            ErrorTypeId    = ErrorTypeId,
            SourceLogPath  = "n/a",
            Status         = status,
            DetectedAt     = T0.AddHours(-1),
        });

    private void AddRecommendation(int id, int failureId, string action, bool isExecuted) =>
        _db.AIRecommendations.Add(new AiRecommendation {
            RecommendationId = id,
            FailureId        = failureId,
            ErrorTypeId      = ErrorTypeId,
            SuggestedAction  = action,
            FixCategory      = FixCategory.Retry,
            RecommendedAt    = T0.AddMinutes(-30),
            IsExecuted       = isExecuted,
        });

    private void AddAction(int id, int recId, string taken, string operatorId, DateTime when) =>
        _db.OperatorActions.Add(new OperatorAction {
            ActionId         = id,
            RecommendationId = recId,
            OperatorId       = operatorId,
            ActionTaken      = taken,
            ActionTimestamp  = when,
        });

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
