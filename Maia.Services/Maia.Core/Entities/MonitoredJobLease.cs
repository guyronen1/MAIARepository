using Maia.Core.Enums;

namespace Maia.Core.Entities;

/// <summary>
/// Runtime coordination state for one MonitoredJob — 1:1 with MonitoredJobs.
/// Kept in its own table so per-tick churn doesn't bloat the config table.
/// Lease semantics:
///   • A worker claims by atomically setting LeasedBy + LeasedUntil.
///   • READPAST + UPDLOCK in the claim query lets concurrent workers skip each other's rows.
///   • If a worker crashes mid-scan, LeasedUntil eventually expires and another worker can steal.
///   • Release sets NextEligibleAt = now + MonitoredJob.PollingIntervalSeconds.
/// </summary>
public class MonitoredJobLease
{
    public int MonitoredJobId { get; set; }

    /// <summary>"host=&lt;machine&gt;;pid=&lt;pid&gt;;runId=&lt;guid&gt;" — null when not leased.</summary>
    public string?   LeasedBy           { get; set; }
    public DateTime? LeasedAt           { get; set; }

    /// <summary>Lease expiry. Null = not leased. Past = expired and stealable.</summary>
    public DateTime? LeasedUntil        { get; set; }

    /// <summary>Earliest UTC moment this job is eligible to be claimed again.</summary>
    public DateTime  NextEligibleAt     { get; set; } = DateTime.MinValue;

    public DateTime? LastRunStartedAt   { get; set; }
    public DateTime? LastRunCompletedAt { get; set; }
    public JobRunOutcome? LastRunOutcome { get; set; }
    public string?   LastRunError       { get; set; }

    public MonitoredJob? MonitoredJob { get; set; }
}
