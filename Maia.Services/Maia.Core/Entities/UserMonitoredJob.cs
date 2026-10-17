namespace Maia.Core.Entities;

/// <summary>
/// One MonitoredJob a user may see and act on. Only consulted when the user's
/// <see cref="User.RestrictToAssignedJobs"/> is set; Administrators and unrestricted
/// users see every job regardless of these rows. Both FKs cascade, so deleting a
/// user or a job removes its assignments.
/// </summary>
public class UserMonitoredJob
{
    public int UserId { get; set; }
    public int MonitoredJobId { get; set; }

    public User? User { get; set; }
    public MonitoredJob? MonitoredJob { get; set; }
}
