using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Unit coverage for the ColumnRange/ValueEquals (table-rule) path of
/// DatabaseScanStrategy — the gap the SqlQuery tests left open. The row-fetch talks
/// to a live SqlConnection, so this pins the two DB-free halves instead: (1) the pure
/// SQL-string builders (identifier quoting incl. the `]`-escaping guard, filter
/// clause + parameters), and (2) the pre-SQL validation/dispatch that throws before
/// any connection is opened.
/// </summary>
public class DatabaseScanStrategyTableRuleTests
{
    // ── (1) Pure SQL-string builders ──────────────────────────────────────────

    [Theory]
    [InlineData("Col", "[Col]")]
    [InlineData("Order Id", "[Order Id]")]
    [InlineData("Wei]rd", "[Wei]]rd]")]           // embedded ] doubled — can't break out
    public void Bracket_escapes_closing_brackets(string input, string expected)
        => Assert.Equal(expected, DatabaseScanStrategy.Bracket(input));

    [Theory]
    [InlineData("Files", "[Files]")]
    [InlineData("dbo.Files", "[dbo].[Files]")]
    [InlineData("[dbo].[Files]", "[dbo].[Files]")] // already-bracketed parts are normalized
    [InlineData("we]ird", "[we]]ird]")]            // escaping applied per part
    public void QuoteTable_brackets_each_part(string input, string expected)
        => Assert.Equal(expected, DatabaseScanStrategy.QuoteTable(input));

    [Theory]
    [InlineData("FilePath", "[FilePath]")]
    [InlineData("j.FilePath", "j.[FilePath]")]     // alias preserved, only the column bracketed
    [InlineData("j.Fi]le", "j.[Fi]]le]")]
    public void QuoteColumnRef_brackets_only_the_column(string input, string expected)
        => Assert.Equal(expected, DatabaseScanStrategy.QuoteColumnRef(input));

    [Fact]
    public void BuildFilterClause_ValueEquals_parameterises_the_expected_value()
    {
        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(
            new ScanCheckRule { CheckType = CheckType.ValueEquals, TargetField = "Status", ExpectedValue = "5" });

        Assert.Equal("([Status] = @ExactVal)", clause);
        Assert.Equal(("@ExactVal", (object)"5"), Assert.Single(ps));
    }

    [Fact]
    public void BuildFilterClause_ColumnRange_both_bounds_emits_OR_with_params()
    {
        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(
            new ScanCheckRule { CheckType = CheckType.ColumnRange, TargetField = "Amount", MinValue = 1m, MaxValue = 9m });

        Assert.Equal("([Amount] < @Min OR [Amount] > @Max)", clause);
        Assert.Equal(new[] { "@Min", "@Max" }, ps.Select(p => p.Name));
        Assert.Equal(new object[] { 1m, 9m }, ps.Select(p => p.Value));
    }

    [Fact]
    public void BuildFilterClause_ColumnRange_min_only_omits_the_max_condition()
    {
        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(
            new ScanCheckRule { CheckType = CheckType.ColumnRange, TargetField = "Amount", MinValue = 1m });

        Assert.Equal("([Amount] < @Min)", clause);
        Assert.Equal("@Min", Assert.Single(ps).Name);
    }

    [Fact]
    public void BuildFilterClause_escapes_a_target_field_containing_a_bracket()
    {
        var (clause, _) = DatabaseScanStrategy.BuildFilterClause(
            new ScanCheckRule { CheckType = CheckType.ValueEquals, TargetField = "Wei]rd", ExpectedValue = "x" });

        Assert.Equal("([Wei]]rd] = @ExactVal)", clause);
    }

    // ── (2) Pre-SQL validation / dispatch (no connection opened) ───────────────

    private static DatabaseScanStrategy BuildStrategy()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TestDb"] = "Server=fake;Database=x;",
            })
            .Build();
        return new DatabaseScanStrategy(
            config, Mock.Of<IJobRepository>(), Mock.Of<IScanWatermarkRepository>(),
            Mock.Of<IClassifyJobsUseCase>(), Mock.Of<IGenerateSuggestionsUseCase>(),
            Mock.Of<ISqlQueryRunner>(), NullLogger<DatabaseScanStrategy>.Instance);
    }

    private static (MonitoredJob, ScanSource) JobAndSource(string? conn, params ScanCheckRule[] rules)
        => (new MonitoredJob { MonitoredJobId = 1, JobTypeId = 7, Name = "TestJob" },
            new ScanSource { ScanSourceId = 1, MonitoredJobId = 1, Name = "DB", ConnectionName = conn, ScanCheckRules = rules.ToList() });

    private static ScanCheckRule ColumnRange(string? table, decimal? min = null, decimal? max = null) => new()
    {
        CheckRuleId = 1, MonitoredJobId = 1, ScanSourceId = 1, IsActive = true,
        CheckType = CheckType.ColumnRange, SourceTable = table, TargetField = "Amount",
        MinValue = min, MaxValue = max,
    };

    [Fact]
    public async Task No_active_supported_rules_throws_before_touching_the_db()
    {
        var inactive = ColumnRange("dbo.X", min: 1);
        inactive.IsActive = false;
        var (job, source) = JobAndSource("TestDb", inactive);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildStrategy().ScanAsync(job, source));
        Assert.Contains("no active database ScanCheckRules", ex.Message);
    }

    [Fact]
    public async Task A_rule_with_no_source_table_throws()
    {
        var (job, source) = JobAndSource("TestDb", ColumnRange(table: null, min: 1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildStrategy().ScanAsync(job, source));
        Assert.Contains("no SourceTable", ex.Message);
    }

    [Fact]
    public async Task An_unresolvable_connection_name_throws()
    {
        var (job, source) = JobAndSource("NoSuchConn", ColumnRange("dbo.X", min: 1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildStrategy().ScanAsync(job, source));
        Assert.Contains("Connection string", ex.Message);
        Assert.Contains("NoSuchConn", ex.Message);
    }

    [Fact]
    public async Task A_ColumnRange_rule_with_neither_bound_is_skipped_no_failures_no_connection()
    {
        // IsRuleValid == false (no Min AND no Max) → the rule is skipped in the loop
        // before any SqlConnection is opened, so a fake connection string never fails.
        var (job, source) = JobAndSource("TestDb", ColumnRange("dbo.X"));

        var result = await BuildStrategy().ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
    }
}
