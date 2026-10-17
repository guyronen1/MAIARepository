using Maia.Core.Interfaces;
using Maia.Core.Security;

namespace Maia.Tests;

/// <summary>
/// Fixed <see cref="IJobAccessScope"/> for controller tests. <see cref="All"/> = an
/// unrestricted caller (the pre-job-access behaviour). A restricted instance reports
/// <paramref name="visible"/> as the assigned set for <paramref name="userId"/>; SQL-side
/// filtering then needs matching <c>UserMonitoredJobs</c> rows in the test database.
/// </summary>
internal sealed class TestJobAccessScope(IReadOnlySet<int>? visible, int userId = 42) : IJobAccessScope
{
    public static TestJobAccessScope All => new(null);

    public Task<JobVisibility> GetAsync(CancellationToken ct = default)
        => Task.FromResult(visible is null ? JobVisibility.All : JobVisibility.AssignedTo(userId));

    public Task<bool> CanSeeAsync(int? monitoredJobId, CancellationToken ct = default)
        => Task.FromResult(visible is null || (monitoredJobId is int id && visible.Contains(id)));

    public Task<IReadOnlySet<int>?> GetVisibleJobIdsAsync(CancellationToken ct = default)
        => Task.FromResult(visible);
}
