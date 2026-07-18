using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.Placeholders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// PlaceholderResolver is the single source of truth for {token} substitution
/// across SqlScript / Script / CopyFile / composite-step payloads. These tests
/// pin: every recognised token maps to the right field, unknown tokens stay
/// literal, case-insensitive match, strict-mode produces the specific
/// SourceFilePath error, and a stale rec (no failure row) survives gracefully.
/// </summary>
public class PlaceholderResolverTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;
    private PlaceholderResolver _resolver = null!;

    // Above the MaiaDbContext seed-data ranges so the tests start clean.
    private const int FailureId      = 9001;
    private const int MonitoredJobId = 9100;
    private const int JobTypeId      = 9200;
    private const int ScanSourceId   = 9300;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobTypes.Add(new JobType { JobTypeId = JobTypeId, Name = "TestJobType" });
        _db.MonitoredJobs.Add(new MonitoredJob
        {
            MonitoredJobId = MonitoredJobId,
            Name           = "TestJob",
            JobTypeId      = JobTypeId,
        });
        _db.ScanSources.Add(new ScanSource
        {
            ScanSourceId   = ScanSourceId,
            MonitoredJobId = MonitoredJobId,
            ScanTypeId     = 1,
            Name           = "TestSource",
            LogFolder      = @"C:\logs\test",
            InputFolder    = @"C:\input\test",
        });
        _db.JobFailures.Add(new JobFailure
        {
            FailureId      = FailureId,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            ScanSourceId   = ScanSourceId,
            SourceId       = "deposit-abc-123",
            SourceLogPath  = @"C:\logs\test\app.log",
            SourceFilePath = @"C:\input\test\deposit_20260601.txt",
            Status         = JobStatus.Failed,
        });
        await _db.SaveChangesAsync();

        _resolver = new PlaceholderResolver(_factory);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_AllKnownPlaceholders_SubstitutesEachField()
    {
        var rec = MakeRec(FailureId);
        var template =
            "fid={failureId} sid={sourceId} log={sourceLogPath} " +
            "file={sourceFilePath} jf={jobFolder} if={inputFolder}";

        var result = await _resolver.ResolveAsync(template, rec);

        Assert.Equal(
            $"fid={FailureId} sid=deposit-abc-123 log=C:\\logs\\test\\app.log " +
            "file=C:\\input\\test\\deposit_20260601.txt jf=C:\\logs\\test if=C:\\input\\test",
            result);
    }

    [Fact]
    public async Task Resolve_UnknownPlaceholder_LeftLiteral()
    {
        var rec = MakeRec(FailureId);
        // {timestamp} is intentionally not in the resolver's token set; v1
        // forbids it (see CLAUDE.md decision on stable placeholders only).
        var result = await _resolver.ResolveAsync("hello {timestamp} world {failureId}", rec);

        Assert.Equal($"hello {{timestamp}} world {FailureId}", result);
    }

    [Fact]
    public async Task Resolve_CaseInsensitiveTokenMatch()
    {
        var rec = MakeRec(FailureId);
        var result = await _resolver.ResolveAsync(
            "{FAILUREID} {SourceId} {SOURCEfilepath}", rec);

        Assert.Equal(
            $"{FailureId} deposit-abc-123 C:\\input\\test\\deposit_20260601.txt",
            result);
    }

    [Fact]
    public async Task ResolveOrThrow_NullSourceFilePath_ThrowsSpecificError()
    {
        // Failure with no SourceFilePath — operator forgot to configure
        // InputPathPattern / FilePathColumn. The error must point them at
        // the fix, not just say "empty value".
        _db.JobFailures.Add(new JobFailure
        {
            FailureId      = FailureId + 1,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            ScanSourceId   = ScanSourceId,
            SourceId       = "x",
            SourceLogPath  = @"C:\logs\test\app.log",
            SourceFilePath = null,                          // ← the case under test
            Status         = JobStatus.Failed,
        });
        await _db.SaveChangesAsync();

        var rec = MakeRec(FailureId + 1);
        var ex = await Assert.ThrowsAsync<PlaceholderUnresolvedException>(() =>
            _resolver.ResolveOrThrowAsync(
                "{sourceFilePath}|C:\\dest",
                rec,
                new[] { "sourceFilePath" }));

        Assert.Equal("sourceFilePath", ex.PlaceholderName);
        Assert.Contains("InputPathPattern", ex.Message);
        Assert.Contains("FilePathColumn", ex.Message);
        Assert.Contains($"failure {FailureId + 1}", ex.Message);
        Assert.Contains("'TestJob'", ex.Message);
    }

    [Fact]
    public async Task Resolve_NullSourceFilePath_NonStrictReturnsEmpty()
    {
        // Same null SourceFilePath case — but via non-strict ResolveAsync.
        // Should resolve to empty (not throw), so SqlScript/Script callers
        // can choose whether to defend against empty values themselves.
        _db.JobFailures.Add(new JobFailure
        {
            FailureId      = FailureId + 2,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            ScanSourceId   = ScanSourceId,
            SourceId       = "y",
            SourceLogPath  = @"C:\logs\test\app.log",
            SourceFilePath = null,
            Status         = JobStatus.Failed,
        });
        await _db.SaveChangesAsync();

        var rec    = MakeRec(FailureId + 2);
        var result = await _resolver.ResolveAsync(
            "before|{sourceFilePath}|after", rec);

        Assert.Equal("before||after", result);
    }

    [Fact]
    public async Task Resolve_FailureNotFound_AllPlaceholdersResolveSafely()
    {
        // Rec references a FailureId that doesn't exist in the DB — could
        // happen if the rec is stale or the failure was deleted out from
        // under it. The resolver must NOT throw; placeholders resolve to
        // empty strings except {failureId} which is on the rec itself.
        var rec = MakeRec(999999);

        var result = await _resolver.ResolveAsync(
            "{failureId} {sourceId} {sourceFilePath} {jobFolder}", rec);

        Assert.Equal("999999   ", result);
    }

    [Fact]
    public async Task Resolve_SourceFileName_IsFilenameSliceOfSourceFilePath()
    {
        // {sourceFileName} = Path.GetFileName({sourceFilePath}) — lets a CopyFile
        // dest reuse the original name under a different folder.
        var rec = MakeRec(FailureId);
        var result = await _resolver.ResolveAsync(
            "name={sourceFileName} dest={inputFolder}\\{sourceFileName}", rec);

        Assert.Equal(
            "name=deposit_20260601.txt dest=C:\\input\\test\\deposit_20260601.txt",
            result);
    }

    [Fact]
    public async Task Resolve_SourceFileName_EmptyWhenNoSourceFilePath()
    {
        _db.JobFailures.Add(new JobFailure
        {
            FailureId      = FailureId + 3,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            ScanSourceId   = ScanSourceId,
            SourceId       = "z",
            SourceLogPath  = @"C:\logs\test\app.log",
            SourceFilePath = null,
            Status         = JobStatus.Failed,
        });
        await _db.SaveChangesAsync();

        var rec    = MakeRec(FailureId + 3);
        var result = await _resolver.ResolveAsync("[{sourceFileName}]", rec);

        Assert.Equal("[]", result);
    }

    // ── ResolveSqlAsync (SQL-injection-safe parameterization) ────────────────

    [Fact]
    public async Task ResolveSql_QuotedPlaceholder_BecomesBareParameter()
    {
        // The dominant operator pattern (and what the UI helper appends):
        // WHERE Id = '{sourceId}'. The surrounding quotes must be consumed so
        // the result is a bare @p0 parameter, not the literal '@p0'.
        var rec    = MakeRec(FailureId);
        var result = await _resolver.ResolveSqlAsync(
            "UPDATE dbo.Files SET Status = 1 WHERE Id = '{sourceId}'", rec);

        Assert.Equal("UPDATE dbo.Files SET Status = 1 WHERE Id = @p0", result.Sql);
        var p = Assert.Single(result.Parameters);
        Assert.Equal("@p0", p.Name);
        Assert.Equal("deposit-abc-123", p.Value);
    }

    [Fact]
    public async Task ResolveSql_BarePlaceholder_BecomesParameter()
    {
        // Numeric/unquoted context: WHERE Id = {failureId}.
        var rec    = MakeRec(FailureId);
        var result = await _resolver.ResolveSqlAsync(
            "DELETE FROM dbo.Files WHERE Id = {failureId}", rec);

        Assert.Equal("DELETE FROM dbo.Files WHERE Id = @p0", result.Sql);
        Assert.Equal(FailureId.ToString(), Assert.Single(result.Parameters).Value);
    }

    [Fact]
    public async Task ResolveSql_MultiplePlaceholders_NumberedInOrder()
    {
        var rec    = MakeRec(FailureId);
        var result = await _resolver.ResolveSqlAsync(
            "UPDATE t SET Path = '{sourceFilePath}' WHERE Id = '{sourceId}' AND Ref = '{referenceId}'",
            rec);

        Assert.Equal("UPDATE t SET Path = @p0 WHERE Id = @p1 AND Ref = @p2", result.Sql);
        Assert.Collection(result.Parameters,
            p => { Assert.Equal("@p0", p.Name); Assert.Equal(@"C:\input\test\deposit_20260601.txt", p.Value); },
            p => { Assert.Equal("@p1", p.Name); Assert.Equal("deposit-abc-123", p.Value); },
            p => { Assert.Equal("@p2", p.Name); Assert.Equal(string.Empty, p.Value); }); // ReferenceId null → empty
    }

    [Fact]
    public async Task ResolveSql_MaliciousSourceId_IsInertParameterValue_NotInjected()
    {
        // THE regression test for the second-order SQL-injection hole. A scanned
        // row whose id is a tautology must NOT alter the SQL structure — it stays
        // a single bound value, so WHERE Id = @p0 matches the literal string
        // "' OR '1'='1", i.e. nothing. No bulk mutation.
        const string attack = "' OR '1'='1";
        _db.JobFailures.Add(new JobFailure
        {
            FailureId      = FailureId + 10,
            JobTypeId      = JobTypeId,
            MonitoredJobId = MonitoredJobId,
            ScanSourceId   = ScanSourceId,
            SourceId       = attack,
            SourceLogPath  = @"C:\logs\test\app.log",
            Status         = JobStatus.Failed,
        });
        await _db.SaveChangesAsync();

        var rec    = MakeRec(FailureId + 10);
        var result = await _resolver.ResolveSqlAsync(
            "UPDATE dbo.Files SET Status = 1 WHERE Id = '{sourceId}'", rec);

        // Structure is untouched — exactly one param marker, no injected OR.
        Assert.Equal("UPDATE dbo.Files SET Status = 1 WHERE Id = @p0", result.Sql);
        Assert.DoesNotContain("OR", result.Sql);
        Assert.DoesNotContain("'1'='1", result.Sql);
        // The attack text survives verbatim ONLY as an inert bound value.
        Assert.Equal(attack, Assert.Single(result.Parameters).Value);
    }

    [Fact]
    public async Task ResolveSql_UnknownPlaceholder_LeftLiteralWithQuotes()
    {
        var rec    = MakeRec(FailureId);
        var result = await _resolver.ResolveSqlAsync(
            "UPDATE t SET x = '{notAToken}' WHERE Id = '{sourceId}'", rec);

        // Unknown token untouched (quotes preserved); only {sourceId} parameterized.
        Assert.Equal("UPDATE t SET x = '{notAToken}' WHERE Id = @p0", result.Sql);
        Assert.Equal("deposit-abc-123", Assert.Single(result.Parameters).Value);
    }

    [Fact]
    public async Task ResolveSql_LoneQuoteAroundToken_DegradesToInertLiteral()
    {
        // Odd case: token abuts a wildcard inside a string literal (LIKE '{sourceId}%').
        // The matched-pair branch can't fire (trailing char is % not '), so the bare
        // token is parameterized and the quotes stay balanced → '@p0%' is a harmless
        // literal (matches nothing at runtime). Crucially: no unbalanced quote, no
        // injection.
        var rec    = MakeRec(FailureId);
        var result = await _resolver.ResolveSqlAsync(
            "DELETE FROM t WHERE Name LIKE '{sourceId}%'", rec);

        Assert.Equal("DELETE FROM t WHERE Name LIKE '@p0%'", result.Sql);
        // Quotes remain balanced (even count) — statement stays parseable.
        Assert.Equal(0, result.Sql.Count(c => c == '\'') % 2);
    }

    [Fact]
    public async Task ResolveSql_EmptyTemplate_NoParameters()
    {
        var rec    = MakeRec(FailureId);
        var result = await _resolver.ResolveSqlAsync("", rec);

        Assert.Equal("", result.Sql);
        Assert.Empty(result.Parameters);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static AiRecommendation MakeRec(int failureId) => new()
    {
        RecommendationId = 1,
        FailureId        = failureId,
        ErrorTypeId      = 1,
        SuggestedAction  = "test",
        FixCategory      = FixCategory.Retry,
        ConfidenceScore  = 0.9m,
    };

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
