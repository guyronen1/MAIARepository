using Maia.Core.Enums;

namespace Maia.Core.Entities;

/// <summary>
/// One check rule within a MonitoredJob scan.
/// A job can have many rules — e.g. check column A AND column B for a Database scan.
/// </summary>
public class ScanCheckRule
{
    public int CheckRuleId    { get; set; }
    public int MonitoredJobId { get; set; }

    public int ScanSourceId { get; set; }
    public ScanSource? ScanSource { get; set; }

    public CheckType CheckType { get; set; }

    /// <summary>
    /// ColumnRange/ValueEquals: the SQL table to query, e.g. "dbo.Orders".
    /// SqlQuery: repurposed to hold the operator-written query or "EXEC sp_Name @p=…"
    /// statement (run as CommandType.Text). nvarchar(max) — may be multi-line.
    /// </summary>
    public string? SourceTable { get; set; }

    /// <summary>
    /// Column name (ColumnRange), keyword phrase (ErrorKeyword),
    /// or response field/path (ResponseContains).
    /// </summary>
    public required string TargetField { get; set; }

    /// <summary>Lower bound for ColumnRange — rows below this are flagged.</summary>
    public decimal? MinValue { get; set; }

    /// <summary>Upper bound for ColumnRange — rows above this are flagged.</summary>
    public decimal? MaxValue { get; set; }

    /// <summary>Expected value for StatusCode or equality checks.</summary>
    public string? ExpectedValue { get; set; }

    /// <summary>
    /// Column used as a scan cursor for database rules (e.g. "CreatedAt", "UpdateDate").
    /// When set, each scan only reads rows newer than the last watermark value,
    /// so the same row is never reported twice. Leave null to scan all rows every time.
    /// </summary>
    public string? WatermarkColumn { get; set; }

    /// <summary>
    /// Column holding the row's unique identity (PK or unique key, e.g. "Id", "FileGuid").
    /// Stored as JobFailure.SourceId so the fix executor can act on the exact row.
    /// </summary>
    public string? SourceIdColumn { get; set; }

    /// <summary>
    /// Database scans only. Column holding a RELATED row's identity — a parent/FK key
    /// that scopes a multi-row fix (e.g. "OrderId" when fixing all line items for an
    /// order). Stored as JobFailure.ReferenceId and exposed via {referenceId} placeholder.
    ///
    /// v1 silent-null: if the configured column is absent from a SqlQuery result set,
    /// ReferenceId is silently null (TryGetValue, same as SourceIdColumn). A fix payload
    /// scoped by an empty {referenceId} produces WHERE col = '' → no rows updated → safe,
    /// not a bulk write. Operators who see "fix updates nothing" should verify the column
    /// appears in their SELECT.
    /// </summary>
    public string? ReferenceIdColumn { get; set; }

    /// <summary>
    /// Database scans only. Column on the source row that holds the input
    /// file path. Read alongside the rule's check and stuffed into
    /// JobFailure.SourceFilePath so {sourceFilePath}-using fix steps can
    /// act on the file the failing process was operating on.
    ///
    /// v1: no join logic; if the path lives on a related table the
    /// operator puts the JOIN into SourceTable directly.
    /// </summary>
    public string? FilePathColumn { get; set; }

    /// <summary>
    /// FileSystem scans only. Regex with capture group #1 = input file path
    /// extracted from the error line. Compiled with 50ms timeout. NULL =
    /// FS scan leaves JobFailure.SourceFilePath null for this rule.
    ///
    /// Distinct DSL from the wildcard-style classification patterns —
    /// full regex applies here because capture groups are required.
    /// </summary>
    public string? InputPathPattern { get; set; }

    // ── FileContent scan config (CheckType.FileContent only) ──────────────────
    // For FileContent rules the existing TargetField holds the FILENAME PATTERN
    // (same '*'-wildcard DSL as classification/FS patterns) — no separate column.
    // The five fields below describe what to pull out of each matched file and
    // how to decide it's a failure. All NULL on FS / DB / API rules.

    /// <summary>Which IFileContentExtractor parses the matched file (XML in v1).
    /// Required when CheckType=FileContent; controller rejects FileContent rules
    /// with no ExtractorType.</summary>
    public FileFormat? ExtractorType { get; set; }

    /// <summary>Format-specific address of the PRIMARY value to test — for XML,
    /// an XPath (e.g. "/file/status/code"). The extractor owns the meaning of
    /// this string. NULL = filename match alone is the signal (no value tested).</summary>
    public string? ExtractorLocator { get; set; }

    /// <summary>Format-specific address of the natural key used as
    /// JobFailure.SourceId — for XML, an XPath (e.g. "/file/header/invoiceId").
    /// NULL = fall back to filename-without-extension. Extraction failure on a
    /// set locator also falls back to filename and increments the
    /// ScanRunHistory.IdentifierExtractionFailures counter.</summary>
    public string? IdentifierLocator { get; set; }

    /// <summary>Comparison applied to the extracted primary value. NULL = no
    /// predicate (filename match alone fails). Must be set together with
    /// ExtractorPredicateValue (controller rejects one-set-one-null for
    /// FileContent rules).</summary>
    public ScanPredicateType? ExtractorPredicateType { get; set; }

    /// <summary>Right-hand operand for ExtractorPredicateType. Case-insensitive
    /// for Equals/Contains. NULL when no predicate is configured.</summary>
    public string? ExtractorPredicateValue { get; set; }

    public Severity Severity    { get; set; } = Severity.Medium;
    public string?  Description { get; set; }
    public bool     IsActive    { get; set; } = true;

    public MonitoredJob? MonitoredJob { get; set; }
}
