namespace Maia.Core.Enums;

/// <summary>
/// Comparison applied to a value extracted from a file's contents during a
/// FileContent scan. NULL on the rule (no predicate) means "filename match
/// alone is the failure" — every matched file produces a failure regardless of
/// the extracted value. When set, the extracted primary value must satisfy
/// (type, ExtractorPredicateValue) for the file to produce a failure.
/// Equals / Contains comparisons are case-insensitive. Regex is a v2 addition.
/// </summary>
public enum ScanPredicateType
{
    Equals      = 0,
    NotEquals   = 1,
    Contains    = 2,
    NotContains = 3
}
