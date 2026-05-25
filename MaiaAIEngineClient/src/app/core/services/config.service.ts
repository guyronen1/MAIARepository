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
}

export interface FixPolicyRule {
  ruleId: number;
  jobTypeId: number;
  errorTypeId: number;
  errorTypeCode: string;
  actionToApply: string;
  fixCategory: string;
  actionType: string;
  actionPayload: string | null;
  isAutoHealEligible: boolean;
  enabled: boolean;
}

export interface UpsertJobRequest {
  name: string; displayName: string | null; jobTypeId: number; scanTypeId: number;
  logFolder: string | null; searchPatterns: string | null; connectionName: string | null;
  logSourceUrl: string | null; pollingIntervalSeconds: number; isActive: boolean; description: string | null;
}
export interface UpsertScanRuleRequest {
  checkType: string; sourceTable: string | null; targetField: string;
  minValue: number | null; maxValue: number | null; expectedValue: string | null;
  watermarkColumn: string | null; sourceIdColumn: string | null;
  severity: string; description: string | null; isActive: boolean;
}
export interface UpsertClassificationRuleRequest {
  jobTypeId: number; errorTypeId: number; pattern: string;
  confidence: number; priority: number; isActive: boolean;
}
export interface UpsertJobClassificationRuleRequest {
  errorTypeId: number; pattern: string;
  confidence: number; priority: number; isActive: boolean;
}
export interface UpsertFixPolicyRuleRequest {
  jobTypeId: number; errorTypeId: number;
  actionToApply: string; fixCategory: string; actionType: string;
  actionPayload: string | null; isAutoHealEligible: boolean; enabled: boolean;
}

@Injectable({ providedIn: 'root' })
export class ConfigService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/config`;

  // ── Lookups ────────────────────────────────────────────────────────────────
  getJobTypes():   Observable<JobType[]>   { return this.http.get<JobType[]>(`${this.base}/job-types`); }
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
  createJob(req: UpsertJobRequest): Observable<{ monitoredJobId: number }> {
    return this.http.post<{ monitoredJobId: number }>(`${this.base}/monitored-jobs`, req);
  }
  updateJob(id: number, req: UpsertJobRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/monitored-jobs/${id}`, req);
  }
  deleteJob(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/monitored-jobs/${id}`);
  }

  // ── Scan Check Rules ───────────────────────────────────────────────────────
  createScanRule(jobId: number, req: UpsertScanRuleRequest): Observable<{ checkRuleId: number }> {
    return this.http.post<{ checkRuleId: number }>(`${this.base}/monitored-jobs/${jobId}/scan-rules`, req);
  }
  updateScanRule(id: number, req: UpsertScanRuleRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/scan-rules/${id}`, req);
  }
  deleteScanRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/scan-rules/${id}`);
  }

  // ── Per-job Classification Rules ───────────────────────────────────────────
  createJobClassificationRule(jobId: number, req: UpsertJobClassificationRuleRequest): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(`${this.base}/monitored-jobs/${jobId}/classification-rules`, req);
  }
  linkJobClassificationRule(jobId: number, ruleId: number): Observable<{ ruleId: number }> {
    return this.http.post<{ ruleId: number }>(`${this.base}/monitored-jobs/${jobId}/classification-rules/${ruleId}/link`, {});
  }
  deleteJobClassificationRule(jobId: number, ruleId: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/monitored-jobs/${jobId}/classification-rules/${ruleId}`);
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
  getFixPolicyRules(jobTypeId?: number): Observable<FixPolicyRule[]> {
    const params = jobTypeId ? new HttpParams().set('jobTypeId', jobTypeId) : undefined;
    return this.http.get<FixPolicyRule[]>(`${this.base}/fix-policy-rules`, { params });
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
