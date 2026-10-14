import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { MonitoredJob } from '../models';

export interface JobType    { jobTypeId: number; name: string; description: string; }
/** Engine behind a connection. 'Unknown' = an unrecognised `{Name}_ProviderName` (a source save is a 400). */
export type DbProvider = 'SqlServer' | 'Oracle' | 'Unknown';
export interface ConnectionInfo { name: string; provider: DbProvider; }
export interface ErrorType  {
  errorTypeId: number; code: string; displayName: string;
  description?: string | null; severity: string; isActive?: boolean;
}

export interface UpsertErrorTypeRequest {
  code: string; displayName: string; description: string | null;
  severity: string; isActive: boolean;
}

export interface ClassificationRule {
  ruleId: number; jobTypeId: number; jobTypeName: string;
  errorTypeId: number; errorTypeCode: string;
  pattern: string; confidence: number; priority: number; isActive: boolean; createdBy: string | null;
  /** Names of MonitoredJobs this rule is actively linked to. Empty = JobType
   *  default (applies to all jobs of jobTypeName); non-empty = scoped to those jobs. */
  linkedJobNames: string[];
}

export interface FixPolicyRuleStep {
  stepId:        number;
  stepOrder:     number;
  actionType:    string;
  actionPayload: string;
  description:   string | null;
}

export interface FixPolicyRule {
  ruleId: number;
  jobTypeId: number;
  errorTypeId: number;
  /** NULL = JobType-level default; set = override scoped to that MonitoredJob.
   *  Override wins over default for the same (JobType, ErrorType) when evaluating
   *  recommendations for a failure on the scoped job. */
  monitoredJobId: number | null;
  errorTypeCode: string;
  actionToApply: string;
  fixCategory: string;
  actionType: string;
  /** Null for Composite rules — payload lives on each step instead. */
  actionPayload: string | null;
  isAutoHealEligible: boolean;
  enabled: boolean;
  /** Empty array for non-composite rules. Ordered by StepOrder ascending. */
  steps: FixPolicyRuleStep[];
}

export interface UpsertJobRequest {
  name: string; displayName: string | null; jobTypeId: number;
  pollingIntervalSeconds: number; isActive: boolean; description: string | null;
}
export interface UpsertScanSourceRequest {
  name: string; scanTypeId: number;
  logFolder: string | null; searchPatterns: string | null; inputFolder: string | null;
  includeSubfolders: boolean;
  connectionName: string | null; logSourceUrl: string | null;
  isActive: boolean;
  /** FileSystem / FileContent: seed file watermarks at the current
   *  position so the first scan skips existing content. UI default = true;
   *  the backend treats a missing flag as false (full-history first scan). */
  seedWatermarkFromNow?: boolean;
}
/** Create responses carry non-blocking warnings (e.g. the watermark seed could
 *  not run) — the entity IS saved; surface them, don't treat them as errors. */
export interface CreateScanSourceResponse { scanSourceId: number; warnings?: string[] | null; }
export interface CreateScanRuleResponse   { checkRuleId: number;  warnings?: string[] | null; }
/** Server-side folder browser (GET /config/browse-folders). `path` null = roots view;
 *  `parent` null = Up leads back to the roots. Directories only. */
