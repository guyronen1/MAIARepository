namespace Maia.Core.Results;

/// <summary>
/// Coarse type family of a source column, computed server-side so the scan-rule drawer
/// can narrow its column dropdowns per check kind (Contains → string, InList /
/// ColumnRange → number, Watermark → date or number). <see cref="Other"/> covers
/// everything else (binary, GUID, XML, intervals…) and stays selectable everywhere.
/// </summary>
public static class ColumnTypeCategory
{
    public const string String = "string";
    public const string Number = "number";
    public const string Date   = "date";
    public const string Other  = "other";
}

/// <summary>One column of a source table, in table (column-id) order.
/// <c>DataType</c> is the engine's own spelling for display, e.g. <c>nvarchar(100)</c>,
/// <c>NUMBER(10,2)</c>; <c>TypeCategory</c> is a <see cref="ColumnTypeCategory"/>.</summary>
public sealed record TableColumnInfo(string Name, string DataType, bool IsNullable, string TypeCategory);

/// <summary>Outcome of <c>ISourceSchemaReader.GetColumnsAsync</c>.</summary>
public enum TableColumnsStatus
{
    Ok,
    /// <summary>No scan source with that id.</summary>
    SourceNotFound,
    /// <summary>The source isn't a Database source.</summary>
    NotDatabaseSource,
    /// <summary>No table name given.</summary>
    TableRequired,
    /// <summary>Not a single <c>table</c> / <c>schema.table</c> — a JOIN, an alias, a
    /// three-part name… The drawer keeps free-text column inputs.</summary>
    TableNotResolvable,
    /// <summary>The source's ConnectionName isn't configured, or its provider is unknown.</summary>
    ConnectionUnresolved,
    /// <summary>Opening the connection failed (server down, login refused, timeout).</summary>
    ConnectionFailed,
    /// <summary>The catalog query itself failed (e.g. no permission on the catalog views).</summary>
    SchemaReadFailed,
    /// <summary>The catalog has no such table — or the connection's login can't see it.</summary>
    TableNotFound,
}

public sealed record TableColumnsResult(
    TableColumnsStatus              Status,
    string?                         Message,
    IReadOnlyList<TableColumnInfo>  Columns)
{
    public static TableColumnsResult Fail(TableColumnsStatus status, string message) => new(status, message, []);
    public static TableColumnsResult Found(IReadOnlyList<TableColumnInfo> columns) => new(TableColumnsStatus.Ok, null, columns);
}
