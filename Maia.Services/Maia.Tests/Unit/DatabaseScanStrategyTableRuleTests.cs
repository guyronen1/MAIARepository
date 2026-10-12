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

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("Details", new[] { "Details" })]
    [InlineData(" UpdateDate , Details ,, ", new[] { "UpdateDate", "Details" })]   // trimmed, blanks dropped
    [InlineData("Details, details, e.Code", new[] { "Details", "e.Code" })]        // case-insensitive distinct, alias kept
    public void ParseDetailColumns_trims_dedups_and_drops_blanks(string? csv, string[] expected)
        => Assert.Equal(expected, DatabaseScanStrategy.ParseDetailColumns(csv));

    [Fact]
    public void BuildRowMessage_without_details_is_unchanged_single_line()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ValueEquals, SourceTable = "dbo.Events",
            TargetField = "Status", ExpectedValue = "8", SourceIdColumn = "EventId", Description = "Event failed" };

        var msg = DatabaseScanStrategy.BuildRowMessage(rule, "1", 8, null, "4711");

        Assert.Equal("[dbo.Events].[Status] = 8 matches error value 8 (EventId=4711) [Status=8] — Event failed", msg);
        Assert.Equal(msg, DatabaseScanStrategy.BuildRowMessage(rule, "1", 8, null, "4711", []));
    }

    [Fact]
    public void BuildRowMessage_appends_one_line_per_detail_and_caps_long_values()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ValueEquals, SourceTable = "dbo.Events",
            TargetField = "Status", ExpectedValue = "8", SourceIdColumn = "EventId" };
        var longText = new string('x', DatabaseScanStrategy.MaxDetailValueLength + 50);
        var details = new List<KeyValuePair<string, string?>>
        {
            new("UpdateDate", "2026-10-12 09:14:33.1230000"),
            new("Details",    longText),
            new("Note",       null),
        };

        var lines = DatabaseScanStrategy.BuildRowMessage(rule, "1", 8, null, "4711", details).Split('\n');

        Assert.Equal("[dbo.Events].[Status] = 8 matches error value 8 (EventId=4711) [Status=8]", lines[0]); // head intact
        Assert.Equal("UpdateDate: 2026-10-12 09:14:33.1230000", lines[1]);
        Assert.Equal("Details: " + new string('x', DatabaseScanStrategy.MaxDetailValueLength) + "…", lines[2]);
        Assert.Equal("Note: NULL", lines[3]);
        Assert.Equal(4, lines.Length);
    }

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

    // ── (1b) Composite scan rules — extra AND'd conditions on the same row ─────

    private static ScanCheckRuleCondition Eq(int order, string col, string value) => new()
        { ConditionOrder = order, Kind = CheckType.ValueEquals, TargetField = col, ExpectedValue = value };

    private static ScanCheckRuleCondition Range(int order, string col, decimal? min = null, decimal? max = null) => new()
        { ConditionOrder = order, Kind = CheckType.ColumnRange, TargetField = col, MinValue = min, MaxValue = max };

    [Fact]
    public void BuildFilterClause_with_conditions_ANDs_each_in_parens_with_prefixed_params()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ValueEquals, TargetField = "Status", ExpectedValue = "5",
            Conditions = [Eq(1, "Region", "EU"), Range(2, "Amount", 10m, 100m)] };

        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(rule);

        Assert.Equal("([Status] = @ExactVal) AND ([Region] = @C0_ExactVal) AND ([Amount] < @C1_Min OR [Amount] > @C1_Max)", clause);
        Assert.Equal(new[] { "@ExactVal", "@C0_ExactVal", "@C1_Min", "@C1_Max" }, ps.Select(p => p.Name));
        Assert.Equal(new object[] { "5", "EU", 10m, 100m }, ps.Select(p => p.Value));
    }

    [Fact]
    public void BuildFilterClause_with_zero_conditions_is_byte_identical_to_legacy()
    {
        var legacy = DatabaseScanStrategy.BuildFilterClause(
            new ScanCheckRule { CheckType = CheckType.ColumnRange, TargetField = "Amount", MinValue = 1m, MaxValue = 9m });
        var empty = DatabaseScanStrategy.BuildFilterClause(
            new ScanCheckRule { CheckType = CheckType.ColumnRange, TargetField = "Amount", MinValue = 1m, MaxValue = 9m, Conditions = [] });

        Assert.Equal(legacy.Clause, empty.Clause);
        Assert.Equal(legacy.Params, empty.Params);
    }

    [Fact]
    public void BuildFilterClause_applies_conditions_in_ConditionOrder_not_list_order()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ColumnRange, TargetField = "Amount", MaxValue = 9m,
            Conditions = [Eq(2, "Second", "b"), Eq(1, "First", "a")] };   // list order reversed

        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(rule);

        Assert.Equal("([Amount] > @Max) AND ([First] = @C0_ExactVal) AND ([Second] = @C1_ExactVal)", clause);
        Assert.Equal(new object[] { 9m, "a", "b" }, ps.Select(p => p.Value));
    }

    [Fact]
    public void BuildFilterClause_condition_may_target_the_parent_column_and_escapes_brackets()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ValueEquals, TargetField = "Status", ExpectedValue = "5",
            Conditions = [Range(1, "Status", min: 1m), Eq(2, "We]ird", "x")] };

        var (clause, _) = DatabaseScanStrategy.BuildFilterClause(rule);

        Assert.Equal("([Status] = @ExactVal) AND ([Status] < @C0_Min) AND ([We]]ird] = @C1_ExactVal)", clause);
    }

    [Fact]
    public void BuildRowMessage_appends_one_token_per_condition_after_the_parent_token()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ValueEquals, SourceTable = "dbo.Files",
            TargetField = "Status", ExpectedValue = "5", SourceIdColumn = "Id", Description = "desc",
            Conditions = [Eq(1, "Region", "EU"), Range(2, "Amount", 10m, 100m)] };

        // ValueEquals token shows the canonical ExpectedValue; ColumnRange shows the actual value.
        var msg = DatabaseScanStrategy.BuildRowMessage(rule, "1", 5, null, "17", null, ["eu", "537"]);

        Assert.Equal("[dbo.Files].[Status] = 5 matches error value 5 (Id=17) [Status=5] [Region=EU] [Amount=537] — desc", msg);
    }

    [Fact]
    public void BuildRowMessage_condition_token_renders_NULL_when_the_value_is_missing()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ColumnRange, SourceTable = "t", TargetField = "A", MaxValue = 1m,
            Conditions = [Range(1, "B", max: 2m)] };

        var msg = DatabaseScanStrategy.BuildRowMessage(rule, "1", 3, null, null, null, [null]);

        Assert.Equal("[t].[A] = 3 is outside range [−∞, 1] (row#1) [A=3] [B=NULL]", msg);
    }

    [Fact]
    public void BuildRowMessage_without_condition_values_is_unchanged_for_a_plain_rule()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ValueEquals, SourceTable = "dbo.Events",
            TargetField = "Status", ExpectedValue = "8", SourceIdColumn = "EventId" };

        Assert.Equal(DatabaseScanStrategy.BuildRowMessage(rule, "1", 8, null, "4711"),
                     DatabaseScanStrategy.BuildRowMessage(rule, "1", 8, null, "4711", null, []));
    }

    [Theory]
    [InlineData(CheckType.ValueEquals, "Region", "EU", null, null, true)]
    [InlineData(CheckType.ValueEquals, "Region", "  ",  null, null, false)]   // no expected value
    [InlineData(CheckType.ValueEquals, "",       "EU", null, null, false)]   // no column
    [InlineData(CheckType.ColumnRange, "Amount", null, 1.0,  null, true)]
    [InlineData(CheckType.ColumnRange, "Amount", null, null, 9.0,  true)]
    [InlineData(CheckType.ColumnRange, "Amount", null, null, null, false)]   // neither bound
    [InlineData(CheckType.FileContent, "/x/y",   "v",  null, null, false)]   // Phase 2 kind — not valid for DB
    [InlineData(CheckType.SqlQuery,    "Col",    "v",  null, null, false)]
    public void IsConditionValid_requires_what_the_kind_compares_against(
        CheckType kind, string col, string? expected, double? min, double? max, bool valid)
    {
        var c = new ScanCheckRuleCondition { Kind = kind, TargetField = col, ExpectedValue = expected,
            MinValue = (decimal?)min, MaxValue = (decimal?)max };
        Assert.Equal(valid, DatabaseScanStrategy.IsConditionValid(c));
    }

    [Fact]
    public void IsRuleValid_fails_the_whole_rule_when_any_condition_is_invalid()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ValueEquals, TargetField = "Status", ExpectedValue = "5",
            Conditions = [Eq(1, "Region", "EU"), Range(2, "Amount")] };   // 2nd has no bounds

        Assert.False(DatabaseScanStrategy.IsRuleValid(rule));
        rule.Conditions[1].MinValue = 1m;
        Assert.True(DatabaseScanStrategy.IsRuleValid(rule));
    }

    [Fact]
    public async Task A_rule_with_an_invalid_condition_is_skipped_no_failures_no_connection()
    {
        // Invalid condition ⇒ IsRuleValid false ⇒ skipped before any SqlConnection is
        // opened (the fake connection string would otherwise throw). Firing on the parent
        // alone would silently widen the match — the one thing a composite must not do.
        var rule = ColumnRange("dbo.X", min: 1);
        rule.Conditions.Add(Eq(1, "Region", "  "));
        var (job, source) = JobAndSource("TestDb", rule);

        var result = await BuildStrategy().ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
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
