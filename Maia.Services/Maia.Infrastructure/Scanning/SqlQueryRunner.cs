using Maia.Core.Enums;
using Maia.Core.Interfaces;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// Production <see cref="ISqlQueryRunner"/>: opens a connection with the provider's
/// driver (<see cref="DbConnections"/>), runs the operator's text command, and projects
/// each row into a case-insensitive column-name → value map. Caps reading at
/// <c>maxRows</c> in code (we can't inject TOP / ROWNUM into an arbitrary query or
/// stored-proc call).
///
/// The command runs as CommandType.Text under the configured connection user's
/// permissions — the SQL is operator-authored and executed verbatim. See the
/// SqlQuery security note in CLAUDE.md (use a least-privilege read-only login).
/// </summary>
public sealed class SqlQueryRunner : ISqlQueryRunner
{
    // Matches the per-step executor default; long enough for a real check query,
    // short enough that a runaway query fails the tick rather than wedging it.
    private const int CommandTimeoutSeconds = 60;

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DbProvider provider, string connectionString, string commandText, int maxRows, CancellationToken ct = default)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await using var conn = await DbConnections.OpenAsync(provider, connectionString, ct);
        var sql = provider == DbProvider.Oracle ? TrimOracleTerminator(commandText) : commandText;
        await using var cmd = DbConnections.CreateCommand(conn, sql, CommandTimeoutSeconds);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (rows.Count < maxRows && await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = DbConnections.ReadValue(reader, i);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// A trailing ';' is the SQL*Plus / SQL Developer statement terminator, not part of
    /// the statement — sent to the server it fails with ORA-00911. Operators paste
    /// queries straight from those tools, so strip it.
    /// </summary>
    internal static string TrimOracleTerminator(string sql) => sql.TrimEnd().TrimEnd(';').TrimEnd();
}
