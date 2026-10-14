using Maia.Core.Entities;
using Maia.Core.Results;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Column lookup for the scan-rule drawer — the DB-free halves: which SourceTable
/// values resolve to one table (a JOIN / alias must fall back to free text), the
/// catalog SQL + binds per engine (names bound, never spliced; Oracle names follow the
/// scan's identifier rule), the type-category mapping, and the dead ends that are
/// decided before any connection opens.
/// </summary>
public sealed class SourceSchemaReaderTests
{
    // ── Table-name parsing ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Files",            null,     "Files")]
    [InlineData("dbo.Files",        "dbo",    "Files")]
    [InlineData("  dbo.Files  ",    "dbo",    "Files")]
    [InlineData("[dbo].[My Table]", "[dbo]",  "[My Table]")]
    [InlineData("HR.\"MixedCase\"", "HR",     "\"MixedCase\"")]
    [InlineData("[my.schema].T",    "[my.schema]", "T")]
    public void TryParseTable_accepts_table_and_schema_table(string raw, string? schema, string table)
    {
        Assert.True(SourceSchemaReader.TryParseTable(raw, out var s, out var t));
        Assert.Equal(schema, s);
        Assert.Equal(table, t);
    }

    [Theory]
    [InlineData("dbo.Files f")]                                       // alias
    [InlineData("dbo.Files f JOIN dbo.Jobs j ON j.Id = f.JobId")]     // JOIN
    [InlineData("dbo.Files, dbo.Jobs")]
    [InlineData("B2B_Test.dbo.Files")]                                // three-part
    [InlineData("dbo.")]
    [InlineData(".Files")]
    [InlineData("[dbo] f")]
    [InlineData("[unterminated")]
    [InlineData("[]")]
    [InlineData("Files;DROP TABLE x")]
    [InlineData("(SELECT 1) q")]
    public void TryParseTable_rejects_anything_but_one_table(string raw)
        => Assert.False(SourceSchemaReader.TryParseTable(raw, out _, out _));

    // ── Catalog SQL ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null,    "Files",      "[Files]")]
    [InlineData("dbo",   "Files",      "[dbo].[Files]")]
    [InlineData("[dbo]", "[My Table]", "[dbo].[My Table]")]
    [InlineData("dbo",   "[a]]b]",     "[dbo].[a]]]]b]")]    // never reached via TryParseTable; escaping still holds
    public void SqlServer_query_binds_the_bracket_quoted_object_name(string? schema, string table, string expected)
    {
        var q = SourceSchemaReader.SqlServerColumnsQuery(schema, table);

        Assert.Contains("FROM sys.columns AS c", q.Sql);
        Assert.Contains("OBJECT_ID(@Object)", q.Sql);
        Assert.Contains("ORDER BY c.column_id", q.Sql);
        Assert.DoesNotContain(table.Trim('[', ']'), q.Sql);       // the name is a bind, not SQL text
        var p = Assert.Single(q.Params);
        Assert.Equal("Object", p.Key);
        Assert.Equal(expected, p.Value);
    }

    [Theory]
    [InlineData("hr",   "orders",        "HR",  "ORDERS")]           // plain → upper, as unquoted Oracle
    [InlineData("HR",   "\"MixedCase\"", "HR",  "MixedCase")]        // quoted → exact
    [InlineData("[hr]", "[orders]",      "HR",  "ORDERS")]
    public void Oracle_query_binds_owner_and_table_in_catalog_spelling(string schema, string table, string owner, string tableName)
    {
        var q = SourceSchemaReader.OracleColumnsQuery(schema, table);

        Assert.Contains("FROM ALL_TAB_COLUMNS", q.Sql);
        Assert.Contains("OWNER = :p_Owner AND TABLE_NAME = :p_Table", q.Sql);
        Assert.Contains("ORDER BY COLUMN_ID", q.Sql);
        Assert.DoesNotContain(tableName, q.Sql);
        Assert.Equal(new[] { ("Owner", (object)owner), ("Table", tableName) },
            q.Params.Select(p => (p.Key, p.Value)));
    }

