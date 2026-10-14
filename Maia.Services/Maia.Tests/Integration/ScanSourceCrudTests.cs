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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// Pins the Tier 2.5 phase-(d1) ScanSource CRUD + validation matrix on the config
/// controllers (post-decomposition: ScanSource ops on ScanSourcesConfigController,
/// source-scoped rule create on ScanRulesConfigController): per-type config
/// requirements, name rules, ScanType immutability, the SourceFolderConflict
/// watermark-grain guard, and the soft-delete cascade to child rules. Controllers
/// exercised directly over an in-memory MaiaDbContext (ScanTypes 1-4 come from seed HasData).
/// </summary>
public class ScanSourceCrudTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private ScanSourcesConfigController _ctrl = null!;
    private ScanRulesConfigController _rules = null!;
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
        var factory     = new TestDbContextFactory(options);
        var currentUser = Mock.Of<ICurrentUserAccessor>();
        // Every connection name the tests use must exist — unknown keys are a 400.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=x;",
            ["ConnectionStrings:C"]                 = "Server=x;",
            ["ConnectionStrings:ConnA"]             = "Server=x;",
            ["ConnectionStrings:ConnB"]             = "Server=x;",
            ["ConnectionStrings:Ora"]               = "Data Source=ORCL;User Id=u;Password=p;",
            ["ConnectionStrings:Ora_ProviderName"]  = "Oracle",
            ["ConnectionStrings:Odd"]               = "Server=x;",
            ["ConnectionStrings:Odd_ProviderName"]  = "MySql",
        }).Build();
        _ctrl = new ScanSourcesConfigController(
            factory, _audit, Mock.Of<IScanWatermarkSeeder>(), config, currentUser,
            NullLogger<ScanSourcesConfigController>.Instance);
        _rules = new ScanRulesConfigController(
            factory,
            new IFileContentExtractor[] { new XmlContentExtractor(NullLogger<XmlContentExtractor>.Instance) },
            _audit, Mock.Of<IScanWatermarkSeeder>(), config, currentUser,
            NullLogger<ScanRulesConfigController>.Instance);
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

    [Fact] public async Task DbSource_UnknownConnectionName_400()
        => Assert.Equal("ConnectionNameUnknown", ErrorOf(await _ctrl.CreateScanSource(JobId, Req("db", DbType, connectionName: "Nope"), default)));

    [Fact]
    public async Task UpdateDbSource_ToUnknownConnectionName_400()
    {
        var id = await CreateOkAsync(Req("db", DbType, connectionName: "C"));
        Assert.Equal("ConnectionNameUnknown",
            ErrorOf(await _ctrl.UpdateScanSource(id, Req("db", DbType, connectionName: "Gone"), default)));
    }

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
        await _rules.CreateScanRuleForSource(id, new UpsertScanCheckRuleRequest(
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
        var r = await _rules.CreateScanRuleForSource(id, new UpsertScanCheckRuleRequest(
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

    // ── Composite scan rules (Phase 1 — DB table rules) ──────────────────────────

    private static UpsertScanCheckRuleRequest DbRule(string checkType, string target, string? expected = null,
        decimal? min = null, decimal? max = null, IReadOnlyList<ScanRuleConditionDto>? conditions = null)
        => new(CheckType: checkType, SourceTable: checkType == "SqlQuery" ? "SELECT 1 AS X WHERE 1=0" : "dbo.Files",
               TargetField: target, MinValue: min, MaxValue: max, ExpectedValue: expected,
               WatermarkColumn: null, SourceIdColumn: "Id", Severity: "Medium", Description: null,
               Conditions: conditions);

    private async Task<int> DbSourceAsync() => await CreateOkAsync(Req("db", DbType, connectionName: "DefaultConnection"));

    // ── Oracle connections ({Name}_ProviderName) ─────────────────────────────────

    [Fact] public async Task DbSource_OracleConnection_is_accepted()
        => await CreateOkAsync(Req("ora", DbType, connectionName: "Ora"));

    [Fact] public async Task DbSource_UnknownProvider_400()
        => Assert.Equal("ConnectionProviderUnknown",
            ErrorOf(await _ctrl.CreateScanSource(JobId, Req("odd", DbType, connectionName: "Odd"), default)));

    [Fact] public async Task DbSource_ProviderCompanionKey_is_not_a_connection_400()
        => Assert.Equal("ConnectionNameUnknown",
            ErrorOf(await _ctrl.CreateScanSource(JobId, Req("ora", DbType, connectionName: "Ora_ProviderName"), default)));

    [Theory]
    [InlineData("EXEC check_orders(60)")]
    [InlineData("  begin check_orders; end;")]
    [InlineData("CALL check_orders()")]
    public async Task OracleSource_SqlQuery_that_is_not_a_SELECT_is_rejected(string query)
    {
        var sid = await CreateOkAsync(Req("ora", DbType, connectionName: "Ora"));
        var r = await _rules.CreateScanRuleForSource(sid, DbRule("SqlQuery", "X") with { SourceTable = query }, default);
        Assert.Equal("OracleQueryNotSelect", ErrorOf(r));
    }

    [Fact]
    public async Task OracleSource_SqlQuery_SELECT_is_accepted_and_SqlServer_EXEC_still_is()
    {
        var ora = await CreateOkAsync(Req("ora", DbType, connectionName: "Ora"));
        Assert.IsType<OkObjectResult>(await _rules.CreateScanRuleForSource(ora,
            DbRule("SqlQuery", "X") with { SourceTable = "SELECT order_id, 1 AS x FROM orders WHERE stuck = 1;" }, default));

        var mssql = await DbSourceAsync();
        Assert.IsType<OkObjectResult>(await _rules.CreateScanRuleForSource(mssql,
            DbRule("SqlQuery", "X") with { SourceTable = "EXEC sp_CheckStuckOrders @threshold=60" }, default));
    }

    [Fact]
    public async Task Conditions_on_a_SqlQuery_rule_are_rejected_ConditionsNotSupported()
    {
        var sid = await DbSourceAsync();
        var r = await _rules.CreateScanRuleForSource(sid,
            DbRule("SqlQuery", "X", conditions: [new ScanRuleConditionDto(1, "ValueEquals", "Region", "EU")]), default);
        Assert.Equal("ConditionsNotSupported", ErrorOf(r));
    }

    [Fact]
    public async Task Conditions_on_an_ErrorKeyword_rule_are_rejected_ConditionsNotSupported()
    {
        var sid = await CreateOkAsync(Req("fs", FsType, logFolder: "c:/x"));
        var r = await _rules.CreateScanRuleForSource(sid, new UpsertScanCheckRuleRequest(
            CheckType: "ErrorKeyword", SourceTable: null, TargetField: "ERROR",
            MinValue: null, MaxValue: null, ExpectedValue: null, WatermarkColumn: null,
            SourceIdColumn: null, Severity: "Medium", Description: null,
            Conditions: [new ScanRuleConditionDto(1, "ErrorKeyword", "timeout")]), default);
        Assert.Equal("ConditionsNotSupported", ErrorOf(r));
    }

    [Fact]
    public async Task Condition_with_a_non_DB_kind_is_rejected_ConditionKindMismatch()
    {
        var sid = await DbSourceAsync();
        var r = await _rules.CreateScanRuleForSource(sid,
            DbRule("ValueEquals", "Status", "5", conditions: [new ScanRuleConditionDto(1, "FileContent", "/x/y", "v")]), default);
        Assert.Equal("ConditionKindMismatch", ErrorOf(r));
    }

    [Theory]
    [InlineData("ValueEquals", "Region", null, null, null)]   // no expected value
    [InlineData("ColumnRange", "Amount", null, null, null)]   // neither bound
    [InlineData("ValueEquals", "   ",    "EU", null, null)]   // no column
    public async Task Incomplete_condition_is_rejected_ConditionIncomplete(string kind, string col, string? expected, double? min, double? max)
    {
        var sid = await DbSourceAsync();
        var r = await _rules.CreateScanRuleForSource(sid,
            DbRule("ValueEquals", "Status", "5", conditions:
                [new ScanRuleConditionDto(1, kind, col, expected, (decimal?)min, (decimal?)max)]), default);
        Assert.Equal("ConditionIncomplete", ErrorOf(r));
    }

    [Fact]
    public async Task Create_persists_conditions_packed_1_to_N_with_kind_irrelevant_fields_nulled()
    {
        var sid = await DbSourceAsync();
        var r = await _rules.CreateScanRuleForSource(sid, DbRule("ValueEquals", "Status", "5", conditions:
        [
            new ScanRuleConditionDto(7, "ColumnRange", "Amount", "stale", 10m, 100m),   // ExpectedValue must be dropped
            new ScanRuleConditionDto(3, "ValueEquals", " Region ", "EU", 1m, 2m),        // bounds must be dropped, column trimmed
        ]), default);
        Assert.IsType<OkObjectResult>(r);

        var rule = await _db.ScanCheckRules.AsNoTracking().Include(x => x.Conditions).FirstAsync(x => x.ScanSourceId == sid);
        var conds = rule.Conditions.OrderBy(c => c.ConditionOrder).ToList();
        Assert.Equal(2, conds.Count);
        Assert.Equal(1, conds[0].ConditionOrder); Assert.Equal(Maia.Core.Enums.CheckType.ValueEquals, conds[0].Kind);
        Assert.Equal("Region", conds[0].TargetField); Assert.Equal("EU", conds[0].ExpectedValue);
        Assert.Null(conds[0].MinValue); Assert.Null(conds[0].MaxValue);
        Assert.Equal(2, conds[1].ConditionOrder); Assert.Equal(Maia.Core.Enums.CheckType.ColumnRange, conds[1].Kind);
        Assert.Null(conds[1].ExpectedValue); Assert.Equal(10m, conds[1].MinValue); Assert.Equal(100m, conds[1].MaxValue);
        Assert.All(conds, c => Assert.Null(c.PredicateType));

        Assert.Contains(_audit.Rows, a => a.EventType == "ScanRuleCreated"
            && a.Detail!.Contains("2 condition(s): 1:ValueEquals Region=EU AND 2:ColumnRange Amount[10, 100]"));
    }

    [Fact]
    public async Task Update_replaces_all_conditions_and_audits_before_after()
    {
        var sid = await DbSourceAsync();
        var created = await _rules.CreateScanRuleForSource(sid, DbRule("ValueEquals", "Status", "5"), default);
        var ruleId = (int)((OkObjectResult)created).Value!.GetType().GetProperty("CheckRuleId")!.GetValue(((OkObjectResult)created).Value)!;

        // Add two conditions
        var put1 = await _rules.UpdateScanRule(ruleId, DbRule("ValueEquals", "Status", "5", conditions:
            [new ScanRuleConditionDto(1, "ValueEquals", "Region", "EU"), new ScanRuleConditionDto(2, "ColumnRange", "Amount", null, 10m, null)]), default);
        Assert.IsType<NoContentResult>(put1);
        Assert.Equal(2, await _db.ScanCheckRuleConditions.AsNoTracking().CountAsync(c => c.CheckRuleId == ruleId));
        Assert.Contains(_audit.Rows, a => a.EventType == "ScanRuleUpdated"
            && a.Detail!.Contains("Conditions: '(none)' → '1:ValueEquals Region=EU AND 2:ColumnRange Amount[10, +∞]'"));

        // Replace with one — the old rows must be gone, not accumulated
        var put2 = await _rules.UpdateScanRule(ruleId, DbRule("ValueEquals", "Status", "5", conditions:
            [new ScanRuleConditionDto(1, "ValueEquals", "Country", "IL")]), default);
        Assert.IsType<NoContentResult>(put2);
        var remaining = await _db.ScanCheckRuleConditions.AsNoTracking().Where(c => c.CheckRuleId == ruleId).ToListAsync();
        Assert.Equal("Country", Assert.Single(remaining).TargetField);

        // Omitting conditions clears them (replace-all contract)
        var put3 = await _rules.UpdateScanRule(ruleId, DbRule("ValueEquals", "Status", "5"), default);
        Assert.IsType<NoContentResult>(put3);
        Assert.Equal(0, await _db.ScanCheckRuleConditions.AsNoTracking().CountAsync(c => c.CheckRuleId == ruleId));
        Assert.Contains(_audit.Rows, a => a.EventType == "ScanRuleUpdated" && a.Detail!.Contains("→ '(none)'"));
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
