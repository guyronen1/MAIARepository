using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Security;

namespace Maia.Application.Security;

/// <summary>
/// <see cref="IJobAccessScope"/> over the authenticated principal. Administrators and
/// users without <c>RestrictToAssignedJobs</c> are unrestricted; everyone else sees
/// only their <c>UserMonitoredJobs</c> rows. Anonymous / unknown users see nothing.
/// Registered scoped, so the user row and assignment set load at most once per request.
/// </summary>
public sealed class JobAccessScope(ICurrentUserAccessor currentUser, IUserRepository users) : IJobAccessScope
{
    private JobVisibility? _visibility;
    private IReadOnlySet<int>? _assigned;

    public async Task<JobVisibility> GetAsync(CancellationToken ct = default)
    {
        if (_visibility is not null) return _visibility;

        if (!currentUser.IsAuthenticated || currentUser.UserId is not int userId)
            return _visibility = JobVisibility.None;

        // Role comes from the claims the session handler rebuilds on every request.
        if (currentUser.Role == MaiaRole.Administrator)
            return _visibility = JobVisibility.All;

        var user = await users.GetByIdAsync(userId, ct);
        _visibility = user is null                 ? JobVisibility.None
                    : !user.RestrictToAssignedJobs ? JobVisibility.All
                    :                                JobVisibility.AssignedTo(userId);
        return _visibility;
    }

    public async Task<bool> CanSeeAsync(int? monitoredJobId, CancellationToken ct = default)
    {
        var ids = await GetVisibleJobIdsAsync(ct);
        if (ids is null) return true;                       // unrestricted (orphans included)
        return monitoredJobId is int id && ids.Contains(id);
    }

    public async Task<IReadOnlySet<int>?> GetVisibleJobIdsAsync(CancellationToken ct = default)
    {
        var vis = await GetAsync(ct);
        if (vis.Unrestricted) return null;
        if (vis.UserId == 0) return new HashSet<int>();
        return _assigned ??= await users.GetAssignedJobIdsAsync(vis.UserId, ct);
    }
}
