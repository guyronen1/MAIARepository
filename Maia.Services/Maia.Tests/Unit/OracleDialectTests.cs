using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Pins the Oracle side of Database scanning without a live Oracle: provider resolution
/// from <c>ConnectionStrings:{Name}_ProviderName</c>, Oracle identifier quoting
/// (plain names upper-cased so "Status" finds STATUS), bind syntax, the ROWNUM-capped
/// query shape, and that the SQL Server dialect is still the legacy T-SQL.
/// </summary>
public sealed class OracleDialectTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // ── Provider resolution ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null,                              DbProvider.SqlServer)]
    [InlineData("",                                DbProvider.SqlServer)]
    [InlineData("SqlServer",                       DbProvider.SqlServer)]
    [InlineData("System.Data.SqlClient",           DbProvider.SqlServer)]
    [InlineData("Microsoft.Data.SqlClient",        DbProvider.SqlServer)]
    [InlineData("Oracle",                          DbProvider.Oracle)]
    [InlineData(" oracle ",                        DbProvider.Oracle)]
    [InlineData("Oracle.ManagedDataAccess.Client", DbProvider.Oracle)]
    public void TryParseProvider_accepts_short_and_invariant_names(string? raw, DbProvider expected)
    {
        Assert.True(DbConnections.TryParseProvider(raw, out var p));
        Assert.Equal(expected, p);
    }

    [Fact]
    public void TryParseProvider_rejects_unknown_engines()
        => Assert.False(DbConnections.TryParseProvider("MySql", out _));

    [Fact]
    public void TryResolve_reads_the_companion_key_and_defaults_to_SqlServer()
    {
        var cfg = Config(new()
        {
            ["ConnectionStrings:Main"]                 = "Server=x;",
            ["ConnectionStrings:Billing"]              = "Data Source=BILLPRD;User Id=u;Password=p;",
            ["ConnectionStrings:Billing_ProviderName"] = "Oracle",
        });

        Assert.Equal(DbProvider.SqlServer, DbConnections.TryResolve(cfg, "Main", out _)!.Provider);
        var billing = DbConnections.TryResolve(cfg, "Billing", out _)!;
        Assert.Equal(DbProvider.Oracle, billing.Provider);
        Assert.Equal("Data Source=BILLPRD;User Id=u;Password=p;", billing.ConnectionString);
    }

    [Fact]
    public void TryResolve_refuses_missing_connections_companion_keys_and_unknown_providers()
    {
        var cfg = Config(new()
        {
            ["ConnectionStrings:Billing"]              = "Data Source=BILLPRD;",
            ["ConnectionStrings:Billing_ProviderName"] = "Oracle",
            ["ConnectionStrings:Odd"]                  = "Server=x;",
            ["ConnectionStrings:Odd_ProviderName"]     = "MySql",
        });

        Assert.Null(DbConnections.TryResolve(cfg, "Nope", out var e1));
        Assert.Contains("not found", e1);
        Assert.Null(DbConnections.TryResolve(cfg, "Billing_ProviderName", out _));
        Assert.Null(DbConnections.TryResolve(cfg, "Odd", out var e3));
        Assert.Contains("Odd_ProviderName", e3);
    }

    [Fact]
    public void Names_skips_companion_keys_and_blank_values()
    {
        var cfg = Config(new()
        {
            ["ConnectionStrings:Billing"]              = "Data Source=BILLPRD;",
            ["ConnectionStrings:Billing_ProviderName"] = "Oracle",
            ["ConnectionStrings:Empty"]                = "",
        });
        Assert.Equal(new[] { "Billing" }, DbConnections.Names(cfg));
    }

    // ── Oracle quoting ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Status",        "\"STATUS\"")]       // plain → upper-cased (= unquoted semantics)
    [InlineData("order_id",      "\"ORDER_ID\"")]
    [InlineData("[Status]",      "\"STATUS\"")]       // T-SQL habit tolerated
    [InlineData("\"MixedCase\"", "\"MixedCase\"")]    // operator-quoted → exact
    [InlineData("Order Date",    "\"Order Date\"")]   // not plain → exact
    [InlineData("DATE",          "\"DATE\"")]         // reserved word still works quoted
    public void Oracle_QuoteIdentifier(string input, string expected)
        => Assert.Equal(expected, SqlDialect.Oracle.QuoteIdentifier(input));

    [Theory]
    [InlineData("a\"b")]
    [InlineData("\"\"")]
    public void Oracle_QuoteIdentifier_refuses_what_Oracle_cannot_name(string input)
        => Assert.Throws<InvalidOperationException>(() => SqlDialect.Oracle.QuoteIdentifier(input));

    [Fact]
    public void Oracle_QuoteTable_and_ColumnRef_quote_every_part()
    {
        Assert.Equal("\"HR\".\"EMPLOYEES\"", SqlDialect.Oracle.QuoteTable("hr.employees"));
        Assert.Equal("\"J\".\"FILE_PATH\"",  SqlDialect.Oracle.QuoteColumnRef("j.file_path"));
    }

    // ── Filter clause ────────────────────────────────────────────────────────

    [Fact]
    public void Oracle_BuildFilterClause_uses_prefixed_named_binds()
    {
        var rule = new ScanCheckRule
        {
            CheckType = CheckType.ValueEquals, TargetField = "Status", ExpectedValue = "8",
            Conditions =
            [
                new ScanCheckRuleCondition { ConditionOrder = 1, Kind = CheckType.ColumnRange, TargetField = "Amount", MinValue = 10, MaxValue = 100 },
            ],
        };

        var (clause, ps) = DatabaseScanStrategy.BuildFilterClause(rule, SqlDialect.Oracle);

        Assert.Equal("(\"STATUS\" = :p_ExactVal) AND (\"AMOUNT\" < :p_C0_Min OR \"AMOUNT\" > :p_C0_Max)", clause);
        Assert.Equal(new[] { "p_ExactVal", "p_C0_Min", "p_C0_Max" }, ps.Select(p => p.Name));
    }

    [Fact]
    public void SqlServer_BuildFilterClause_is_unchanged_by_the_dialect_split()
    {
        var rule = new ScanCheckRule { CheckType = CheckType.ColumnRange, TargetField = "Amount", MinValue = 1, MaxValue = 2 };
        Assert.Equal(DatabaseScanStrategy.BuildFilterClause(rule),
                     DatabaseScanStrategy.BuildFilterClause(rule, SqlDialect.SqlServer),
                     new FilterComparer());
        Assert.Equal("([Amount] < @Min OR [Amount] > @Max)", DatabaseScanStrategy.BuildFilterClause(rule).Clause);
    }

    // ── Query shape ──────────────────────────────────────────────────────────

    [Fact]
    public void Oracle_MatchingRowsQuery_caps_with_ROWNUM_after_ordering_by_the_watermark()
    {
        var sql = SqlDialect.Oracle.MatchingRowsQuery(
            "\"STATUS\"", ", TO_CHAR(\"UPDATED_AT\") AS \"_WatermarkVal\"", "\"APP\".\"EVENTS\"",
            "(\"STATUS\" = :p_ExactVal) AND \"UPDATED_AT\" > :p_Watermark", "\"UPDATED_AT\"", 500);

        Assert.Contains("SELECT TO_CHAR(ROWNUM) AS \"_RowKey\", q.*", sql);
        Assert.Contains("ORDER BY \"UPDATED_AT\" ASC", sql);
        Assert.Contains("WHERE ROWNUM <= 500", sql);
        Assert.DoesNotContain("TOP", sql);
        Assert.DoesNotContain("[", sql);
        Assert.DoesNotContain("@", sql);
        // The ORDER BY must sit inside the subquery — ROWNUM is assigned after it.
        Assert.True(sql.IndexOf("ORDER BY", StringComparison.Ordinal) < sql.IndexOf(") q", StringComparison.Ordinal));
    }

    [Fact]
    public void Oracle_MatchingRowsQuery_without_watermark_has_no_ORDER_BY()
        => Assert.DoesNotContain("ORDER BY",
            SqlDialect.Oracle.MatchingRowsQuery("\"STATUS\"", "", "\"EVENTS\"", "(\"STATUS\" = :p_ExactVal)", null, 500));

    [Fact]
    public void SqlServer_MatchingRowsQuery_keeps_the_TSQL_shape()
    {
        var sql = SqlDialect.SqlServer.MatchingRowsQuery("[Status]", "", "[dbo].[Events]", "([Status] = @ExactVal)", null, 500);
        Assert.Contains("SELECT TOP 500", sql);
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY (SELECT NULL))", sql);
        Assert.Contains("FROM [dbo].[Events]", sql);
    }

    [Fact]
    public void Oracle_FilteredMaxQuery_is_type_agnostic_TO_CHAR()
        => Assert.Equal("SELECT TO_CHAR(MAX(\"UPDATED_AT\")) FROM \"EVENTS\" WHERE (\"STATUS\" = :p_ExactVal)",
            SqlDialect.Oracle.FilteredMaxQuery("\"UPDATED_AT\"", "\"EVENTS\"", "(\"STATUS\" = :p_ExactVal)"));

    // ── SqlQuery text ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1 FROM dual;",       "SELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual ;  \n",  "SELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual",        "SELECT 1 FROM dual")]
    public void TrimOracleTerminator_strips_the_SQLPlus_semicolon(string input, string expected)
        => Assert.Equal(expected, SqlQueryRunner.TrimOracleTerminator(input));

    private sealed class FilterComparer : IEqualityComparer<(string Clause, List<(string Name, object Value)> Params)>
    {
        public bool Equals((string Clause, List<(string Name, object Value)> Params) a, (string Clause, List<(string Name, object Value)> Params) b)
            => a.Clause == b.Clause && a.Params.SequenceEqual(b.Params);
        public int GetHashCode((string Clause, List<(string Name, object Value)> Params) x) => x.Clause.GetHashCode();
    }
}
