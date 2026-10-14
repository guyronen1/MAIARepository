using System.Data.Common;
using System.Text.RegularExpressions;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.Fix;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// Column list of one table on a Database scan source's own database — see
/// <see cref="ISourceSchemaReader"/>. The table must be a single <c>table</c> or
/// <c>schema.table</c> (plain, <c>[bracketed]</c> or <c>"quoted"</c> parts); anything
/// else (a JOIN, an alias, a three-part name) is <see cref="TableColumnsStatus.TableNotResolvable"/>
/// and the drawer keeps free text.
///  • SQL Server: <c>sys.columns</c> for <c>OBJECT_ID(@Object)</c>, the name passed as a
///    bracket-quoted parameter (no schema → the login's default schema, as in the scan).
///  • Oracle: <c>ALL_TAB_COLUMNS</c> with owner + table bound, names normalised by
///    <see cref="SqlDialect.OracleDialect.CatalogName"/> (plain → UPPER, "Quoted" → exact).
///    No owner → the session's current schema (the connecting user unless a logon trigger
///    moved it), then a private / PUBLIC synonym of that name — read-only Oracle users
///    usually see application tables through synonyms.
/// </summary>
public sealed class SourceSchemaReader(
    IDbContextFactory<MaiaDbContext> dbFactory,
    IConfiguration                   config,
    ILogger<SourceSchemaReader>      logger) : ISourceSchemaReader
{
    /// <summary>A catalog query: SQL plus its binds (logical name → value).</summary>
    internal sealed record CatalogQuery(string Sql, IReadOnlyList<KeyValuePair<string, object>> Params);

    public async Task<TableColumnsResult> GetColumnsAsync(int scanSourceId, string? table, CancellationToken ct = default)
    {
        ScanSource? source;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            source = await db.ScanSources.AsNoTracking()
                .Include(s => s.ScanTypeDefinition)
                .FirstOrDefaultAsync(s => s.ScanSourceId == scanSourceId, ct);
        }
        if (source is null)
            return TableColumnsResult.Fail(TableColumnsStatus.SourceNotFound, $"Scan source {scanSourceId} does not exist.");
        return await ReadColumnsAsync(source, table, ct);
    }

    /// <summary>Everything after the source lookup — the seam the unit tests drive.</summary>
    internal async Task<TableColumnsResult> ReadColumnsAsync(ScanSource source, string? table, CancellationToken ct)
    {
        if (source.ScanTypeDefinition is null || source.ScanType != ScanType.Database)
            return TableColumnsResult.Fail(TableColumnsStatus.NotDatabaseSource,
                $"Scan source '{source.Name}' is not a Database source.");
        if (string.IsNullOrWhiteSpace(table))
            return TableColumnsResult.Fail(TableColumnsStatus.TableRequired, "Enter a Source Table to list its columns.");
        if (!TryParseTable(table, out var schemaPart, out var tablePart))
            return TableColumnsResult.Fail(TableColumnsStatus.TableNotResolvable,
                $"'{table.Trim()}' is not a single table (schema.table) — type the column names.");

        var connName = source.ConnectionName ?? "DefaultConnection";
        var conn = DbConnections.TryResolve(config, connName, out var resolveError);
        if (conn is null)
            return TableColumnsResult.Fail(TableColumnsStatus.ConnectionUnresolved, resolveError!);

        CatalogQuery query;
        try
        {
            query = conn.Provider == DbProvider.Oracle
                ? OracleColumnsQuery(schemaPart, tablePart)
                : SqlServerColumnsQuery(schemaPart, tablePart);
        }
        catch (InvalidOperationException ex)     // a name Oracle can't hold (embedded ")
        {
            return TableColumnsResult.Fail(TableColumnsStatus.TableNotResolvable, ex.Message);
        }

        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);
        var timeoutSeconds = (int)ExecutorTimeouts.Default.TotalSeconds;

        DbConnection db;
        try
        {
            db = await DbConnections.OpenAsync(conn, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return TableColumnsResult.Fail(TableColumnsStatus.ConnectionFailed,
                $"Connecting to '{connName}' timed out after {timeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "Column lookup: could not open connection {Connection}", connName);
            return TableColumnsResult.Fail(TableColumnsStatus.ConnectionFailed,
                $"Could not connect to '{connName}': {ex.Message}");
        }

        await using (db)
        {
            try
            {
                var columns = await ReadAsync(db, conn.Provider, query, timeoutSeconds, cts.Token);
                if (columns.Count == 0 && conn.Provider == DbProvider.Oracle && schemaPart is null)
                {
                    // Unqualified Oracle name that isn't in the current schema: follow a
                    // private, then PUBLIC, synonym (one hop) to the real owner + table.
                    var target = await ResolveOracleSynonymAsync(db, tablePart, timeoutSeconds, cts.Token);
                    if (target is { } t)
                        columns = await ReadAsync(db, conn.Provider,
                            OracleColumnsQueryExact(t.Owner, t.Table), timeoutSeconds, cts.Token);
                }

                return columns.Count == 0
                    ? TableColumnsResult.Fail(TableColumnsStatus.TableNotFound,
                        $"Table '{table.Trim()}' was not found on '{connName}', or its login can't see it.")
                    : TableColumnsResult.Found(columns);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return TableColumnsResult.Fail(TableColumnsStatus.SchemaReadFailed,
                    $"Reading the columns of '{table.Trim()}' timed out after {timeoutSeconds}s.");
            }
            catch (DbException ex)
            {
                logger.LogWarning(ex, "Column lookup failed for {Table} on {Connection}", table, connName);
                return TableColumnsResult.Fail(TableColumnsStatus.SchemaReadFailed,
                    $"Could not read the columns of '{table.Trim()}' on '{connName}': {ex.Message}");
            }
        }
    }

    private static async Task<List<TableColumnInfo>> ReadAsync(
        DbConnection db, DbProvider provider, CatalogQuery query, int timeoutSeconds, CancellationToken ct)
    {
        await using var cmd = Command(db, provider, query, timeoutSeconds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TableColumnInfo>();
        while (await reader.ReadAsync(ct))
            list.Add(provider == DbProvider.Oracle ? ReadOracleColumn(reader) : ReadSqlServerColumn(reader));
        return list;
    }

    private static async Task<(string Owner, string Table)?> ResolveOracleSynonymAsync(
        DbConnection db, string tablePart, int timeoutSeconds, CancellationToken ct)
    {
        await using var cmd = Command(db, DbProvider.Oracle, OracleSynonymQuery(tablePart), timeoutSeconds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    private static DbCommand Command(DbConnection db, DbProvider provider, CatalogQuery query, int timeoutSeconds)
    {
        var dialect = SqlDialect.For(provider);
        var cmd = DbConnections.CreateCommand(db, query.Sql, timeoutSeconds);
        foreach (var (name, value) in query.Params)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = dialect.ParamName(name);
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    // ── Table-name parsing ──────────────────────────────────────────────────────

    // A part the scan would accept unquoted: no whitespace or punctuation (Unicode letters ok).
    private static readonly Regex PlainPart = new(@"^[\p{L}_@#][\p{L}\p{N}_@#$]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits <paramref name="raw"/> into an optional schema and a table, each kept in
    /// its original spelling (<c>[x]</c> / <c>"x"</c> delimiters included) so each engine
    /// can apply its own identifier rule. False for anything that isn't one or two
    /// parts — a JOIN, an alias, a comma, a three-part name, an empty part.
    /// </summary>
    internal static bool TryParseTable(string raw, out string? schema, out string table)
    {
        schema = null; table = "";
        var parts = new List<string>();
        var s = raw.Trim();
        var i = 0;
        while (true)
        {
            if (i >= s.Length) return false;               // empty part (leading/trailing dot)
            int start = i;
            if (s[i] is '[' or '"')
            {
                var close = s[i] == '[' ? ']' : '"';
                var end = s.IndexOf(close, i + 1);
                if (end < 0 || end == i + 1) return false;  // unterminated or empty
                i = end + 1;
            }
            else
            {
                while (i < s.Length && s[i] != '.') i++;
                if (!PlainPart.IsMatch(s[start..i])) return false;
            }
            parts.Add(s[start..i]);
            if (i == s.Length) break;
            if (s[i] != '.') return false;                  // e.g. "[dbo] f" or "[a]x"
            i++;
        }
        if (parts.Count is < 1 or > 2) return false;
        if (parts.Count == 2) schema = parts[0];
        table = parts[^1];
        return true;
    }

    /// <summary>A part without its delimiters (SQL Server: case follows the collation).</summary>
    private static string Unquote(string part) =>
        part.Length >= 2 && (part[0] == '[' || part[0] == '"') ? part[1..^1] : part;

    // ── Catalog queries ─────────────────────────────────────────────────────────

    internal static CatalogQuery SqlServerColumnsQuery(string? schemaPart, string tablePart)
    {
        static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";
        var objectName = schemaPart is null
            ? Bracket(Unquote(tablePart))
            : Bracket(Unquote(schemaPart)) + "." + Bracket(Unquote(tablePart));
        return new CatalogQuery("""
            SELECT c.name, TYPE_NAME(c.user_type_id), TYPE_NAME(c.system_type_id),
                   c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@Object)
            ORDER BY c.column_id
            """, [new("Object", objectName)]);
    }

    private const string OracleColumnsSelect = """
        SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE
        FROM ALL_TAB_COLUMNS
        """;

    internal static CatalogQuery OracleColumnsQuery(string? schemaPart, string tablePart)
    {
        var tableName = SqlDialect.OracleDialect.CatalogName(tablePart);
        return schemaPart is null
            ? new CatalogQuery(OracleColumnsSelect + """

                WHERE OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AND TABLE_NAME = :p_Table
                ORDER BY COLUMN_ID
                """, [new("Table", tableName)])
            : OracleColumnsQueryExact(SqlDialect.OracleDialect.CatalogName(schemaPart), tableName);
    }

    /// <summary>Owner + table already in catalog spelling (e.g. from ALL_SYNONYMS).</summary>
    internal static CatalogQuery OracleColumnsQueryExact(string owner, string table) =>
        new(OracleColumnsSelect + """

            WHERE OWNER = :p_Owner AND TABLE_NAME = :p_Table
            ORDER BY COLUMN_ID
            """, [new("Owner", owner), new("Table", table)]);

    internal static CatalogQuery OracleSynonymQuery(string tablePart) =>
        new("""
            SELECT TABLE_OWNER, TABLE_NAME
            FROM ALL_SYNONYMS
            WHERE SYNONYM_NAME = :p_Table AND DB_LINK IS NULL
              AND OWNER IN (SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'), 'PUBLIC')
            ORDER BY CASE WHEN OWNER = 'PUBLIC' THEN 1 ELSE 0 END
            """, [new("Table", SqlDialect.OracleDialect.CatalogName(tablePart))]);

    // ── Row → column ────────────────────────────────────────────────────────────

    private static int? Int(DbDataReader r, int i) =>
        DbConnections.ReadValue(r, i) is { } v ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static TableColumnInfo ReadSqlServerColumn(DbDataReader r)
    {
        var userType   = r.IsDBNull(1) ? "unknown" : r.GetString(1);
        var systemType = r.IsDBNull(2) ? userType  : r.GetString(2);
        return new TableColumnInfo(
            r.GetString(0),
            SqlServerDisplayType(userType, systemType, Int(r, 3) ?? 0, Int(r, 4) ?? 0, Int(r, 5) ?? 0),
            !r.IsDBNull(6) && r.GetBoolean(6),
            SqlServerCategory(systemType));
    }

    private static TableColumnInfo ReadOracleColumn(DbDataReader r)
    {
        var dataType = r.IsDBNull(1) ? "UNKNOWN" : r.GetString(1);
        return new TableColumnInfo(
            r.GetString(0),
            OracleDisplayType(dataType, Int(r, 2), Int(r, 3), Int(r, 4), Int(r, 5)),
            !r.IsDBNull(6) && r.GetString(6) == "Y",
            OracleCategory(dataType));
    }

    // ── Type mapping ────────────────────────────────────────────────────────────

    private static readonly HashSet<string> SqlServerStrings =
        new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar", "text", "ntext", "sysname" };
    private static readonly HashSet<string> SqlServerNumbers =
        new(StringComparer.OrdinalIgnoreCase) { "tinyint", "smallint", "int", "bigint", "decimal", "numeric",
                                                "float", "real", "money", "smallmoney", "bit" };
    private static readonly HashSet<string> SqlServerDates =
        new(StringComparer.OrdinalIgnoreCase) { "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset" };

    /// <summary>Category from the column's SYSTEM type (so an alias type maps by its base).
    /// <c>bit</c> is a number (it compares to 0 / 1); <c>time</c> is other — a time of day
    /// wraps every midnight, so it's no scan cursor.</summary>
    internal static string SqlServerCategory(string systemType) =>
        SqlServerStrings.Contains(systemType) ? ColumnTypeCategory.String
        : SqlServerNumbers.Contains(systemType) ? ColumnTypeCategory.Number
        : SqlServerDates.Contains(systemType) ? ColumnTypeCategory.Date
        : ColumnTypeCategory.Other;

    /// <summary>Oracle <c>DATA_TYPE</c> (e.g. <c>VARCHAR2</c>, <c>TIMESTAMP(6) WITH TIME ZONE</c>).</summary>
    internal static string OracleCategory(string dataType)
    {
        var t = dataType.Trim().ToUpperInvariant();
        return t switch
        {
            "VARCHAR2" or "NVARCHAR2" or "VARCHAR" or "CHAR" or "NCHAR" or "CLOB" or "NCLOB" or "LONG"
                => ColumnTypeCategory.String,
            "NUMBER" or "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" or "INTEGER"
                => ColumnTypeCategory.Number,
            "DATE" => ColumnTypeCategory.Date,
            _ when t.StartsWith("TIMESTAMP", StringComparison.Ordinal) => ColumnTypeCategory.Date,
            _ => ColumnTypeCategory.Other,
        };
    }

    /// <summary>SQL Server type as declared, e.g. <c>nvarchar(100)</c>, <c>varchar(max)</c>,
    /// <c>decimal(18,2)</c>. Alias types show their own name.</summary>
    internal static string SqlServerDisplayType(string userType, string systemType, int maxLength, int precision, int scale)
    {
        if (!string.Equals(userType, systemType, StringComparison.OrdinalIgnoreCase)) return userType;
        return systemType.ToLowerInvariant() switch
        {
            "char" or "varchar" or "binary" or "varbinary" => $"{systemType}({(maxLength < 0 ? "max" : maxLength)})",
            "nchar" or "nvarchar" => $"{systemType}({(maxLength < 0 ? "max" : maxLength / 2)})",
            "decimal" or "numeric" => $"{systemType}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{systemType}({scale})",
            _ => systemType,
        };
    }

    /// <summary>Oracle type as declared, e.g. <c>VARCHAR2(50)</c>, <c>NUMBER(10,2)</c>.</summary>
    internal static string OracleDisplayType(string dataType, int? dataLength, int? charLength, int? precision, int? scale)
    {
        switch (dataType.ToUpperInvariant())
        {
            case "VARCHAR2": case "NVARCHAR2": case "VARCHAR": case "CHAR": case "NCHAR":
                return $"{dataType}({(charLength is > 0 ? charLength : dataLength)})";
            case "RAW":
                return $"{dataType}({dataLength})";
            case "NUMBER":
                if (precision is null) return scale == 0 ? "NUMBER(*,0)" : "NUMBER";
                return scale is null or 0 ? $"NUMBER({precision})" : $"NUMBER({precision},{scale})";
            default:
                return dataType;
        }
    }
}
