using Maia.Core.Enums;

namespace Maia.Core.Entities;

public class ErrorType
{
    public int ErrorTypeId { get; set; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public Severity Severity { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<JobFailure> JobFailures { get; set; } = [];
    public ICollection<ClassificationRule> ClassificationRules { get; set; } = [];
    public ICollection<FixPolicyRule> FixPolicyRules { get; set; } = [];
    public ICollection<AiRecommendation> Recommendations { get; set; } = [];
}
