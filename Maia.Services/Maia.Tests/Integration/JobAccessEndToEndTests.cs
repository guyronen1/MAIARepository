using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maia.Tests.Integration;

/// <summary>
/// Per-user job visibility through the real pipeline (auth handler, policies, DI):
/// an admin assigns jobs on the Users API, and the operator's reads and actions are
/// then limited to those jobs — while the admin keeps seeing everything.
/// </summary>
public sealed class JobAccessEndToEndTests : IAsyncLifetime
{
    private const int JobTypeId = 91;
    private const int JobA = 9101, JobB = 9102;
    private const int FailureA = 91001, FailureB = 91002;
    private const int RecB = 91102;

    private AuthTestFactory _factory = null!;
    private HttpClient _admin = null!;
    private int _operatorId;

    public async Task InitializeAsync()
    {
        _factory = new AuthTestFactory();
        await _factory.SeedUsersAsync();

        var dbf = _factory.Services.GetRequiredService<IDbContextFactory<MaiaDbContext>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "E2EJobType" });
            db.MonitoredJobs.AddRange(
                new MonitoredJob { MonitoredJobId = JobA, Name = "Job A", JobTypeId = JobTypeId },
                new MonitoredJob { MonitoredJobId = JobB, Name = "Job B", JobTypeId = JobTypeId });
            db.JobFailures.AddRange(Failure(FailureA, JobA), Failure(FailureB, JobB));
            db.AIRecommendations.Add(new AiRecommendation
            {
                RecommendationId = RecB, FailureId = FailureB, ErrorTypeId = 1,
                SuggestedAction = "Retry", RecommendedAt = DateTime.Now,
            });
            await db.SaveChangesAsync();
            _operatorId = (await db.Users.SingleAsync(u => u.Username == AuthTestFactory.OperatorUser)).UserId;
        }

        _admin = await LoggedIn(AuthTestFactory.AdminUser);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static JobFailure Failure(int id, int jobId) => new()
    {
        FailureId = id, JobTypeId = JobTypeId, MonitoredJobId = jobId,
        SourceLogPath = "n/a", Status = JobStatus.Failed, DetectedAt = DateTime.Now,
    };

    private async Task<HttpClient> LoggedIn(string username)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username, password = AuthTestFactory.Password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static int[] FailureIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("failureId").GetInt32()).Order().ToArray();

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Existing_users_default_to_all_jobs()
    {
        using var op = await LoggedIn(AuthTestFactory.OperatorUser);

        Assert.Equal(new[] { FailureA, FailureB }, FailureIds(await Json(await op.GetAsync("/api/data/failures"))));
        var me = await Json(await op.GetAsync("/api/auth/me"));
        Assert.Equal("all", me.GetProperty("jobScope").GetString());
    }

    [Fact]
    public async Task Assigned_operator_sees_and_acts_on_only_assigned_jobs()
    {
        var put = await _admin.PutAsJsonAsync($"/api/users/{_operatorId}/jobs", new { allJobs = false, jobIds = new[] { JobA } });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var op = await LoggedIn(AuthTestFactory.OperatorUser);

        // Reads
        Assert.Equal(new[] { FailureA }, FailureIds(await Json(await op.GetAsync("/api/data/failures"))));
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/data/failures/{FailureB}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/data/failures/{FailureB}/source")).StatusCode);
        var jobs = await Json(await op.GetAsync("/api/config/monitored-jobs"));
        Assert.Equal(new[] { JobA }, jobs.EnumerateArray().Select(j => j.GetProperty("monitoredJobId").GetInt32()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/config/monitored-jobs/{JobB}")).StatusCode);
        Assert.Equal(0, (await Json(await op.GetAsync("/api/data/recommendations"))).GetProperty("totalCount").GetInt32());

        // Actions on the other job answer 404 and change nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync($"/api/recommendations/{RecB}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync($"/api/failures/{FailureB}/mark-resolved", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync($"/api/JobScan/{JobB}", null)).StatusCode);

        // /me tells the client the user is scoped.
        var me = await Json(await op.GetAsync("/api/auth/me"));
        Assert.Equal("assigned", me.GetProperty("jobScope").GetString());
        Assert.Equal(1, me.GetProperty("assignedJobCount").GetInt32());

        // The admin still sees every job.
        Assert.Equal(new[] { FailureA, FailureB }, FailureIds(await Json(await _admin.GetAsync("/api/data/failures"))));
    }

    [Fact]
    public async Task Job_access_round_trips_is_listed_and_audited()
    {
        await _admin.PutAsJsonAsync($"/api/users/{_operatorId}/jobs", new { allJobs = false, jobIds = new[] { JobB, JobA } });

        var access = await Json(await _admin.GetAsync($"/api/users/{_operatorId}/jobs"));
        Assert.False(access.GetProperty("allJobs").GetBoolean());
        Assert.Equal(new[] { JobA, JobB }, access.GetProperty("jobIds").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        var row = (await Json(await _admin.GetAsync("/api/users"))).EnumerateArray()
            .Single(u => u.GetProperty("userId").GetInt32() == _operatorId);
        Assert.False(row.GetProperty("allJobs").GetBoolean());
        Assert.Equal(2, row.GetProperty("jobCount").GetInt32());

        var dbf = _factory.Services.GetRequiredService<IDbContextFactory<MaiaDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var audit = await db.AuditLogs.SingleAsync(a => a.EventType == "UserJobAccessUpdated");
        Assert.Equal("User", audit.EntityType);
        Assert.Equal(_operatorId.ToString(), audit.EntityId);
        Assert.Contains("AllJobs: True → False", audit.Detail);
        Assert.Contains("Jobs: (none) → Job A, Job B", audit.Detail);
    }

    [Fact]
    public async Task Unknown_job_id_is_rejected()
    {
        var put = await _admin.PutAsJsonAsync($"/api/users/{_operatorId}/jobs", new { allJobs = false, jobIds = new[] { 424242 } });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("UnknownJob", await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Created_user_can_start_scoped()
    {
        var create = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = "newop", password = "Created!1234", role = "Operator",
            allJobs = false, jobIds = new[] { JobB },
        });
        var id = (await Json(create)).GetProperty("userId").GetInt32();

        var access = await Json(await _admin.GetAsync($"/api/users/{id}/jobs"));
        Assert.False(access.GetProperty("allJobs").GetBoolean());
        Assert.Equal(new[] { JobB }, access.GetProperty("jobIds").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }
}
