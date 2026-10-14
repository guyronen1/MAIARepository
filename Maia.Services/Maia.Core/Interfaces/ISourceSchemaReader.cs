using Maia.Core.Results;

namespace Maia.Core.Interfaces;

/// <summary>
/// Reads the column list of one table on a Database scan source's own database, for
/// the scan-rule drawer's column dropdowns. The connection is resolved exactly like the
/// scan resolves it (the source's <c>ConnectionName</c>, <c>DefaultConnection</c> when
/// blank, engine from <c>{Name}_ProviderName</c>), so the list shows what the scan will
/// actually see.
///
/// Read-only catalog query (SQL Server <c>sys.columns</c>, Oracle <c>ALL_TAB_COLUMNS</c>),
/// names always bound as parameters, short timeout. Expected dead ends (a JOIN in the
/// table, bad connection, missing table) come back as a <see cref="TableColumnsStatus"/>
/// with an operator-language message — never thrown.
/// </summary>
public interface ISourceSchemaReader
{
    Task<TableColumnsResult> GetColumnsAsync(int scanSourceId, string? table, CancellationToken ct = default);
}
