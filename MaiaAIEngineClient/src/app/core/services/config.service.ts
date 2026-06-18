import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { MonitoredJob, ScanCheckRule } from '../models';

export interface JobType    { jobTypeId: number; name: string; description: string; }
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
}
export interface UpsertScanRuleRequest {
  checkType: string; sourceTable: string | null; targetField: string;
  minValue: number | null; maxValue: number | null; expectedValue: string | null;
  watermarkColumn: string | null; sourceIdColumn: string | null;
  severity: string; description: string | null; isActive: boolean;
  /** DB scans only — column on the source row holding the input file path. */
  filePathColumn:   string | null;
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

  /** Operator identity attached to every config write. Backend rejects writes
   *  with no operatorId. Hardcoded for now — when authn lands, this becomes
   *  the authenticated user (single point of change). */
  private readonly actor = 'operator';

  /** Spread the actor into a request body. Used by POST/PUT calls. */
  private withActor<T extends object>(req: T): T & { operatorId: string } {
    return { ...req, operatorId: this.actor };
  }

  /** Query-string variant for DELETE / link endpoints that have no body. */
  private actorParams(): HttpParams {
    return new HttpParams().set('operatorId', this.actor);
  }

  // ── Lookups ────────────────────────────────────────────────────────────────
  getJobTypes():   Observable<JobType[]>   { return this.http.get<JobType[]>(`${this.base}/job-types`); }
  getErrorTypes(includeInactive = false): Observable<ErrorType[]> {
    const params = includeInactive ? new HttpParams().set('includeInactive', 'true') : undefined;
    return this.http.get<ErrorType[]>(`${this.base}/error-types`, { params });
  }
  createErrorType(req: UpsertErrorTypeRequest): Observable<{ errorTypeId: number }> {
    return this.http.post<{ errorTypeId: number }>(`${this.base}/error-types`, this.withActor(req));
  }
  updateErrorType(id: number, req: UpsertErrorTypeRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/error-types/${id}`, this.withActor(req));
  }
  deleteErrorType(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/error-types/${id}`, { params: this.actorParams() });
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
    return this.http.post<{ monitoredJobId: number }>(`${this.base}/monitored-jobs`, this.withActor(req));
  }
  updateJob(id: number, req: UpsertJobRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/monitored-jobs/${id}`, this.withActor(req));
  }
  deleteJob(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/monitored-jobs/${id}`, { params: this.actorParams() });
  }

  // ── Scan Sources (Tier 2.5) ────────────────────────────────────────────────
  createScanSource(jobId: number, req: UpsertScanSourceRequest): Observable<{ scanSourceId: number }> {
    return this.http.post<{ scanSourceId: number }>(`${this.base}/monitored-jobs/${jobId}/scan-sources`, this.withActor(req));
  }
  updateScanSource(id: number, req: UpsertScanSourceRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/scan-sources/${id}`, this.withActor(req));
  }
  deleteScanSource(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/scan-sources/${id}`, { params: this.actorParams() });
  }
  /** Source-scoped rule create — the canonical add-rule path now that the worker
   *  scans per source. */
  createScanRuleForSource(sourceId: number, req: UpsertScanRuleRequest): Observable<{ checkRuleId: number }> {
    return this.http.post<{ checkRuleId: number }>(`${this.base}/scan-sources/${sourceId}/scan-rules`, this.withActor(req));
  }

  // ── Scan Check Rules ───────────────────────────────────────────────────────
  createScanRule(jobId: number, req: UpsertScanRuleRequest): Observable<{ checkRuleId: number }> {
    return this.http.post<{ checkRuleId: number }>(`${this.base}/monitored-jobs/${jobId}/scan-rules`, this.withActor(req));
  }
  updateScanRule(id: number, req: UpsertScanRuleRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/scan-rules/${id}`, this.withActor(req));
  }
  deleteScanRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/scan-rules/${id}`, { params: this.actorParams() });
  }

  // ── Per-job Classification Rules ───────────────────────────────────────────
  createJobClassificationRule(jobId: number, req: UpsertJobClassificationRuleRequest): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(`${this.base}/monitored-jobs/${jobId}/classification-rules`, this.withActor(req));
  }
  linkJobClassificationRule(jobId: number, ruleId: number): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(
      `${this.base}/monitored-jobs/${jobId}/classification-rules/${ruleId}/link`,
      null,
      { params: this.actorParams() });
  }
  deleteJobClassificationRule(jobId: number, ruleId: number): Observable<void> {
    return this.http.delete<void>(
      `${this.base}/monitored-jobs/${jobId}/classification-rules/${ruleId}`,
      { params: this.actorParams() });
  }

  // ── Global Classification Rules ────────────────────────────────────────────
  getAllClassificationRules(): Observable<ClassificationRule[]> {
    return this.http.get<ClassificationRule[]>(`${this.base}/classification-rules`);
  }
  createClassificationRule(req: UpsertClassificationRuleRequest): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(`${this.base}/classification-rules`, this.withActor(req));
  }
  updateClassificationRule(id: number, req: UpsertClassificationRuleRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/classification-rules/${id}`, this.withActor(req));
  }
  deleteClassificationRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/classification-rules/${id}`, { params: this.actorParams() });
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
    return this.http.post<{ ruleId: number }>(`${this.base}/fix-policy-rules`, this.withActor(req));
  }
  updateFixPolicyRule(id: number, req: UpsertFixPolicyRuleRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/fix-policy-rules/${id}`, this.withActor(req));
  }
  deleteFixPolicyRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/fix-policy-rules/${id}`, { params: this.actorParams() });
  }
}
