using System.Text.Json;
using Maia.API.Controllers;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Security;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.DataAccess.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Maia.Tests.Integration;

/// <summary>
/// Pins the SQL-side job filter (<see cref="JobVisibilityQueryExtensions.WhereJobVisible"/>)
/// and its use by the operational reads: a restricted user sees only rows of assigned
/// jobs, never orphan failures, and the filter translates to a plain EXISTS (not the
/// ROW_NUMBER shape that caused the 2026-09-06 dashboard timeouts).
/// </summary>
public class JobVisibilityFilterTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;

    private const int JobTypeId = 81;
    private const int JobA      = 8101;   // assigned to the restricted user
    private const int JobB      = 8102;   // not assigned
    private const int UserId    = 77;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "VisJobType" });
        _db.MonitoredJobs.AddRange(
            new MonitoredJob { MonitoredJobId = JobA, Name = "Job A", JobTypeId = JobTypeId },
            new MonitoredJob { MonitoredJobId = JobB, Name = "Job B", JobTypeId = JobTypeId });
        _db.Users.Add(new User { UserId = UserId, Username = "scoped", PasswordHash = "x", RoleId = (int)MaiaRole.Operator, RestrictToAssignedJobs = true });
        _db.UserMonitoredJobs.Add(new UserMonitoredJob { UserId = UserId, MonitoredJobId = JobA });

        AddFailure(1, JobA, JobStatus.Failed);
        AddFailure(2, JobA, JobStatus.Resolved);
        AddFailure(3, JobB, JobStatus.Failed);
        AddFailure(4, null, JobStatus.Failed);   // orphan
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private void AddFailure(int id, int? jobId, JobStatus status) =>
        _db.JobFailures.Add(new JobFailure
        {
            FailureId = id, JobTypeId = JobTypeId, MonitoredJobId = jobId,
            SourceLogPath = "n/a", Status = status, DetectedAt = DateTime.Now.AddMinutes(-id),
        });

    private TestJobAccessScope Restricted => new(new HashSet<int> { JobA }, UserId);

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhereJobVisible_restricted_keeps_only_assigned_jobs_and_drops_orphans()
    {
        var ids = await _db.JobFailures
            .WhereJobVisible(_db, JobVisibility.AssignedTo(UserId), f => f.MonitoredJobId)
            .Select(f => f.FailureId).OrderBy(i => i).ToListAsync();

        Assert.Equal(new[] { 1, 2 }, ids);
    }

    [Fact]
    public async Task WhereJobVisible_unrestricted_or_null_is_a_no_op()
    {
        Assert.Equal(4, await _db.JobFailures.WhereJobVisible(_db, JobVisibility.All, f => f.MonitoredJobId).CountAsync());
        Assert.Equal(4, await _db.JobFailures.WhereJobVisible(_db, null, f => f.MonitoredJobId).CountAsync());
    }

    [Fact]
    public void WhereJobVisible_translates_to_EXISTS_not_ROW_NUMBER()
    {
        // ToQueryString never opens a connection, so a dummy SQL Server context works.
        using var sql = new MaiaDbContext(new DbContextOptionsBuilder<MaiaDbContext>()
            .UseSqlServer("Server=unused;Database=unused;").Options);

        var text = sql.JobFailures
            .WhereJobVisible(sql, JobVisibility.AssignedTo(UserId), f => f.MonitoredJobId)
            .ToQueryString();

        Assert.Contains("EXISTS", text);
        Assert.Contains("[UserMonitoredJobs]", text);
        Assert.DoesNotContain("ROW_NUMBER", text);
        Assert.DoesNotContain("OPENJSON", text);
    }

    [Fact]
    public async Task Failures_list_and_status_respect_the_scope()
    {
        var controller = Controller(Restricted);

        var list = Json(await controller.GetFailures(ct: default));
        var ids  = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("failureId").GetInt32()).OrderBy(i => i);
        Assert.Equal(new[] { 1, 2 }, ids);

        // A failure on an unassigned job — and an orphan — answer 404, like a missing one.
        Assert.IsType<NotFoundObjectResult>(await controller.GetFailureStatus(3, default));
        Assert.IsType<NotFoundObjectResult>(await controller.GetFailureStatus(4, default));
        Assert.IsType<NotFoundObjectResult>(await controller.GetFailureSource(3, default));
    }

    [Fact]
    public async Task Dashboard_stats_count_only_visible_jobs()
    {
        var scoped = Json(await Controller(Restricted).GetDashboardStats(default));
        Assert.Equal(2, scoped.GetProperty("totalFailures").GetInt32());
        Assert.Equal(1, scoped.GetProperty("active").GetInt32());

        var all = Json(await Controller(TestJobAccessScope.All).GetDashboardStats(default));
        Assert.Equal(4, all.GetProperty("totalFailures").GetInt32());
    }

    private DataController Controller(IJobAccessScope scope) => new(
        new SqlJobRepository(_factory),
        Mock.Of<IRecommendationRepository>(),
        Mock.Of<IMonitoredJobRepository>(),
        Mock.Of<IScanRunHistoryRepository>(),
        Mock.Of<IWorkerControlService>(),
        Mock.Of<ISourceDataReader>(),
        _factory,
        scope);

    private static JsonElement Json(IActionResult result)
    {
        var ok   = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
