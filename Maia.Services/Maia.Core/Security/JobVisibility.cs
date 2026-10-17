namespace Maia.Core.Security;

/// <summary>
/// Which MonitoredJobs the current request may see. <see cref="Unrestricted"/> covers
/// Administrators and users with "All jobs"; otherwise only the jobs assigned to
/// <see cref="UserId"/> in <c>UserMonitoredJobs</c> are visible, and failures with no
/// job are hidden. Repository/query helpers treat a <c>null</c> visibility as
/// unrestricted, so background callers (the worker) never pass one.
/// </summary>
public sealed record JobVisibility(bool Unrestricted, int UserId)
{
    public static readonly JobVisibility All = new(true, 0);

    /// <summary>Restricted to <paramref name="userId"/>'s assigned jobs.</summary>
    public static JobVisibility AssignedTo(int userId) => new(false, userId);

    /// <summary>Sees nothing (anonymous / unknown user). No user has id 0.</summary>
    public static readonly JobVisibility None = new(false, 0);
}
