namespace Maia.Core.Entities;

public class JobType
{
    public int JobTypeId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<JobFailure> JobFailures { get; set; } = [];
    public ICollection<ClassificationRule> ClassificationRules { get; set; } = [];
    public ICollection<FixPolicyRule> FixPolicyRules { get; set; } = [];
    public ICollection<MonitoredJob> MonitoredJobs { get; set; } = [];
}