    [Fact]
    public void Oracle_query_without_owner_uses_the_session_schema()
    {
        var q = SourceSchemaReader.OracleColumnsQuery(null, "orders");

        Assert.Contains("OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AND TABLE_NAME = :p_Table", q.Sql);
        var p = Assert.Single(q.Params);
        Assert.Equal(("Table", (object)"ORDERS"), (p.Key, p.Value));
    }

    [Fact]
    public void Oracle_synonym_query_prefers_a_private_synonym_over_PUBLIC()
    {
        var q = SourceSchemaReader.OracleSynonymQuery("orders");

        Assert.Contains("FROM ALL_SYNONYMS", q.Sql);
        Assert.Contains("SYNONYM_NAME = :p_Table AND DB_LINK IS NULL", q.Sql);
        Assert.Contains("OWNER IN (SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'), 'PUBLIC')", q.Sql);
        Assert.Contains("ORDER BY CASE WHEN OWNER = 'PUBLIC' THEN 1 ELSE 0 END", q.Sql);
        Assert.Equal("ORDERS", Assert.Single(q.Params).Value);
    }

    [Fact]
    public void Oracle_query_refuses_a_name_with_an_embedded_quote()
        => Assert.Throws<InvalidOperationException>(() => SourceSchemaReader.OracleColumnsQuery(null, "\"a\"b\""));

    // ── Type mapping ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("nvarchar", "string")] [InlineData("varchar", "string")] [InlineData("char", "string")]
    [InlineData("nchar", "string")]    [InlineData("text", "string")]    [InlineData("ntext", "string")]
    [InlineData("int", "number")]      [InlineData("bigint", "number")]  [InlineData("tinyint", "number")]
    [InlineData("decimal", "number")]  [InlineData("numeric", "number")] [InlineData("float", "number")]
    [InlineData("money", "number")]    [InlineData("bit", "number")]
    [InlineData("datetime", "date")]   [InlineData("datetime2", "date")] [InlineData("date", "date")]
    [InlineData("smalldatetime", "date")] [InlineData("datetimeoffset", "date")]
    [InlineData("time", "other")]      [InlineData("uniqueidentifier", "other")] [InlineData("varbinary", "other")]
    [InlineData("xml", "other")]       [InlineData("timestamp", "other")] [InlineData("hierarchyid", "other")]
    public void SqlServer_category(string systemType, string category)
        => Assert.Equal(category, SourceSchemaReader.SqlServerCategory(systemType));

