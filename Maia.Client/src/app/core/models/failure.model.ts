import { Recommendation } from './recommendation.model';

export type FailureStage = 'Failed' | 'Classified' | 'Recommended' | 'Acknowledged' | 'Manual' | 'Fixed';
export type JobStatus    = 'Failed' | 'Resolved' | 'ManualRequired' | 'AwaitingManualAction';

export interface JobFailure {
  failureId:        number;
  jobId:            number;
  jobTypeName:      string;
  sourceId:         string | null;
  stepName:         string | null;
  errorMessage:     string | null;
  detectedAt:       string;
  status:           JobStatus;
  errorTypeCode:    string | null;
  /** Severity of the matched error type (Critical/High/Medium/Low), or null
   *  when unclassified. Drives the leading severity signal + default sort. */
  severity:         string | null;
  monitoredJobName: string | null;
  /** True when this failure has a Success=false FixExecutionLog row since
   *  today-midnight. Drives the "Failed to Execute" marker in the failures
   *  list, independent of the active view filter. Backend computes this
   *  via a batched lookup after paging. */
  hasRecentFixFailure: boolean;
}

/** One fix-execution attempt. For composite policies there's one row per
 *  step (executedBy ends in '.Composite') plus a summary row; single-action
 *  fixes have just the summary row. */
export interface FixExecution {
  fixId:            number;
  recommendationId: number;
  executedAction:   string;
  executedBy:       string;
  success:          boolean;
  resultDetail:     string | null;
  executedAt:       string;
  triggerType:      string;
}

export interface FailureStatus {
  failureId:        number;
  sourceId:         string | null;
  /** Input file path captured at scan time (FS InputPathPattern / DB
   *  FilePathColumn). What {sourceFilePath} resolves to for this failure. */
  sourceFilePath:   string | null;
  stepName:         string | null;
  errorMessage:     string | null;
  detectedAt:       string;
  status:           JobStatus;
  stage:            FailureStage;
  errorTypeCode:    string | null;
  monitoredJobName: string | null;
  /** The detecting source's ScanType (FileSystem / Database / ApiEndpoint /
   *  FileContent); null when the source is gone. Gates "View Source". */
  scanType:         string | null;
  recommendations:  Recommendation[];
  /** Fix-execution history, chronological. Empty until a fix has run. */
  executions:       FixExecution[];
}

/** One column of a source row; `value` null = SQL NULL. */
export interface SourceColumn {
  name:      string;
  value:     string | null;
  truncated: boolean;
}

/**
 * GET /data/failures/{id}/source — the LIVE source data behind a failure
 * (may have changed since detection). `kind` picks which fields are set.
 */
export interface SourceData {
  kind:     'DbRows' | 'File' | 'Unavailable';
  scanType: string;
  /** Unavailable: why. DbRows: optional note (key not unique). */
  reason:   string | null;

  connectionName:   string | null;
  provider:         string | null;
  /** Table name, or "query" for a SqlQuery rule. */
  table:            string | null;
  keyColumn:        string | null;
  keyValue:         string | null;
  rows:             SourceColumn[][];
  highlightColumns: string[];

  path:            string | null;
  sizeBytes:       number | null;
  lastModified:    string | null;
  content:         string | null;
  /** True when `content` is a window of a larger file. */
  truncated:       boolean;
  /** 1-based file line number of the first line of `content`. */
  windowStartLine: number;
  /** 1-based file line numbers of the matched text. */
  matchLines:      number[];
  format:          'xml' | 'text' | null;
}

export interface PagedResult<T> {
  totalCount: number;
  totalPages: number;
  page:       number;
  pageSize:   number;
  items:      T[];
}