export interface FolderEntry   { name: string; fullPath: string; }
export interface FolderListing {
  path: string | null; parent: string | null;
  folders: FolderEntry[]; roots: FolderEntry[]; truncated: boolean;
}
export interface UpsertScanRuleRequest {
  checkType: string; sourceTable: string | null; targetField: string;
  minValue: number | null; maxValue: number | null; expectedValue: string | null;
  watermarkColumn: string | null; sourceIdColumn: string | null;
  /** DB scans only — column holding a related row's identity (parent/FK key).
   *  Stored as JobFailure.ReferenceId; exposed via {referenceId} placeholder. */
  referenceIdColumn: string | null;
  severity: string; description: string | null; isActive: boolean;
  /** DB scans only — column on the source row holding the input file path. */
  filePathColumn:   string | null;
  /** DB scans only — comma-separated columns appended to the failure's error
   *  message as "Column: value" lines. SqlQuery rules must SELECT them. */
  detailColumns:    string | null;
  /** FS scans only — regex (capture group #1 = input file path). */
  inputPathPattern: string | null;
  // ── FileContent scans only ──
  /** Extractor/format name, e.g. "Xml". Required for FileContent rules. */
  extractorType:           string | null;
  /** Address of the value to test (XPath for XML). Null = filename-only failure. */
  extractorLocator:        string | null;
  /** Address of the natural key for SourceId. Null = filename-without-ext. */
  identifierLocator:       string | null;
  /** Equals | NotEquals | Contains | NotContains. Null = no predicate. */
  extractorPredicateType:  string | null;
  /** Right-hand operand for the predicate. */
  extractorPredicateValue: string | null;
  /** DB rules with a watermarkColumn (or any file-based rule, which seeds its
   *  source's file positions): seed the watermark at the
   *  current MAX so the first scan only reports rows that arrive after save.
   *  UI default = true; backend treats a missing flag as false. */
  seedWatermarkFromNow?: boolean;
  /** Composite scan rule — extra checks AND'd onto the rule's own check, on the
   *  SAME source row. Phase 1: Database ValueEquals / ColumnRange rules only
   *  (400 ConditionsNotSupported otherwise). Replace-all on PUT; omitted/empty
   *  clears existing conditions. Orders are re-packed to 1..N server-side. */
  conditions?: ScanRuleConditionDto[];
}
/** Write shape of one composite-scan-rule condition (no id — replaced wholesale). */
export interface ScanRuleConditionDto {
  conditionOrder: number;
  /** 'ValueEquals' | 'ColumnRange' (Phase 1). */
  kind:           string;
  /** Column on the source row. */
  targetField:    string;
  expectedValue:  string | null;
  minValue:       number | null;
  maxValue:       number | null;
  description:    string | null;
}
export interface UpsertClassificationRuleRequest {
  jobTypeId: number; errorTypeId: number; pattern: string;
  confidence: number; priority: number; isActive: boolean;
  // Suggestion provenance — set when accepted from an /unconfigured cluster.
  suggestedBy?: string | null;
  suggestedFromHash?: string | null;
  suggestedConfidence?: number | null;
}
export interface UpsertJobClassificationRuleRequest {
  errorTypeId: number; pattern: string;
  confidence: number; priority: number; isActive: boolean;
}
export interface FixPolicyStepDto {
  stepOrder:     number;
  actionType:    string;
  actionPayload: string;
  description:   string | null;
}

export interface UpsertFixPolicyRuleRequest {
  jobTypeId: number; errorTypeId: number;
  /** Optional override scope. NULL → JobType-level default (default behavior). */
  monitoredJobId: number | null;
  actionToApply: string; fixCategory: string; actionType: string;
  /** Must be null when actionType === 'Composite'. Backend rejects otherwise. */
  actionPayload: string | null; isAutoHealEligible: boolean; enabled: boolean;
  /** Required when actionType === 'Composite'. Must be null/empty otherwise.
   *  Backend normalises StepOrder to 1..N before persist, so gaps are fine. */
  steps?: FixPolicyStepDto[];
}

