using Maia.Core.Results;

namespace Maia.Core.Interfaces;

/// <summary>
/// Reads the live source data behind a failure for the "View Source" popup:
/// the source row (all columns) for a Database failure, located by the failure's
/// <c>SourceId</c> through the producing rule's <c>SourceIdColumn</c>; the log file
/// for a FileSystem failure; the data file for a FileContent failure.
///
/// Read-only. Paths and table names come from the stored failure + its scan config,
/// never from the caller. Expected dead ends (no Source ID column, file rolled away,
/// connection down) come back as <see cref="SourceDataKind.Unavailable"/>, not thrown.
/// </summary>
public interface ISourceDataReader
{
    /// <returns>Null when the failure doesn't exist.</returns>
    Task<SourceDataResult?> ReadAsync(int failureId, CancellationToken ct = default);
}
