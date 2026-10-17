namespace Maia.Core.Entities;

/// <summary>
/// MAIA-local user account. Authentication is local in v1 (AD swap is v2 and
/// changes only how identity is validated — this record and the role table stay).
/// <see cref="PasswordHash"/> holds ASP.NET Core Identity's versioned PBKDF2 format
/// (algorithm + salt + iteration count embedded). Soft-disable via
/// <see cref="IsActive"/>, mirroring the codebase's soft-delete convention.
/// </summary>
public class User
{
    public int UserId { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public int RoleId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Forces a password rotation at next login before any other action
    /// is permitted. Set on the seeded bootstrap admin and on admin-initiated resets.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>When true, a non-admin user sees and acts on only the jobs in
    /// <see cref="JobAccess"/>. False (the default, and the backfill for users that
    /// pre-date job assignment) = every job, the "All jobs" switch in the UI.
    /// Stored inverted so the column's DB default is the CLR default (false).</summary>
    public bool RestrictToAssignedJobs { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public Role? Role { get; set; }
    public ICollection<UserSession> Sessions { get; set; } = [];
    public ICollection<UserMonitoredJob> JobAccess { get; set; } = [];
}