    [Theory]
    [InlineData("VARCHAR2", "string")] [InlineData("NVARCHAR2", "string")] [InlineData("CHAR", "string")]
    [InlineData("CLOB", "string")]     [InlineData("NCLOB", "string")]
    [InlineData("NUMBER", "number")]   [InlineData("FLOAT", "number")]     [InlineData("BINARY_DOUBLE", "number")]
    [InlineData("DATE", "date")]       [InlineData("TIMESTAMP(6)", "date")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "date")] [InlineData("TIMESTAMP(9) WITH LOCAL TIME ZONE", "date")]
    [InlineData("RAW", "other")]       [InlineData("BLOB", "other")]       [InlineData("ROWID", "other")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "other")]
    public void Oracle_category(string dataType, string category)
        => Assert.Equal(category, SourceSchemaReader.OracleCategory(dataType));

    [Theory]
    [InlineData("nvarchar", "nvarchar", 100, 0, 0, "nvarchar(50)")]   // max_length is bytes
    [InlineData("nvarchar", "nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", "varchar", 20, 0, 0, "varchar(20)")]
    [InlineData("decimal", "decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("datetime2", "datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", "int", 4, 10, 0, "int")]
    [InlineData("sysname", "nvarchar", 256, 0, 0, "sysname")]           // alias type shows its own name
    public void SqlServer_display_type(string user, string system, int len, int prec, int scale, string expected)
        => Assert.Equal(expected, SourceSchemaReader.SqlServerDisplayType(user, system, len, prec, scale));

    [Theory]
    [InlineData("VARCHAR2", 200, 50, null, null, "VARCHAR2(50)")]
    [InlineData("NUMBER", 22, 0, 10, 2, "NUMBER(10,2)")]
    [InlineData("NUMBER", 22, 0, 10, 0, "NUMBER(10)")]
    [InlineData("NUMBER", 22, 0, null, 0, "NUMBER(*,0)")]
    [InlineData("NUMBER", 22, 0, null, null, "NUMBER")]
    [InlineData("DATE", 7, 0, null, null, "DATE")]
    public void Oracle_display_type(string type, int? len, int? charLen, int? prec, int? scale, string expected)
        => Assert.Equal(expected, SourceSchemaReader.OracleDisplayType(type, len, charLen, prec, scale));

    // ── Dead ends decided before any connection opens ───────────────────────

    private static SourceSchemaReader Reader(Dictionary<string, string?> cfg) =>
        new(null!, new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(),
            NullLogger<SourceSchemaReader>.Instance);

    private static ScanSource DbSource(string? connection = "B2BTest") => new()
    {
        Name = "db", ConnectionName = connection,
        ScanTypeDefinition = new ScanTypeDefinition { ScanTypeId = 2, Name = "Database" },
    };

    [Fact]
    public async Task Non_database_source_is_rejected()
    {
        var src = new ScanSource { Name = "fs", ScanTypeDefinition = new ScanTypeDefinition { Name = "FileSystem" } };
        var r = await Reader(new()).ReadColumnsAsync(src, "dbo.Files", default);
        Assert.Equal(TableColumnsStatus.NotDatabaseSource, r.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Blank_table_is_TableRequired(string? table)
        => Assert.Equal(TableColumnsStatus.TableRequired,
            (await Reader(new()).ReadColumnsAsync(DbSource(), table, default)).Status);

    [Fact]
    public async Task Join_table_is_TableNotResolvable_without_touching_the_connection()
    {
        // No connection string configured at all — the parse must short-circuit first.
        var r = await Reader(new()).ReadColumnsAsync(DbSource(), "dbo.Files f JOIN dbo.Jobs j ON j.Id = f.JobId", default);
        Assert.Equal(TableColumnsStatus.TableNotResolvable, r.Status);
        Assert.Empty(r.Columns);
    }

    [Fact]
    public async Task Unconfigured_connection_is_ConnectionUnresolved()
    {
        var r = await Reader(new()).ReadColumnsAsync(DbSource("Nope"), "dbo.Files", default);
        Assert.Equal(TableColumnsStatus.ConnectionUnresolved, r.Status);
        Assert.Contains("Nope", r.Message);
    }

    [Fact]
    public async Task Blank_connection_name_resolves_DefaultConnection_like_the_scan()
    {
        var r = await Reader(new()).ReadColumnsAsync(DbSource(connection: null), "dbo.Files", default);
        Assert.Equal(TableColumnsStatus.ConnectionUnresolved, r.Status);
        Assert.Contains("DefaultConnection", r.Message);
    }

    [Fact]
    public async Task Unknown_provider_is_ConnectionUnresolved()
    {
        var r = await Reader(new()
        {
            ["ConnectionStrings:B2BTest"]              = "Server=x;",
            ["ConnectionStrings:B2BTest_ProviderName"] = "MySql",
        }).ReadColumnsAsync(DbSource(), "dbo.Files", default);
        Assert.Equal(TableColumnsStatus.ConnectionUnresolved, r.Status);
    }

    [Fact]
    public async Task Malformed_connection_string_is_ConnectionFailed_not_thrown()
    {
        var r = await Reader(new() { ["ConnectionStrings:B2BTest"] = "this is not a connection string" })
            .ReadColumnsAsync(DbSource(), "dbo.Files", default);
        Assert.Equal(TableColumnsStatus.ConnectionFailed, r.Status);
    }
}
