namespace Maia.Core.Entities;

/// <summary>
/// Registry of monitored processes. A job is a pure identity record —
/// scan config lives on its ScanSource children (Tier 2.5).
/// </summary>
public class MonitoredJob
{
    public int MonitoredJobId { get; set; }

    /// <summary>Machine-friendly unique name, e.g. "ETL_Sales_Daily".</summary>
    public required string Name { get; set; }

    public string? DisplayName { get; set; }
    public int     JobTypeId   { get; set; }

    // ── Scheduling ────────────────────────────────────────────────────────────
    public int  PollingIntervalSeconds { get; set; } = 300;
    public bool IsActive               { get; set; } = true;
    public string?   Description { get; set; }
    public DateTime  CreatedAt   { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────
    public JobType? JobType { get; set; }

    /// <summary>Typed observation points within this job (one per ScanType + config).</summary>
    public ICollection<ScanSource>       ScanSources    { get; set; } = [];

    /// <summary>What to detect during each scan — one rule per check.</summary>
    public ICollection<ScanCheckRule>    ScanCheckRules { get; set; } = [];

    /// <summary>Per-job classification rule overrides (for the classify step).</summary>
    public ICollection<MonitoredJobRule> JobRules       { get; set; } = [];

    public ICollection<JobFailure>       Failures       { get; set; } = [];

    /// <summary>Runtime coordination row (1:1). Created automatically on insert.</summary>
    public MonitoredJobLease? Lease { get; set; }
}
