namespace Maia.Core.Entities;

/// <summary>
/// Immutable audit-trail row. Covers operator actions on recommendations
/// (FailureId set, EntityType = "AiRecommendation" or "JobFailure"), config
/// changes (FailureId null, EntityType / EntityId identify the changed
/// entity), and any future system events.
///
/// EventType convention for config rows is "{EntityType}{ActionVerb}":
/// FixPolicyCreated / FixPolicyUpdated / FixPolicyDeleted,
/// MonitoredJobCreated / MonitoredJobUpdated / MonitoredJobDeleted, etc.
/// Existing values "OperatorApproved" / "OperatorRejected" stay as-is.
/// </summary>
public class AuditLog
{
    public int AuditId { get; set; }

    /// <summary>JobFailure the event relates to. Null for events that aren't
    /// failure-scoped (config changes, system actions).</summary>
    public int? FailureId { get; set; }

    /// <summary>Discriminator — entity class name (PascalCase, singular):
    /// "FixPolicyRule", "MonitoredJob", "ErrorType", "ClassificationRule",
    /// "ScanCheckRule", "AiRecommendation", "JobFailure". Null only on legacy
    /// rows that predate the column.</summary>
    public string? EntityType { get; set; }

    /// <summary>ID of the target entity as a string (so int and GUID keys
    /// fit the same column). For config rows: the FixPolicyRule.RuleId,
    /// MonitoredJob.MonitoredJobId, etc.</summary>
    public string? EntityId { get; set; }

    public required string EventType { get; set; }
    public required string Actor { get; set; }
    public string? Detail { get; set; }
    public DateTime Timestamp { get; set; }

    public JobFailure? Failure { get; set; }
}
