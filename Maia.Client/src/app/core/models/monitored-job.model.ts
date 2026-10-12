export interface ScanCheckRule {
  checkRuleId:      number;
  checkType:        string;
  sourceTable:      string | null;
  targetField:      string;
  minValue:         number | null;
  maxValue:         number | null;
  expectedValue:    string | null;
  watermarkColumn:    string | null;
  sourceIdColumn:     string | null;
  /** DB scans: column holding a related row's identity (parent/FK key) for
   *  multi-row child updates. Stored as JobFailure.ReferenceId, exposed via
   *  {referenceId} placeholder. Null = not configured. */
  referenceIdColumn:  string | null;
  /** DB scans: column on the source row that holds the input file path.
   *  Captured into JobFailure.SourceFilePath when this rule matches. */
  filePathColumn:     string | null;
  /** DB scans: comma-separated source-row columns whose values are appended
   *  to the failure's error message as "Column: value" lines (e.g.
   *  "UpdateDate, Details"). SqlQuery rules must SELECT them. Null = none. */
  detailColumns:      string | null;
  /** FS scans: regex with capture group #1 = input file path extracted
   *  from the matching error line. Differs from classification patterns:
   *  full regex applies here (capture groups required). */
  inputPathPattern: string | null;
  /** FileContent scans: extractor/format name (e.g. "Xml"). Null on other types. */
  extractorType:           string | null;
  /** FileContent scans: address of the value to test (XPath for XML). Null =
   *  filename match alone is the failure signal. */
  extractorLocator:        string | null;
  /** FileContent scans: address of the natural key for SourceId (XPath for XML).
   *  Null = fall back to filename without extension. */
  identifierLocator:       string | null;
  /** FileContent scans: Equals | NotEquals | Contains | NotContains. Null = no
   *  predicate (filename match fires unconditionally). */
  extractorPredicateType:  string | null;
  /** FileContent scans: right-hand operand for the predicate. */
  extractorPredicateValue: string | null;
  severity:         string;
  description:      string | null;
  /** Composite scan rule: extra checks AND'd onto this rule's own check, all on
   *  the SAME source row (Phase 1: DB ValueEquals / ColumnRange rules only).
   *  Empty for a plain single-check rule. Ordered by conditionOrder. */
  conditions:       ScanCheckRuleCondition[];
}

/** One extra AND'd condition of a composite scan rule. `conditionId` churns on
 *  every save (replace-all persistence) — never key on it. */
export interface ScanCheckRuleCondition {
  conditionId:    number;
  conditionOrder: number;
  /** ValueEquals | ColumnRange (Phase 1). Reserved: FileContent, ErrorKeyword. */
  kind:           string;
  /** Column name on the source row (Phase 2: XPath locator / keyword). */
  targetField:    string;
  /** ValueEquals: the value that flags the row. */
  expectedValue:  string | null;
  /** ColumnRange: rows below this are flagged. */
  minValue:       number | null;
  /** ColumnRange: rows above this are flagged. */
  maxValue:       number | null;
  /** Reserved for Phase 2 FileContent conditions; always null today. */
  predicateType:  string | null;
  description:    string | null;
}

/** Tier 2.5: a typed observation point within a job (its own ScanType + config),
 *  carrying its own scan rules. Active sources only on the wire. */
export interface ScanSource {
  scanSourceId:      number;
  monitoredJobId:    number;
  name:              string;
  scanTypeId:        number;
  scanTypeName:      string;
  logFolder:         string | null;
  searchPatterns:    string | null;
  inputFolder:       string | null;
  includeSubfolders: boolean;
  connectionName:    string | null;
  logSourceUrl:      string | null;
  isActive:          boolean;
  scanCheckRules:    ScanCheckRule[];
}

export interface RuleOverride {
  ruleId:        number;
  pattern:       string;
  errorTypeCode: string;
  confidence:    number;
  priority:      number;
}

import type { MonitoredJobLease } from './worker-status.model';

export interface MonitoredJob {
  monitoredJobId:          number;
  name:                    string;
  displayName:             string | null;
  jobTypeName:             string;
  pollingIntervalSeconds:  number;
  isActive:                boolean;
  description:             string | null;
  createdAt:               string;
  scanCheckRules:          ScanCheckRule[];
  rules:                   RuleOverride[];
  /** Tier 2.5: active scan sources, each nesting its own rules. */
  sources:                 ScanSource[];
  /** Runtime lease snapshot — null when the job has never been claimed. */
  lease:                   MonitoredJobLease | null;
}
