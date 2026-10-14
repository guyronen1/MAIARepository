using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Scanning;
using Maia.Infrastructure.Scanning;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// The Database table-rule check kinds added 2026-10-14 — NotEquals (<c>&lt;&gt;</c>,
/// NULL rows not flagged), Contains (<c>LIKE '%text%'</c>, text matched literally) and
/// InList (<c>IN (1, 2, 3)</c>). Pins the generated SQL + parameters on both engines,
/// the message headline / classifier token, and the value validation.
/// </summary>
public class DbCheckKindsTests
{
    private static ScanCheckRule Rule(CheckType kind, string field, string? expected, params ScanCheckRuleCondition[] conditions)
        => new() { CheckType = kind, SourceTable = "dbo.Events", TargetField = field, ExpectedValue = expected,
                   SourceIdColumn = "EventId", Conditions = [.. conditions] };

    // ── InList parsing ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("1, 2, 3", new[] { 1.0, 2.0, 3.0 })]
    [InlineData(" 5 ", new[] { 5.0 })]
    [InlineData("1.5,-2", new[] { 1.5, -2.0 })]
    public void TryParseInList_accepts_comma_separated_numbers(string csv, double[] expected)
    {
        Assert.True(DbCheckKinds.TryParseInList(csv, out var values));
        Assert.Equal(expected.Select(d => (decimal)d), values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("1, two, 3")]
    [InlineData("1,,2")]           // an empty item is not a number
    public void TryParseInList_rejects_blank_or_non_numeric(string? csv)
        => Assert.False(DbCheckKinds.TryParseInList(csv, out _));

    [Fact]
    public void TryParseInList_caps_the_item_count()
    {
        var ok  = string.Join(",", Enumerable.Range(1, DbCheckKinds.MaxInListItems));
        var bad = string.Join(",", Enumerable.Range(1, DbCheckKinds.MaxInListItems + 1));
        Assert.True(DbCheckKinds.TryParseInList(ok, out _));
        Assert.False(DbCheckKinds.TryParseInList(bad, out _));
    }

    // ── Filter clause (SQL Server) ───────────────────────────────────────────

    [Fact]
    public void NotEquals_is_a_plain_not_equal_so_NULL_rows_are_not_flagged()
    {
        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(Rule(CheckType.NotEquals, "Status", "0"));

        Assert.Equal("([Status] <> @ExactVal)", clause);
        Assert.DoesNotContain("IS NULL", clause);
        Assert.Equal(("@ExactVal", (object)"0"), Assert.Single(ps));
    }

    [Fact]
    public void Contains_binds_a_LIKE_pattern_with_the_text_escaped_to_match_literally()
    {
        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(Rule(CheckType.Contains, "Details", @"50%_off[1]\x"));

        Assert.Equal(@"([Details] LIKE @Like ESCAPE '\')", clause);
        Assert.Equal(("@Like", (object)@"%50\%\_off\[1]\\x%"), Assert.Single(ps));
    }

    [Fact]
    public void InList_binds_one_parameter_per_number()
    {
        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(Rule(CheckType.InList, "Code", "1, 2, 3"));

        Assert.Equal("([Code] IN (@In0, @In1, @In2))", clause);
        Assert.Equal(new[] { "@In0", "@In1", "@In2" }, ps.Select(p => p.Name));
        Assert.Equal(new object[] { 1m, 2m, 3m }, ps.Select(p => p.Value));
    }

    [Fact]
    public void New_kinds_work_as_extra_conditions_with_prefixed_parameters()
    {
        var rule = Rule(CheckType.ValueEquals, "Status", "8",
            new ScanCheckRuleCondition { ConditionOrder = 1, Kind = CheckType.Contains,  TargetField = "Details", ExpectedValue = "timeout" },
            new ScanCheckRuleCondition { ConditionOrder = 2, Kind = CheckType.InList,    TargetField = "Code",    ExpectedValue = "4, 5" },
            new ScanCheckRuleCondition { ConditionOrder = 3, Kind = CheckType.NotEquals, TargetField = "Region",  ExpectedValue = "EU" });

        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(rule);

        Assert.Equal(@"([Status] = @ExactVal) AND ([Details] LIKE @C0_Like ESCAPE '\') AND ([Code] IN (@C1_In0, @C1_In1)) AND ([Region] <> @C2_ExactVal)", clause);
        Assert.Equal(new[] { "@ExactVal", "@C0_Like", "@C1_In0", "@C1_In1", "@C2_ExactVal" }, ps.Select(p => p.Name));
    }

    // ── Filter clause (Oracle) ───────────────────────────────────────────────

    [Fact]
    public void Oracle_Contains_escapes_only_percent_and_underscore()
    {
        // An escape before any other character is ORA-01424, so '[' stays as-is.
        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(Rule(CheckType.Contains, "Details", "50%_off[1]"), SqlDialect.Oracle);

        Assert.Equal(@"(""DETAILS"" LIKE :p_Like ESCAPE '\')", clause);
        Assert.Equal(("p_Like", (object)@"%50\%\_off[1]%"), Assert.Single(ps));
    }

    [Fact]
    public void Oracle_InList_and_NotEquals_use_prefixed_named_binds()
    {
        Assert.Equal("(\"CODE\" IN (:p_In0, :p_In1))",
            DatabaseScanStrategy.BuildFilterClause(Rule(CheckType.InList, "Code", "1,2"), SqlDialect.Oracle).Clause);
        Assert.Equal("(\"STATUS\" <> :p_ExactVal)",
            DatabaseScanStrategy.BuildFilterClause(Rule(CheckType.NotEquals, "Status", "0"), SqlDialect.Oracle).Clause);
    }

    // ── Message headline + classifier token ──────────────────────────────────

    [Fact]
    public void NotEquals_message_carries_the_rows_actual_value_in_the_token()
    {
        var msg = DatabaseScanStrategy.BuildRowMessage(Rule(CheckType.NotEquals, "Status", "0"), "1", 7, null, "42");
        Assert.Equal("[dbo.Events].[Status] = 7 differs from expected value 0 (EventId=42) [Status=7]", msg);
    }

    [Fact]
    public void InList_message_lists_the_numbers_and_carries_the_actual_value()
    {
        var msg = DatabaseScanStrategy.BuildRowMessage(Rule(CheckType.InList, "Code", "4, 5"), "1", 5, null, "42");
        Assert.Equal("[dbo.Events].[Code] = 5 is in error list (4, 5) (EventId=42) [Code=5]", msg);
    }

    [Fact]
    public void Contains_message_tokens_the_searched_text_and_caps_the_matched_value()
    {
        var longText = "connection timeout " + new string('x', DatabaseScanStrategy.MaxContainsValueLength);
        var msg = DatabaseScanStrategy.BuildRowMessage(Rule(CheckType.Contains, "Details", "timeout"), "1", longText, null, "42");

        Assert.EndsWith(" contains 'timeout' (EventId=42) [Details~timeout]", msg);
        Assert.Contains("…", msg);
        Assert.DoesNotContain(new string('x', DatabaseScanStrategy.MaxContainsValueLength), msg);
    }

    [Fact]
    public void Condition_tokens_per_kind_Contains_shows_the_text_others_the_actual_value()
    {
        var rule = Rule(CheckType.ValueEquals, "Status", "8",
            new ScanCheckRuleCondition { ConditionOrder = 1, Kind = CheckType.Contains,  TargetField = "Details", ExpectedValue = "timeout" },
            new ScanCheckRuleCondition { ConditionOrder = 2, Kind = CheckType.NotEquals, TargetField = "Region",  ExpectedValue = "EU" });

        var msg = DatabaseScanStrategy.BuildRowMessage(rule, "1", 8, null, "42", conditionValues: ["a timeout here", "US"]);

        Assert.Equal("[dbo.Events].[Status] = 8 matches error value 8 (EventId=42) [Status=8] [Details~timeout] [Region=US]", msg);
    }

    // ── Validity (a rule the scan would skip) ────────────────────────────────

    [Theory]
    [InlineData(CheckType.NotEquals, "0",     true)]
    [InlineData(CheckType.NotEquals, " ",     false)]
    [InlineData(CheckType.Contains,  "err",   true)]
    [InlineData(CheckType.Contains,  null,    false)]
    [InlineData(CheckType.InList,    "1, 2",  true)]
    [InlineData(CheckType.InList,    "1, x",  false)]
    public void IsRuleValid_and_IsConditionValid_require_the_kinds_value(CheckType kind, string? expected, bool valid)
    {
        Assert.Equal(valid, DatabaseScanStrategy.IsRuleValid(Rule(kind, "Col", expected)));
        Assert.Equal(valid, DatabaseScanStrategy.IsConditionValid(
            new ScanCheckRuleCondition { Kind = kind, TargetField = "Col", ExpectedValue = expected }));
    }
}
