using Maia.Core.Entities;

namespace Maia.Core.Interfaces;

/// <summary>
/// Fetches the FixPolicyRule that applies for execution. Two-layer lookup:
///
///   1. Override: when <paramref name="monitoredJobId"/> is provided and a
///      MonitoredJob-scoped enabled rule exists for (monitoredJobId, errorTypeId),
///      return that rule. Overrides win over JobType defaults.
///   2. Default: otherwise look up the JobType-level rule for
///      (jobTypeId, errorTypeId) where MonitoredJobId IS NULL.
///   3. Returns null when neither exists (caller falls back to dictionary).
///
/// Separate from IFixCatalogueRepository which serves suggestion generation.
/// </summary>
public interface IFixPolicyRepository
{
    Task<FixPolicyRule?> GetForAsync(
        int jobTypeId,
        int errorTypeId,
        int? monitoredJobId = null,
        CancellationToken ct = default);
}
