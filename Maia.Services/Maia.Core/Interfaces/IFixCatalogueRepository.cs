using Maia.Core.Results;

namespace Maia.Core.Interfaces;

/// <summary>
/// Reads fix policy entries from the FixPolicyRules table for suggestion
/// generation. Used by DbFixCatalogue to give operators runtime control over
/// fix actions. Lookup mirrors IFixPolicyRepository: per-MonitoredJob override
/// wins over JobType default. When <paramref name="monitoredJobId"/> is null,
/// only the default layer is consulted.
/// </summary>
public interface IFixCatalogueRepository
{
    Task<FixCatalogueEntry?> GetEntryAsync(
        string errorTypeCode,
        int jobTypeId,
        int? monitoredJobId = null,
        CancellationToken ct = default);
}
