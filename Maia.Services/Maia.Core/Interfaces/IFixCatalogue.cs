using Maia.Core.Results;

namespace Maia.Core.Interfaces;

/// <summary>
/// Maps a (JobType, ErrorType) pair (or a per-MonitoredJob override) to a fix
/// suggestion entry. Lookup priority mirrors IFixPolicyRepository: override
/// wins over default. When <paramref name="monitoredJobId"/> is null, only
/// the default layer is consulted.
///
/// Default implementation: static dictionary keyed by ErrorType only
/// (last-resort fallback, ignores both jobTypeId and monitoredJobId).
/// Swap in DbFixCatalogue for operator-configurable DB-driven entries with
/// override-then-default lookup and dictionary fallback.
/// </summary>
public interface IFixCatalogue
{
    Task<FixCatalogueEntry?> GetEntryAsync(
        string errorTypeCode,
        int jobTypeId,
        int? monitoredJobId = null,
        CancellationToken ct = default);
}
