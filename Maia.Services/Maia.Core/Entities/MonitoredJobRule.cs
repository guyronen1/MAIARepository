namespace Maia.Core.Entities;

/// <summary>
/// Binds a specific ClassificationRule to a MonitoredJob,
/// enabling per-job rule overrides instead of relying purely on JobType-level rules.
/// </summary>
public class MonitoredJobRule
{
    public int JobRuleId { get; set; }
    public int MonitoredJobId { get; set; }
    public int RuleId { get; set; }
    public bool IsActive { get; set; } = true;

    public MonitoredJob? MonitoredJob { get; set; }
    public ClassificationRule? Rule { get; set; }
}
