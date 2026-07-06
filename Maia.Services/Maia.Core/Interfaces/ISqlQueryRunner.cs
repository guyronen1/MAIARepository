namespace Maia.Core.Interfaces;

/// <summary>
/// Executes operator-written T-SQL text against a connection string and returns
/// the result rows as column-name → value maps.
///
/// Deliberately narrow: this is a TESTABILITY seam for the CheckType.SqlQuery
/// branch of <c>DatabaseScanStrategy</c> (which would otherwise be untestable
/// without a live database), NOT a database-engine abstraction. The existing
/// ColumnRange / ValueEquals paths still talk to SqlConnection directly.
///
/// Rows are keyed by column name (case-insensitive) so the strategy can read
/// the operator-declared TargetField / SourceIdColumn by name and detect a
/// missing column (key absent) rather than binding by ordinal — the result
/// shape is whatever the operator's SELECT / EXEC returned.
/// </summary>
public interface ISqlQueryRunner
{
    /// <param name="connectionString">Resolved connection string (admin-configured).</param>
    /// <param name="commandText">Operator-written T-SQL run as CommandType.Text —
    /// a SELECT or an "EXEC sp_Name @p=..." statement.</param>
    /// <param name="maxRows">Hard cap; the runner stops reading after this many rows.</param>
    /// <returns>Up to <paramref name="maxRows"/> rows; each a case-insensitive
    /// column-name → value map (DBNull → null).</returns>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        string connectionString, string commandText, int maxRows, CancellationToken ct = default);
}
