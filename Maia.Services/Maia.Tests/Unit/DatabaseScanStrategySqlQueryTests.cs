using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Unit tests for the CheckType.SqlQuery branch of DatabaseScanStrategy, using a
/// fake ISqlQueryRunner (the testability seam) so no live database is needed.
/// Pins the v1 contract: every returned row is a failure (Option A — no extra
/// predicate); TargetField read BY NAME; SourceIdColumn optional with row-index
/// fallback; EXEC/SELECT text passed verbatim; row cap; short StepName +
/// "db://conn/query" SourceLogPath; missing-TargetField hard failure; no-watermark
/// open-failure dedup. The existing ColumnRange/ValueEquals paths talk to
/// SqlConnection directly and remain untested in v1 (known, scoped gap).
/// </summary>
public class DatabaseScanStrategySqlQueryTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeSqlRunner(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : ISqlQueryRunner
    {
        public string? LastCommandText { get; private set; }
        public int      LastMaxRows    { get; private set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            string connectionString, string commandText, int maxRows, CancellationToken ct = default)
        {
            LastCommandText = commandText;
            LastMaxRows     = maxRows;
            return Task.FromResult(rows);
        }
    }

    private sealed class Harness
    {
        public readonly List<JobFailure> Saved = new();
        public readonly List<JobFailure> Classified = new();
        public bool OpenFailureExists { get; init; }
        public string? StoredWatermark { get; init; }
        public HashSet<string> OpenSourceIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string? UpdatedWatermark { get; private set; }

        public DatabaseScanStrategy Build(ISqlQueryRunner runner)
        {
            var jobRepo = new Mock<IJobRepository>();
            var next = 1;
            jobRepo.Setup(r => r.SaveAsync(It.IsAny<JobFailure>(), It.IsAny<CancellationToken>()))
                   .Returns((JobFailure f, CancellationToken _) => { f.FailureId = next++; Saved.Add(f); return Task.FromResult(f); });
            jobRepo.Setup(r => r.HasOpenFailureAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(OpenFailureExists);
            jobRepo.Setup(r => r.GetOpenFailureSourceIdsAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(OpenSourceIds);

            var watermarks = new Mock<IScanWatermarkRepository>();
            watermarks.Setup(w => w.GetDbWatermarkAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(StoredWatermark);
            watermarks.Setup(w => w.UpdateDbWatermarkAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .Callback((int _, string v, CancellationToken _) => UpdatedWatermark = v)
                      .Returns(Task.CompletedTask);

            var classify = new Mock<IClassifyJobsUseCase>();
            classify.Setup(c => c.ExecuteAsync(It.IsAny<IEnumerable<JobFailure>>(), It.IsAny<CancellationToken>()))
                    .Returns((IEnumerable<JobFailure> fs, CancellationToken _) =>
                    {
                        Classified.AddRange(fs);
                        return Task.FromResult((IReadOnlyList<ClassificationResult>)Array.Empty<ClassificationResult>());
                    });

            var suggest = new Mock<IGenerateSuggestionsUseCase>();
            suggest.Setup(s => s.ExecuteAsync(It.IsAny<IEnumerable<ClassificationResult>>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TestDb"] = "Server=fake;Database=x;" })
                .Build();

            return new DatabaseScanStrategy(
                config, jobRepo.Object, watermarks.Object,
                classify.Object, suggest.Object, runner,
                NullLogger<DatabaseScanStrategy>.Instance);
        }
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, object?> Row(params (string Name, object? Value)[] cols)
        => cols.ToDictionary(c => c.Name, c => c.Value, StringComparer.OrdinalIgnoreCase);

    private static ScanCheckRule SqlRule(int id, string query, string targetField,
        string? sourceIdColumn = null, string? desc = null, string? watermarkColumn = null)
        => new()
        {
            CheckRuleId     = id,
            MonitoredJobId  = 1,
            ScanSourceId    = 1,
            CheckType       = CheckType.SqlQuery,
            SourceTable     = query,
            TargetField     = targetField,
            SourceIdColumn  = sourceIdColumn,
            WatermarkColumn = watermarkColumn,
            Description     = desc,
            IsActive        = true,
        };

    private static (MonitoredJob Job, ScanSource Source) JobAndSource(params ScanCheckRule[] rules)
        => (new MonitoredJob { MonitoredJobId = 1, JobTypeId = 7, Name = "TestJob" },
            new ScanSource { ScanSourceId = 1, MonitoredJobId = 1, Name = "DB", ConnectionName = "TestDb", ScanCheckRules = rules.ToList() });

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawSelect_CreatesFailurePerRow_WithSourceIdMessageAndPaths()
    {
        var runner = new FakeSqlRunner(new[]
        {
            Row(("OrderId", "ORD-1"), ("IsStuck", 1)),
            Row(("OrderId", "ORD-2"), ("IsStuck", 1)),
        });
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(10,
            "SELECT OrderId, IsStuck FROM Orders WHERE IsStuck = 1", "IsStuck",
            sourceIdColumn: "OrderId", desc: "Stuck orders"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(2, result.FailuresDetected);
        Assert.Equal(new[] { "ORD-1", "ORD-2" }, h.Saved.Select(f => f.SourceId));
        Assert.All(h.Saved, f => Assert.Equal("Stuck orders", f.StepName));         // Description → StepName
        Assert.All(h.Saved, f => Assert.Equal("db://TestDb/query", f.SourceLogPath));
        Assert.Contains("Stuck orders: [IsStuck] = 1", h.Saved[0].ErrorMessage);
        Assert.Contains("OrderId=ORD-1", h.Saved[0].ErrorMessage);
    }

    [Fact]
    public async Task ExecStoredProc_PassesCommandTextVerbatim_AndCap()
    {
        var runner = new FakeSqlRunner(new[] { Row(("Status", "ERROR"), ("Id", 42)) });
        var h = new Harness();
        var strat = h.Build(runner);
        const string proc = "EXEC sp_CheckStuckOrders @threshold=60";
        var (job, source) = JobAndSource(SqlRule(11, proc, "Status", sourceIdColumn: "Id"));

        await strat.ScanAsync(job, source);

        Assert.Equal(proc, runner.LastCommandText);   // run verbatim — no EXEC detection/munging
        Assert.Equal(500, runner.LastMaxRows);          // code-side cap passed to the runner
        Assert.Equal("42", Assert.Single(h.Saved).SourceId);
    }

    [Fact]
    public async Task MissingTargetFieldColumn_ThrowsClearError_NoFailures()
    {
        var runner = new FakeSqlRunner(new[] { Row(("OrderId", "ORD-1")) });   // no "IsStuck"
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(12, "SELECT OrderId FROM Orders", "IsStuck"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strat.ScanAsync(job, source));
        Assert.Contains("IsStuck", ex.Message);
        Assert.Contains("OrderId", ex.Message);   // lists the columns actually returned
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task SourceIdColumnAbsentFromResult_FallsBackToRowIndex()
    {
        var runner = new FakeSqlRunner(new[] { Row(("Status", "ERROR")), Row(("Status", "ERROR")) });
        var h = new Harness();
        var strat = h.Build(runner);
        // SourceIdColumn configured but not present in the result set.
        var (job, source) = JobAndSource(SqlRule(13, "SELECT Status FROM X", "Status", sourceIdColumn: "MissingId"));

        await strat.ScanAsync(job, source);

        Assert.Equal(new[] { "1", "2" }, h.Saved.Select(f => f.SourceId));
    }

    [Fact]
    public async Task NoSourceIdColumnConfigured_UsesRowIndex()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", "x")) });
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(14, "SELECT V FROM X", "V"));

        await strat.ScanAsync(job, source);

        Assert.Equal("1", Assert.Single(h.Saved).SourceId);
    }

    [Fact]
    public async Task HitsRowCap_DoesNotThrow_AndCreatesAllReturnedRows()
    {
        var rows = Enumerable.Range(1, 500)
            .Select(i => Row(("V", i), ("Id", i)))
            .ToArray<IReadOnlyDictionary<string, object?>>();
        var runner = new FakeSqlRunner(rows);
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(15, "SELECT V, Id FROM Big", "V", sourceIdColumn: "Id"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(500, result.FailuresDetected);
        Assert.Equal(500, runner.LastMaxRows);
    }

    [Fact]
    public async Task NoDescription_StepNameIsPerRuleLabel()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", "x"), ("Id", "k")) });
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(99, "SELECT V, Id FROM X", "V", sourceIdColumn: "Id"));

        await strat.ScanAsync(job, source);

        Assert.Equal("SqlQuery #99", Assert.Single(h.Saved).StepName);
    }

    [Fact]
    public async Task NoRows_NoFailures()
    {
        var runner = new FakeSqlRunner(Array.Empty<IReadOnlyDictionary<string, object?>>());
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(16, "SELECT V FROM X WHERE 1=0", "V"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
        Assert.Empty(h.Saved);
    }

    [Fact] // No SourceId, no watermark → coarse per-rule dedup (whole rule skipped while open)
    public async Task NoKeysConfigured_OpenFailureExists_CoarseDedupSkipsRule()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", "x")) });
        var h = new Harness { OpenFailureExists = true };
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(17, "SELECT V FROM X", "V", desc: "D"));   // no sourceId/watermark

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
        Assert.Empty(h.Saved);
    }

    // ── Per-SourceId dedup ─────────────────────────────────────────────────────

    [Fact] // a row whose SourceId already has an open failure is skipped; a NEW id fires
    public async Task PerSourceId_OpenIdSkipped_NewIdStillFires()
    {
        var runner = new FakeSqlRunner(new[]
        {
            Row(("V", 1), ("Id", "ORD-1")),   // already open → skip
            Row(("V", 1), ("Id", "ORD-2")),   // new → fire
        });
        var h = new Harness { OpenSourceIds = new(StringComparer.OrdinalIgnoreCase) { "ORD-1" } };
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(20, "SELECT V, Id FROM X", "V", sourceIdColumn: "Id", desc: "Stuck"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(1, result.FailuresDetected);
        Assert.Equal("ORD-2", Assert.Single(h.Saved).SourceId);
    }

    [Fact] // open set is lowercased, row id uppercased — must still match (no duplicate)
    public async Task PerSourceId_DedupIsCaseInsensitive()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", 1), ("Id", "6E02EF59-AAAA")) });
        var h = new Harness { OpenSourceIds = new(StringComparer.OrdinalIgnoreCase) { "6e02ef59-aaaa" } };
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(21, "SELECT V, Id FROM X", "V", sourceIdColumn: "Id"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
    }

    // ── Watermark (in-memory incremental) ──────────────────────────────────────

    [Fact] // rows at/below the stored mark are filtered; only newer ones fire; mark advances to max seen
    public async Task Watermark_FiltersOldRows_FiresNew_AndAdvances()
    {
        var runner = new FakeSqlRunner(new[]
        {
            Row(("V", 1), ("Id", "A"), ("UpdateDate", new DateTime(2026, 5, 1))),   // old → filtered
            Row(("V", 1), ("Id", "B"), ("UpdateDate", new DateTime(2026, 7, 1))),   // new → fires
        });
        var h = new Harness { StoredWatermark = "2026-06-01 00:00:00.0000000" };
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(22, "SELECT V, Id, UpdateDate FROM X", "V",
            sourceIdColumn: "Id", watermarkColumn: "UpdateDate"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(1, result.FailuresDetected);
        Assert.Equal("B", Assert.Single(h.Saved).SourceId);
        Assert.Equal("2026-07-01 00:00:00.0000000", h.UpdatedWatermark);   // advanced to highest seen
    }

    [Fact] // first scan (no stored mark) fires everything and sets the baseline
    public async Task Watermark_FirstScan_FiresAll_SetsBaseline()
    {
        var runner = new FakeSqlRunner(new[]
        {
            Row(("V", 1), ("Id", "A"), ("UpdateDate", new DateTime(2026, 5, 1))),
            Row(("V", 1), ("Id", "B"), ("UpdateDate", new DateTime(2026, 7, 1))),
        });
        var h = new Harness { StoredWatermark = null };
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(23, "SELECT V, Id, UpdateDate FROM X", "V",
            sourceIdColumn: "Id", watermarkColumn: "UpdateDate"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(2, result.FailuresDetected);
        Assert.Equal("2026-07-01 00:00:00.0000000", h.UpdatedWatermark);
    }

    [Fact] // watermark configured but the column isn't in the result set → clear config error
    public async Task Watermark_ColumnMissingFromResult_ThrowsClearError()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", 1), ("Id", "A")) });   // no UpdateDate
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(24, "SELECT V, Id FROM X", "V",
            sourceIdColumn: "Id", watermarkColumn: "UpdateDate"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strat.ScanAsync(job, source));
        Assert.Contains("UpdateDate", ex.Message);
        Assert.Contains("WatermarkColumn", ex.Message);
    }

    [Fact] // watermark + per-id compose: old row filtered by watermark, open id skipped, the one fresh+new row fires
    public async Task WatermarkAndSourceId_Compose()
    {
        var runner = new FakeSqlRunner(new[]
        {
            Row(("V", 1), ("Id", "OLD"), ("UpdateDate", new DateTime(2026, 5, 1))),   // filtered by watermark
            Row(("V", 1), ("Id", "OPEN"),("UpdateDate", new DateTime(2026, 7, 1))),   // new ts but id already open → skip
            Row(("V", 1), ("Id", "NEW"), ("UpdateDate", new DateTime(2026, 7, 2))),   // new ts + new id → fire
        });
        var h = new Harness
        {
            StoredWatermark = "2026-06-01 00:00:00.0000000",
            OpenSourceIds   = new(StringComparer.OrdinalIgnoreCase) { "OPEN" },
        };
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(25, "SELECT V, Id, UpdateDate FROM X", "V",
            sourceIdColumn: "Id", watermarkColumn: "UpdateDate"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal("NEW", Assert.Single(h.Saved).SourceId);
        Assert.Equal("2026-07-02 00:00:00.0000000", h.UpdatedWatermark);   // advanced to highest seen (incl. filtered/skipped)
    }

    // ── Per-rule resilience ────────────────────────────────────────────────────

    [Fact] // a rule that throws (watermark column not in SELECT) must NOT abort the scan
           // or orphan a later rule's failures — the good rule still creates AND classifies,
           // and the scan surfaces the error afterward for visibility.
    public async Task OneRuleThrows_LaterRuleStillCreatesAndClassifies_ScanSurfacesError()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", "x"), ("Id", "k")) });
        var h = new Harness();
        var strat = h.Build(runner);
        // Bad rule FIRST: WatermarkColumn 'UpdateDate' isn't in the result → throws.
        var bad  = SqlRule(30, "SELECT V, Id FROM X", "V", sourceIdColumn: "Id", watermarkColumn: "UpdateDate");
        var good = SqlRule(31, "SELECT V, Id FROM X", "V", sourceIdColumn: "Id", desc: "Good");
        var (job, source) = JobAndSource(bad, good);

        // Scan surfaces the rule error (scan-run recorded Failed) ...
        await Assert.ThrowsAsync<InvalidOperationException>(() => strat.ScanAsync(job, source));

        // ... but the good rule (after the throwing one) still created its failure ...
        Assert.Equal("k", Assert.Single(h.Saved).SourceId);
        Assert.Equal("Good", h.Saved[0].StepName);
        // ... and that failure was classified BEFORE the error surfaced (not orphaned).
        Assert.Contains(h.Classified, f => f.SourceId == "k");
    }
}
