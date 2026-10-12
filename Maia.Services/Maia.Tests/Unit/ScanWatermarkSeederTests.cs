using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// "Start scanning from now" seeding (<see cref="ScanWatermarkSeeder"/>). Pins:
/// (1) FileSystem sources seed each pattern-matching file's byte length, top-level
/// only, through the insert-only repo call; (2) FileContent sources seed every
/// file's mtime (recursive when IncludeSubfolders); (3) existing rows are kept, not
/// overwritten; (4) a missing folder / missing connection string is a Warning, never
/// a throw (the save already succeeded); (5) rule kinds without a watermark are
/// NotApplicable; (6) SqlQuery rules take the in-memory max of the WatermarkColumn
/// and report a missing column as a warning. The table-rule SQL path needs a live
/// SqlConnection and is covered by the manual acceptance run, not here.
/// </summary>
public sealed class ScanWatermarkSeederTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maia-seed-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IScanWatermarkRepository> _repo = new(MockBehavior.Strict);
    private readonly Mock<ISqlQueryRunner> _sql = new(MockBehavior.Strict);

    public ScanWatermarkSeederTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private ScanWatermarkSeeder Seeder(Dictionary<string, string?>? conn = null)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(conn ?? new Dictionary<string, string?>())
            .Build();
        return new ScanWatermarkSeeder(cfg, _repo.Object, _sql.Object, NullLogger<ScanWatermarkSeeder>.Instance);
    }

    private ScanSource Source(string? patterns = null, bool recurse = false) => new()
    {
        ScanSourceId = 42, MonitoredJobId = 7, Name = "src", ScanTypeId = 1,
        LogFolder = _dir, SearchPatterns = patterns, IncludeSubfolders = recurse,
    };

    private string Put(string relative, string content)
    {
        var full = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    // ── FileSystem ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FileSystem_seeds_matching_files_at_their_current_length_top_level_only()
    {
        var a = Put("app1.log", "0123456789");         // 10 bytes, matches
        Put("notes.txt", "xx");                        // no match
        Put("sub/app2.log", "yyy");                    // sub-folder — FS scan is top-level only
        _repo.Setup(r => r.TryAddFileOffsetAsync(7, 42, a, 10, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var res = await Seeder().SeedSourceFromNowAsync(Source("app*.log"), ScanType.FileSystem);

        Assert.True(res.Applicable);
        Assert.Equal(1, res.Seeded);
        Assert.Equal(0, res.Skipped);
        Assert.Null(res.Warning);
        _repo.VerifyAll();
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileSystem_defaults_to_star_dot_log_when_no_patterns_configured()
    {
        var a = Put("x.log", "abc");
        Put("x.txt", "abc");
        _repo.Setup(r => r.TryAddFileOffsetAsync(7, 42, a, 3, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var res = await Seeder().SeedSourceFromNowAsync(Source(patterns: null), ScanType.FileSystem);

        Assert.Equal(1, res.Seeded);
        _repo.VerifyAll();
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileSystem_keeps_existing_watermark_rows_and_counts_them_as_skipped()
    {
        var a = Put("a.log", "1234");
        _repo.Setup(r => r.TryAddFileOffsetAsync(7, 42, a, 4, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var res = await Seeder().SeedSourceFromNowAsync(Source(), ScanType.FileSystem);

        Assert.Equal(0, res.Seeded);
        Assert.Equal(1, res.Skipped);
        Assert.Null(res.Warning);
        // The seeder must never fall back to the overwriting Update* call.
        _repo.Verify(r => r.UpdateFileOffsetAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Missing_folder_is_a_warning_not_a_throw()
    {
        var src = Source();
        src.LogFolder = Path.Combine(_dir, "does-not-exist");

        var res = await Seeder().SeedSourceFromNowAsync(src, ScanType.FileSystem);

        Assert.True(res.Applicable);
        Assert.Equal(0, res.Seeded);
        Assert.NotNull(res.Warning);
        Assert.Contains("not found", res.Warning);
        _repo.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ScanType.Database)]
    [InlineData(ScanType.ApiEndpoint)]
    public async Task Non_file_sources_are_not_applicable(ScanType type)
    {
        var res = await Seeder().SeedSourceFromNowAsync(Source(), type);

        Assert.False(res.Applicable);
        Assert.Null(res.Warning);
        _repo.VerifyNoOtherCalls();
    }

    // ── FileContent ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FileContent_seeds_every_file_mtime_recursively_when_configured()
    {
        var a = Put("WARNING1.xml", "<a/>");
        var b = Put("deep/WARNING2.xml", "<b/>");
        var ma = File.GetLastWriteTime(a);
        var mb = File.GetLastWriteTime(b);
        _repo.Setup(r => r.TryAddContentWatermarkAsync(7, 42, a, ma, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _repo.Setup(r => r.TryAddContentWatermarkAsync(7, 42, b, mb, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var res = await Seeder().SeedSourceFromNowAsync(Source(recurse: true), ScanType.FileContent);

        Assert.Equal(2, res.Seeded);
        _repo.VerifyAll();
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileContent_top_level_only_when_not_recursive()
    {
        var a = Put("W.xml", "<a/>");
        Put("deep/W2.xml", "<b/>");
        _repo.Setup(r => r.TryAddContentWatermarkAsync(7, 42, a, File.GetLastWriteTime(a), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var res = await Seeder().SeedSourceFromNowAsync(Source(recurse: false), ScanType.FileContent);

        Assert.Equal(1, res.Seeded);
        _repo.VerifyAll();
        _repo.VerifyNoOtherCalls();
    }

    // ── DB rules ────────────────────────────────────────────────────────────────

    private static ScanCheckRule DbRule(CheckType type, string? watermark, string? table = "dbo.T") => new()
    {
        CheckRuleId = 99, MonitoredJobId = 7, ScanSourceId = 42,
        CheckType = type, SourceTable = table, TargetField = "Status", WatermarkColumn = watermark,
        ExpectedValue = "5",
    };

    [Fact]
    public async Task ErrorKeyword_rule_seeds_its_sources_file_offsets()
    {
        var a = Put("a.log", "12345");
        _repo.Setup(r => r.TryAddFileOffsetAsync(7, 42, a, 5, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var res = await Seeder().SeedRuleFromNowAsync(DbRule(CheckType.ErrorKeyword, watermark: null, table: null), Source());

        Assert.True(res.Applicable);
        Assert.Equal(1, res.Seeded);
        _repo.VerifyAll();
        _sql.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileContent_rule_seeds_its_sources_content_watermarks()
    {
        var a = Put("W.xml", "<a/>");
        _repo.Setup(r => r.TryAddContentWatermarkAsync(7, 42, a, File.GetLastWriteTime(a), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var res = await Seeder().SeedRuleFromNowAsync(DbRule(CheckType.FileContent, watermark: null, table: null), Source());

        Assert.True(res.Applicable);
        Assert.Equal(1, res.Seeded);
        _repo.VerifyAll();
        _sql.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Db_rule_without_watermark_column_is_not_applicable()
    {
        var res = await Seeder().SeedRuleFromNowAsync(DbRule(CheckType.ValueEquals, watermark: null), Source());

        Assert.False(res.Applicable);
        Assert.Null(res.Warning);
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_connection_string_is_a_warning_not_a_throw()
    {
        var src = Source(); src.ConnectionName = "Nope";

        var res = await Seeder().SeedRuleFromNowAsync(DbRule(CheckType.ValueEquals, "UpdateDate"), src);

        Assert.True(res.Applicable);
        Assert.NotNull(res.Warning);
        Assert.Contains("'Nope'", res.Warning);
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SqlQuery_rule_seeds_in_memory_max_of_watermark_column()
    {
        var src = Source(); src.ConnectionName = "B2B";
        var cfg = new Dictionary<string, string?> { ["ConnectionStrings:B2B"] = "Server=x;Database=y;" };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Status"] = 5, ["UpdateDate"] = new DateTime(2026, 10, 1, 8, 0, 0) },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Status"] = 5, ["UpdateDate"] = new DateTime(2026, 10, 12, 9, 30, 0) },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Status"] = 5, ["UpdateDate"] = null },
        };
        _sql.Setup(s => s.ExecuteAsync("Server=x;Database=y;", "SELECT * FROM T WHERE Status=5", 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);
        _repo.Setup(r => r.TryAddDbWatermarkAsync(99, "2026-10-12 09:30:00.0000000", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var res = await Seeder(cfg).SeedRuleFromNowAsync(DbRule(CheckType.SqlQuery, "UpdateDate", "SELECT * FROM T WHERE Status=5"), src);

        Assert.True(res.Applicable);
        Assert.Equal(1, res.Seeded);
        Assert.Null(res.Warning);
        _repo.VerifyAll();
        _sql.VerifyAll();
    }

    [Fact]
    public async Task SqlQuery_rule_with_no_matching_rows_seeds_nothing_without_warning()
    {
        var cfg = new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Server=x;" };
        _sql.Setup(s => s.ExecuteAsync("Server=x;", "Q", 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IReadOnlyDictionary<string, object?>>());

        var res = await Seeder(cfg).SeedRuleFromNowAsync(DbRule(CheckType.SqlQuery, "Id", "Q"), Source());

        Assert.True(res.Applicable);
        Assert.Equal(0, res.Seeded);
        Assert.Null(res.Warning);
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SqlQuery_rule_missing_watermark_column_in_result_is_a_warning()
    {
        var cfg = new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Server=x;" };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Status"] = 5 },
        };
        _sql.Setup(s => s.ExecuteAsync("Server=x;", "Q", 500, It.IsAny<CancellationToken>())).ReturnsAsync(rows);

        var res = await Seeder(cfg).SeedRuleFromNowAsync(DbRule(CheckType.SqlQuery, "UpdateDate", "Q"), Source());

        Assert.NotNull(res.Warning);
        Assert.Contains("'UpdateDate'", res.Warning);
        _repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sql_exception_is_a_warning_not_a_throw()
    {
        var cfg = new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Server=x;" };
        _sql.Setup(s => s.ExecuteAsync("Server=x;", "Q", 500, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("login failed"));

        var res = await Seeder(cfg).SeedRuleFromNowAsync(DbRule(CheckType.SqlQuery, "Id", "Q"), Source());

        Assert.True(res.Applicable);
        Assert.Equal(0, res.Seeded);
        Assert.Contains("login failed", res.Warning);
    }
}
