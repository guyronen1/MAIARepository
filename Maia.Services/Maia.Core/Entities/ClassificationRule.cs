namespace Maia.Core.Entities;

public class ClassificationRule
{
    public int RuleId { get; set; }
    public int JobTypeId { get; set; }
    public int ErrorTypeId { get; set; }
    public required string Pattern { get; set; }
    public decimal Confidence { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }

    // ── Suggestion provenance (v2-readiness) ─────────────────────────────────
    // Populated when this rule was accepted from an /unconfigured suggestion;
    // null for manually-created rules. The training signal for future ML/LLM
    // analyzers — lets v2 match operator decisions back to the cluster that
    // produced the suggestion.
    //   SuggestedBy         — analyzer version ("ngram-v1" / "embedding-v1" / …)
    //   SuggestedFromHash   — SHA-256 (first 16 hex) of the cluster's sorted
    //                         sample failure ids at suggestion time
    //   SuggestedConfidence — analyzer confidence (null in v1; ngram doesn't score)
    public string?  SuggestedBy { get; set; }
    public string?  SuggestedFromHash { get; set; }
    public decimal? SuggestedConfidence { get; set; }

    public JobType? JobType { get; set; }
    public ErrorType? ErrorType { get; set; }
    public ICollection<MonitoredJobRule> MonitoredJobRules { get; set; } = [];
}
