using Maia.Core.Enums;

namespace Maia.Core.Entities;

/// <summary>
/// One ADDITIONAL check AND'd onto its parent <see cref="ScanCheckRule"/>. The parent's
/// own check is condition #0; a failure fires only when the parent AND every condition
/// hold on the same row (DB) — Phase 2 extends the same shape to the same file
/// (FileContent locators) and the same log line (FS keywords). OR is expressed by
/// separate rules. A rule with zero conditions behaves exactly as before.
///
/// Kind-generic by design: <see cref="Kind"/> reuses <see cref="CheckType"/> and
/// <see cref="PredicateType"/> is reserved for FileContent conditions, so Phase 2 adds
/// no migration. Phase 1 (DB table rules) accepts Kind ∈ {ValueEquals, ColumnRange}.
/// </summary>
public class ScanCheckRuleCondition
{
    public int ConditionId { get; set; }
    public int CheckRuleId { get; set; }

    /// <summary>1-based display/evaluation order within the rule. The controller
    /// re-packs to 1..N on every save (unique per rule).</summary>
    public int ConditionOrder { get; set; }

    /// <summary>What this condition tests. Phase 1: ValueEquals | ColumnRange (DB column
    /// predicates). Reserved for Phase 2: FileContent (locator + predicate), ErrorKeyword.</summary>
    public CheckType Kind { get; set; }

    /// <summary>Column name (DB). Phase 2: XPath locator (FileContent) or keyword (FS).</summary>
    public required string TargetField { get; set; }

    /// <summary>ValueEquals: the value that flags the row. (Phase 2 FileContent: predicate RHS.)</summary>
    public string? ExpectedValue { get; set; }

    /// <summary>ColumnRange: rows below this are flagged.</summary>
    public decimal? MinValue { get; set; }

    /// <summary>ColumnRange: rows above this are flagged.</summary>
    public decimal? MaxValue { get; set; }

    /// <summary>Reserved for Phase 2 FileContent conditions. Always null in Phase 1.</summary>
    public ScanPredicateType? PredicateType { get; set; }

    public string? Description { get; set; }

    public ScanCheckRule? CheckRule { get; set; }
}
