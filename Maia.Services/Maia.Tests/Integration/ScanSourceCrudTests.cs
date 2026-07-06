using System.Text.Json;
using Maia.API.Controllers;
using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.Fix;
using Maia.Infrastructure.Scanning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// Pins the Tier 2.5 phase-(d1) ScanSource CRUD + validation matrix on
/// ConfigController: per-type config requirements, name rules, ScanType
/// immutability, the SourceFolderConflict watermark-grain guard, and the
/// soft-delete cascade to child rules. Controller exercised directly over an
/// in-memory MaiaDbContext (ScanTypes 1-4 come from seed HasData).
/// </summary>
public class ScanSourceCrudTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private ConfigController _ctrl = null!;
    private CapturingAudit _audit = null!;

    private const int JobTypeId = 70;
    private const int JobId     = 7000;
    // Seeded ScanTypes (HasData): 1=FileSystem, 2=Database, 3=ApiEndpoint, 4=FileContent.
    private const int FsType = 1, DbType = 2, ApiType = 3, FcType = 4;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new MaiaDbContext(options);
        await _db.Database.EnsureCreatedAsync();   // applies ScanTypes seed
        _db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "TestJT" });
        _db.MonitoredJobs.Add(new MonitoredJob { MonitoredJobId = JobId, Name = "TestJob", JobTypeId = JobTypeId });
        await _db.SaveChangesAsync();

        _audit = new CapturingAudit();
        _ctrl = new ConfigController(
            Mock.Of<IMonitoredJobRepository>(),
            Mock.Of<IClassificationRuleRepository>(),
            _audit,
            NullLogger<ConfigController>.Instance,
            new TestDbContextFactory(options),
            new IFileContentExtractor[] { new XmlContentExtractor(NullLogger<XmlContentExtractor>.Instance) },
            new SqlFixScopeValidator(),
            // Anonymous accessor (UserName null) → Actor() falls back to the request's
            // operatorId, the Phase-1 behavior these tests assert against.
            Mock.Of<ICurrentUserAccessor>());
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static UpsertScanSourceRequest Req(
        string name, int scanTypeId, string? logFolder = null, string? connectionName = null,
        string? logSourceUrl = null, bool includeSubfolders = false, bool isActive = true)
        => new(name, scanTypeId,
               LogFolder: logFolder, ConnectionName: connectionName, LogSourceUrl: logSourceUrl,
               IncludeSubfolders: includeSubfolders, IsActive: isActive);

    private static string? ErrorOf(IActionResult r)
    {
        if (r is not BadRequestObjectResult bad) return null;
        var json = JsonSerializer.Serialize(bad.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
    }

    private async Task<int> CreateOkAsync(UpsertScanSourceRequest req)
    {
        var r = await _ctrl.CreateScanSource(JobId, req, default);
        var ok = Assert.IsType<OkObjectResult>(r);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("scanSourceId").GetInt32();
    }

    // ── Validation matrix ───────────────────────────────────────────────────────

    [Fact] public async Task FsSource_NoLogFolder_400()
        => Assert.Equal("LogFolderRequired", ErrorOf(await _ctrl.CreateScanSource(JobId, Req("fs", FsType), default)));

    [Fact] public async Task FileContentSource_NoLogFolder_400()
        => Assert.Equal("LogFolderRequired", ErrorOf(await _ctrl.CreateScanSource(JobId, Req("fc", FcType), default)));

    [Fact] public async Task DbSource_NoConnectionName_400()
        => Assert.Equal("ConnectionNameRequired", ErrorOf(await _ctrl.CreateScanSource(JobId, Req("db", DbType), default)));

    [Fact] public async Task ApiSource_NoUrl_400()
        => Assert.Equal("LogSourceUrlRequired", ErrorOf(await _ctrl.CreateScanSource(JobId, Req("api", ApiType), default)));

    [Fact] public async Task IncludeSubfolders_OnDbSource_400()
        => Assert.Equal("IncludeSubfoldersInvalidForType",
            ErrorOf(await _ctrl.CreateScanSource(JobId, Req("db", DbType, connectionName: "C", includeSubfolders: true), default)));

    [Fact] public async Task EmptyName_400()
        => Assert.Equal("SourceNameRequired", ErrorOf(await _ctrl.CreateScanSource(JobId, Req("  ", FsType, logFolder: "c:/x"), default)));

    [Fact] public async Task UnknownScanType_400()
        => Assert.Equal("UnknownScanType", ErrorOf(await _ctrl.CreateScanSource(JobId, Req("x", 999, logFolder: "c:/x"), default)));

    [Fact]
    public async Task DuplicateActiveName_400()
    {
        await CreateOkAsync(Req("Logs", FsType, logFolder: "c:/a"));
        Assert.Equal("SourceNameDuplicate",
            ErrorOf(await _ctrl.CreateScanSource(JobId, Req("Logs", DbType, connectionName: "C"), default)));
    }

    /// <summary>
    /// Two ACTIVE FS sources of one job may not share a LogFolder: watermarks are
    /// keyed (MonitoredJobId, FilePath) — NOT per source — so they'd fight over the
    /// same watermark rows (silent data loss). Guard lifts when watermarks re-key.
    /// </summary>
    [Fact]
    public async Task SameLogFolder_TwoFileBasedSources_400_WatermarkGrainGuard()
    {
        await CreateOkAsync(Req("logs-A", FsType, logFolder: @"C:\Shared\In"));
        // different name, same folder (case-insensitive), FileContent vs FS — still conflicts
        Assert.Equal("SourceFolderConflict",
            ErrorOf(await _ctrl.CreateScanSource(JobId, Req("content-B", FcType, logFolder: @"c:\shared\in"), default)));
    }

    [Fact]
    public async Task DifferentFolders_TwoFileBasedSources_Ok()
    {
        await CreateOkAsync(Req("logs-A", FsType, logFolder: @"C:\In\A"));
        var r = await _ctrl.CreateScanSource(JobId, Req("logs-B", FsType, logFolder: @"C:\In\B"), default);
        Assert.IsType<OkObjectResult>(r);
    }

    [Fact]
    public async Task TwoDbSources_SameJob_Ok_NoCardinalityLimit()
    {
        await CreateOkAsync(Req("db-1", DbType, connectionName: "ConnA"));
        var r = await _ctrl.CreateScanSource(JobId, Req("db-2", DbType, connectionName: "ConnB"), default);
        Assert.IsType<OkObjectResult>(r);   // multiple sources of one type allowed
    }

    // ── ScanType immutability ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateChangingScanType_400_Immutable()
    {
        var id = await CreateOkAsync(Req("src", FsType, logFolder: "c:/x"));
        var put = await _ctrl.UpdateScanSource(id, Req("src", DbType, connectionName: "C"), default);
        Assert.Equal("ScanTypeImmutable", ErrorOf(put));
    }

    [Fact]
    public async Task UpdateSameType_EditsConfig_Ok()
    {
        var id = await CreateOkAsync(Req("src", FsType, logFolder: @"C:\old"));
        var put = await _ctrl.UpdateScanSource(id, Req("src", FsType, logFolder: @"C:\new"), default);
        Assert.IsType<NoContentResult>(put);
        Assert.Equal(@"C:\new", (await _db.ScanSources.FindAsync(id))!.LogFolder);
    }

    // ── Soft-delete cascade ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSource_SoftDeletes_AndDeactivatesChildRules()
    {
        var id = await CreateOkAsync(Req("src", FsType, logFolder: "c:/x"));
        // add a rule to the source via the source-scoped endpoint
        await _ctrl.CreateScanRuleForSource(id, new UpsertScanCheckRuleRequest(
            CheckType: "ErrorKeyword", SourceTable: null, TargetField: "ERROR",
            MinValue: null, MaxValue: null, ExpectedValue: null, WatermarkColumn: null,
            SourceIdColumn: null, Severity: "Medium", Description: null), default);

        var del = await _ctrl.DeleteScanSource(id, default);
        Assert.IsType<NoContentResult>(del);

        // Re-read from the shared in-memory store (controller used separate contexts).
        var src  = await _db.ScanSources.AsNoTracking().FirstAsync(s => s.ScanSourceId == id);
        var rule = await _db.ScanCheckRules.AsNoTracking().FirstAsync(r => r.ScanSourceId == id);
        Assert.False(src.IsActive);
        Assert.False(rule.IsActive);   // cascade soft-delete
    }

    // ── Source-scoped rule create attribution ─────────────────────────────────────

    [Fact]
    public async Task CreateRuleForSource_SetsScanSourceId_AndJobId()
    {
        var id = await CreateOkAsync(Req("src", FsType, logFolder: "c:/x"));
        var r = await _ctrl.CreateScanRuleForSource(id, new UpsertScanCheckRuleRequest(
            CheckType: "ErrorKeyword", SourceTable: null, TargetField: "FAIL",
            MinValue: null, MaxValue: null, ExpectedValue: null, WatermarkColumn: null,
            SourceIdColumn: null, Severity: "High", Description: null), default);
        Assert.IsType<OkObjectResult>(r);
        var rule = await _db.ScanCheckRules.FirstAsync(x => x.ScanSourceId == id);
        Assert.Equal(id,    rule.ScanSourceId);
        Assert.Equal(JobId, rule.MonitoredJobId);
    }

    // ── Audit Detail format ───────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_Create_Update_Delete_DetailFormat()
    {
        var id = await CreateOkAsync(Req("Events", FcType, logFolder: @"C:\In"));
        Assert.Contains(_audit.Rows, a => a.EntityType == "ScanSource" && a.EventType == "ScanSourceCreated"
            && a.Detail!.Contains("Created ScanSource 'Events'") && a.Detail.Contains("ScanTypeId=4"));

        await _ctrl.UpdateScanSource(id, Req("Events-Renamed", FcType, logFolder: @"C:\In"), default);
        Assert.Contains(_audit.Rows, a => a.EventType == "ScanSourceUpdated" && a.Detail!.Contains("Name: 'Events' → 'Events-Renamed'"));

        await _ctrl.DeleteScanSource(id, default);
        Assert.Contains(_audit.Rows, a => a.EventType == "ScanSourceDeleted" && a.Detail!.Contains("Soft-deleted ScanSource"));
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────

    private sealed class CapturingAudit : IAuditRepository
    {
        public List<AuditLog> Rows { get; } = new();
        public Task WriteAsync(AuditLog audit, CancellationToken ct = default) { Rows.Add(audit); return Task.CompletedTask; }
        public Task<PagedResult<AuditLog>> QueryAsync(AuditLogFilter filter, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options) : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
