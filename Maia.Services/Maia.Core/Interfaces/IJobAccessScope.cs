using Maia.Core.Security;

namespace Maia.Core.Interfaces;

/// <summary>
/// Per-request answer to "which MonitoredJobs may the signed-in user see?". The
/// single source of truth for per-user job visibility: list endpoints filter with
/// <see cref="GetAsync"/>, single-item endpoints guard with <see cref="CanSeeAsync"/>
/// and answer 404 (not 403) for a job the user can't see, so existence isn't leaked.
///
/// Resolved live from the user row (not from claims), so an admin's change applies on
/// the user's next request. Cached for the lifetime of the request.
/// </summary>
public interface IJobAccessScope
{
    Task<JobVisibility> GetAsync(CancellationToken ct = default);

    /// <summary>True when the user may see <paramref name="monitoredJobId"/>. A null
    /// job (orphan failure) is visible to unrestricted users only.</summary>
    Task<bool> CanSeeAsync(int? monitoredJobId, CancellationToken ct = default);

    /// <summary>The assigned job ids, or null when the user is unrestricted. For
    /// in-memory filtering of small collections (the job list itself).</summary>
    Task<IReadOnlySet<int>?> GetVisibleJobIdsAsync(CancellationToken ct = default);
}