@Injectable({ providedIn: 'root' })
export class ConfigService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/config`;

  // Identity is server-authoritative: the audit actor comes from the session
  // cookie on the backend, so config writes carry no operatorId.

  // ── Lookups ────────────────────────────────────────────────────────────────
  getJobTypes():   Observable<JobType[]>   { return this.http.get<JobType[]>(`${this.base}/job-types`); }
  /** ConnectionStrings keys from the API's appsettings with each one's engine
   *  (`{Name}_ProviderName`; names only, never values). Admin. */
  getConnections(): Observable<ConnectionInfo[]> { return this.http.get<ConnectionInfo[]>(`${this.base}/connection-names`); }
  /** Sub-folders of `path` on the MAIA server; omit `path` for the roots. Admin. */
  browseFolders(path?: string | null): Observable<FolderListing> {
    const params = path ? new HttpParams().set('path', path) : undefined;
    return this.http.get<FolderListing>(`${this.base}/browse-folders`, { params });
  }
  getErrorTypes(includeInactive = false): Observable<ErrorType[]> {
    const params = includeInactive ? new HttpParams().set('includeInactive', 'true') : undefined;
    return this.http.get<ErrorType[]>(`${this.base}/error-types`, { params });
  }
  createErrorType(req: UpsertErrorTypeRequest): Observable<{ errorTypeId: number }> {
    return this.http.post<{ errorTypeId: number }>(`${this.base}/error-types`, req);
  }
  updateErrorType(id: number, req: UpsertErrorTypeRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/error-types/${id}`, req);
  }
  deleteErrorType(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/error-types/${id}`);
  }

  // ── Monitored Jobs ─────────────────────────────────────────────────────────
  getAllJobs(): Observable<MonitoredJob[]> {
    return this.http.get<MonitoredJob[]>(`${this.base}/monitored-jobs`);
  }
  /** Tier 2.5: full operational picture of one job (sources + rules + class + fix)
   *  for the dedicated config screen. */
  getJob(id: number): Observable<MonitoredJob> {
    return this.http.get<MonitoredJob>(`${this.base}/monitored-jobs/${id}`);
  }
  createJob(req: UpsertJobRequest): Observable<{ monitoredJobId: number }> {
    return this.http.post<{ monitoredJobId: number }>(`${this.base}/monitored-jobs`, req);
  }
  updateJob(id: number, req: UpsertJobRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/monitored-jobs/${id}`, req);
  }
  deleteJob(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/monitored-jobs/${id}`);
  }

  // ── Scan Sources (Tier 2.5) ────────────────────────────────────────────────
  createScanSource(jobId: number, req: UpsertScanSourceRequest): Observable<CreateScanSourceResponse> {
    return this.http.post<CreateScanSourceResponse>(`${this.base}/monitored-jobs/${jobId}/scan-sources`, req);
  }
  /** 204 normally; 200 + { warnings } when seedWatermarkFromNow ran. */
  updateScanSource(id: number, req: UpsertScanSourceRequest): Observable<{ warnings?: string[] | null } | null> {
    return this.http.put<{ warnings?: string[] | null } | null>(`${this.base}/scan-sources/${id}`, req);
  }
  deleteScanSource(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/scan-sources/${id}`);
  }
  /** Source-scoped rule create — the canonical add-rule path now that the worker
   *  scans per source. */
  createScanRuleForSource(sourceId: number, req: UpsertScanRuleRequest): Observable<CreateScanRuleResponse> {
    return this.http.post<CreateScanRuleResponse>(`${this.base}/scan-sources/${sourceId}/scan-rules`, req);
  }

  // ── Scan Check Rules ───────────────────────────────────────────────────────
  createScanRule(jobId: number, req: UpsertScanRuleRequest): Observable<CreateScanRuleResponse> {
    return this.http.post<CreateScanRuleResponse>(`${this.base}/monitored-jobs/${jobId}/scan-rules`, req);
  }
  /** 204 normally; 200 + { warnings } when seedWatermarkFromNow ran. */
  updateScanRule(id: number, req: UpsertScanRuleRequest): Observable<{ warnings?: string[] | null } | null> {
    return this.http.put<{ warnings?: string[] | null } | null>(`${this.base}/scan-rules/${id}`, req);
  }
  deleteScanRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/scan-rules/${id}`);
  }

  // ── Per-job Classification Rules ───────────────────────────────────────────
  createJobClassificationRule(jobId: number, req: UpsertJobClassificationRuleRequest): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(`${this.base}/monitored-jobs/${jobId}/classification-rules`, req);
  }
  linkJobClassificationRule(jobId: number, ruleId: number): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(
      `${this.base}/monitored-jobs/${jobId}/classification-rules/${ruleId}/link`,
      null);
  }
  deleteJobClassificationRule(jobId: number, ruleId: number): Observable<void> {
    return this.http.delete<void>(
      `${this.base}/monitored-jobs/${jobId}/classification-rules/${ruleId}`);
  }

  // ── Global Classification Rules ────────────────────────────────────────────
  getAllClassificationRules(): Observable<ClassificationRule[]> {
    return this.http.get<ClassificationRule[]>(`${this.base}/classification-rules`);
  }
  createClassificationRule(req: UpsertClassificationRuleRequest): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(`${this.base}/classification-rules`, req);
  }
  updateClassificationRule(id: number, req: UpsertClassificationRuleRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/classification-rules/${id}`, req);
  }
  deleteClassificationRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/classification-rules/${id}`);
  }

  // ── Fix Policy Rules ───────────────────────────────────────────────────────
  /**
   * @param jobTypeId       Filter to a JobType's policies.
   * @param monitoredJobId  When set, additionally returns the override scoped
   *                        to this MonitoredJob (lookup priority: override
   *                        wins over default at evaluation time). Used by the
   *                        per-job Fix Options tab to show effective config.
   */
  getFixPolicyRules(jobTypeId?: number, monitoredJobId?: number): Observable<FixPolicyRule[]> {
    let params = new HttpParams();
    if (jobTypeId      !== undefined) params = params.set('jobTypeId',      jobTypeId);
    if (monitoredJobId !== undefined) params = params.set('monitoredJobId', monitoredJobId);
    return this.http.get<FixPolicyRule[]>(`${this.base}/fix-policy-rules`,
      { params: params.keys().length ? params : undefined });
  }
  getFixPolicyRuleById(id: number): Observable<FixPolicyRule> {
    return this.http.get<FixPolicyRule>(`${this.base}/fix-policy-rules/${id}`);
  }
  createFixPolicyRule(req: UpsertFixPolicyRuleRequest): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(`${this.base}/fix-policy-rules`, req);
  }
  updateFixPolicyRule(id: number, req: UpsertFixPolicyRuleRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/fix-policy-rules/${id}`, req);
  }
  deleteFixPolicyRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/fix-policy-rules/${id}`);
  }
}
