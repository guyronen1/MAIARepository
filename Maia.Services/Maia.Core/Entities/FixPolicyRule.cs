using Maia.Core.Enums;

namespace Maia.Core.Entities;

public class FixPolicyRule
{
    public int RuleId { get; set; }
    public int JobTypeId { get; set; }
    public int ErrorTypeId { get; set; }

    /// <summary>
    /// Optional override scope. When NULL, the rule is a JobType-level default
    /// that applies to every MonitoredJob of <see cref="JobTypeId"/>. When set,
    /// the rule is a per-MonitoredJob override that takes precedence over the
    /// default for that specific job.
    ///
    /// Lookup priority (see SqlFixPolicyRepository.GetForAsync):
    ///   1. Override matching (MonitoredJobId, ErrorTypeId, Enabled=1)
    ///   2. Default  matching (JobTypeId, ErrorTypeId, Enabled=1, MonitoredJobId IS NULL)
    ///   3. DbFixCatalogue dictionary fallback (unchanged)
    /// </summary>
    public int? MonitoredJobId { get; set; }

    /// <summary>Human-readable description of the fix (shown to operators).</summary>
    public required string ActionToApply { get; set; }

    public FixCategory FixCategory { get; set; }
    public bool IsAutoHealEligible { get; set; }
    public bool Enabled { get; set; } = true;
    public string? CreatedBy { get; set; }
    public DateTime ActionTimestamp { get; set; }

    // ── Suggestion provenance (v2-readiness) ─────────────────────────────────
    // Populated when this policy was created in response to an /unconfigured
    // Case-B (missing-policy) gap; null for manually-created policies. Same
    // semantics + shape as ClassificationRule's provenance fields.
    public string?  SuggestedBy { get; set; }
    public string?  SuggestedFromHash { get; set; }
    public decimal? SuggestedConfidence { get; set; }

    // ── Execution wiring ─────────────────────────────────────────────────────

    /// <summary>How to execute the fix automatically.</summary>
    public FixActionType ActionType { get; set; } = FixActionType.Manual;

    /// <summary>
    /// The action target, interpreted by the executor for this ActionType:
    /// - ApiCall:         URL (e.g. http://jobs.internal/api/retry/{failureId})
    /// - StoredProcedure: "SpName" or "ConnectionName|SpName"
    /// - Script:          executable + args (e.g. powershell.exe C:\scripts\fix.ps1 {failureId})
    /// - Manual:          null (not used)
    /// Supports {failureId} placeholder substitution at runtime.
    /// </summary>
    public string? ActionPayload { get; set; }

    public JobType?      JobType        { get; set; }
    public ErrorType?    ErrorType      { get; set; }
    public MonitoredJob? MonitoredJob   { get; set; }

    /// <summary>Ordered steps for Composite rules. Empty for single-action rules.
    /// Eager-loaded with OrderBy(StepOrder) by SqlFixPolicyRepository.</summary>
    public ICollection<FixPolicyRuleStep> Steps { get; set; } = [];
}
