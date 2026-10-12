namespace Maia.API.Controllers;

// ── Config request contracts ───────────────────────────────────────────────────
//
// Shared by the per-entity config controllers (split out of the former
// ConfigController). The audit actor is the authenticated principal (server-side),
// never a client value, so these carry NO operatorId. Authorization guarantees an
// authenticated user reaches any write.

public sealed record UpsertMonitoredJobRequest(
    string  Name,
    string? DisplayName,
    int     JobTypeId,
    int     PollingIntervalSeconds,
    bool    IsActive,
    string? Description);

public sealed record UpsertScanSourceRequest(
    string  Name,
    int     ScanTypeId,
    string? LogFolder         = null,
    string? SearchPatterns    = null,
    string? InputFolder       = null,
    bool    IncludeSubfolders = false,
    string? ConnectionName    = null,
    string? LogSourceUrl      = null,
    bool    IsActive          = true,
    /// <summary>FileSystem / FileContent sources: seed the file
    /// watermarks at each existing file's current position so the first scan only
    /// picks up data appended/modified after save. Default false keeps today's
    /// full-history first scan for callers that don't send it (the UI sends true).</summary>
    bool    SeedWatermarkFromNow = false);

public sealed record UpsertScanCheckRuleRequest(
    string   CheckType,
    string?  SourceTable,
    string   TargetField,
    decimal? MinValue,
    decimal? MaxValue,
    string?  ExpectedValue,
    string?  WatermarkColumn,
    string?  SourceIdColumn,
    string   Severity,
    string?  Description,
    bool     IsActive = true,
    /// <summary>DB scans only — column holding a related row's identity (parent/FK key)
    /// for multi-row child updates. Stored as JobFailure.ReferenceId; exposed via
    /// {referenceId} placeholder. Null = not configured.</summary>
    string?  ReferenceIdColumn = null,
    /// <summary>DB scans only — column on the source row that holds the
    /// input file path. Read into JobFailure.SourceFilePath when matched.</summary>
    string?  FilePathColumn   = null,
    /// <summary>FS scans only — regex with capture group #1 = input file path
    /// extracted from the matching error line. Null = no extraction.</summary>
    string?  InputPathPattern = null,
    /// <summary>DB scans only — comma-separated source-row columns appended to the
    /// failure's ErrorMessage as "Column: value" lines (e.g. "UpdateDate, Details").
    /// SqlQuery rules must include them in the SELECT. Null = none.</summary>
    string?  DetailColumns    = null,
    // ── FileContent scans only (CheckType=FileContent) ──────────────────────────
    /// <summary>Extractor/format name, e.g. "Xml". Required for FileContent rules.</summary>
    string?  ExtractorType           = null,
    /// <summary>Format-specific address of the value to test (XPath for XML).
    /// Null = filename match alone is the failure signal.</summary>
    string?  ExtractorLocator        = null,
    /// <summary>Format-specific address of the natural key for SourceId (XPath
    /// for XML). Null = fall back to filename without extension.</summary>
    string?  IdentifierLocator       = null,
    /// <summary>Predicate over the extracted value: Equals/NotEquals/Contains/
    /// NotContains. Null = no predicate (filename match fires unconditionally).</summary>
    string?  ExtractorPredicateType  = null,
    /// <summary>Right-hand operand for the predicate. Required with a predicate type.</summary>
    string?  ExtractorPredicateValue = null,
    /// <summary>DB rules with a WatermarkColumn: seed the watermark at
    /// the current MAX so the first scan only reports rows that arrive after save.
    /// Default false keeps today's full-history first scan for callers that don't
    /// send it (the UI sends true). Ignored for rule kinds without a watermark.</summary>
    bool     SeedWatermarkFromNow    = false,
    /// <summary>Composite scan rule: extra checks AND'd onto this rule's own check,
    /// all on the SAME source row. Phase 1 accepts them on Database ValueEquals /
    /// ColumnRange rules only (400 ConditionsNotSupported otherwise). Replace-all on
    /// PUT — null/empty clears existing conditions. Orders are re-packed to 1..N.</summary>
    IReadOnlyList<ScanRuleConditionDto>? Conditions = null);

/// <summary>One extra AND'd condition of a composite scan rule (write shape — no id;
/// conditions are replaced wholesale on every save).</summary>
public sealed record ScanRuleConditionDto(
    int      ConditionOrder,
    /// <summary>ValueEquals | ColumnRange (Phase 1). Reserved: FileContent, ErrorKeyword.</summary>
    string   Kind,
    /// <summary>Column name on the source row (Phase 2: XPath / keyword).</summary>
    string   TargetField,
    /// <summary>ValueEquals: the value that flags the row.</summary>
    string?  ExpectedValue = null,
    /// <summary>ColumnRange: rows below this are flagged.</summary>
    decimal? MinValue      = null,
    /// <summary>ColumnRange: rows above this are flagged.</summary>
    decimal? MaxValue      = null,
    string?  Description   = null);

public sealed record UpsertClassificationRuleRequest(
    int     JobTypeId,
    int     ErrorTypeId,
    string  Pattern,
    decimal Confidence,
    int     Priority,
    bool    IsActive = true,
    // Suggestion provenance — set only when accepted from an /unconfigured
    // cluster; null for manual creation. Applied on CREATE only (ignored on update).
    string?  SuggestedBy = null,
    string?  SuggestedFromHash = null,
    decimal? SuggestedConfidence = null);

public sealed record UpsertJobClassificationRuleRequest(
    int     ErrorTypeId,
    string  Pattern,
    decimal Confidence,
    int     Priority,
    bool    IsActive = true);

public sealed record UpsertFixPolicyRuleRequest(
    int     JobTypeId,
    int     ErrorTypeId,
    string  ActionToApply,
    string  FixCategory,
    string  ActionType,
    string? ActionPayload,
    bool    IsAutoHealEligible,
    bool    Enabled,
    /// <summary>NULL = JobType-level default (applies to all jobs of JobTypeId).
    /// Set = MonitoredJob-scoped override that wins over the default for this one job.</summary>
    int?    MonitoredJobId = null,
    /// <summary>Ordered steps for Composite policies. Required when
    /// ActionType=Composite; must be null/empty otherwise. Controller normalises
    /// StepOrder to 1..N (gaps allowed in input).</summary>
    IReadOnlyList<FixPolicyStepDto>? Steps = null,
    // Suggestion provenance — set only when created in response to an
    // /unconfigured Case-B gap; null for manual creation. Applied on CREATE only.
    string?  SuggestedBy = null,
    string?  SuggestedFromHash = null,
    decimal? SuggestedConfidence = null);

public sealed record FixPolicyStepDto(
    int     StepOrder,
    string  ActionType,
    string  ActionPayload,
    string? Description);

public sealed record UpsertErrorTypeRequest(
    string  Code,
    string  DisplayName,
    string? Description,
    string  Severity,
    bool    IsActive = true);
