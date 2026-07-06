using System.Data;
using Maia.Core.Interfaces;
using Microsoft.Data.SqlClient;

namespace Maia.Infrastructure.Scanning;

/// <summary>
/// Production <see cref="ISqlQueryRunner"/>: opens a SqlConnection, runs the
/// operator's text command, and projects each row into a case-insensitive
/// column-name → value map. Caps reading at <c>maxRows</c> in code (we can't
/// inject TOP into an arbitrary query or stored-proc call).
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
        string connectionString, string commandText, int maxRows, CancellationToken ct = default)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(commandText, conn)
        {
            CommandType    = CommandType.Text,
            CommandTimeout = CommandTimeoutSeconds,
        };

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (rows.Count < maxRows && await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }
}
