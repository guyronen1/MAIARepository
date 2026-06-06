import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { JobFailure, FailureStatus, PagedResult } from '../models';

export interface DashboardStats {
  totalFailures:        number;
  active:               number;
  resolved:             number;
  manualRequired:       number;
  unclassified:         number;
  awaitingAction:       number;
  autoFixed:            number;
  manuallyFixed:        number;
  /** Today-scoped (server-local midnight to now) — feed for the Resolved Today KPI. */
  resolvedToday:        number;
  autoFixedToday:       number;
  manuallyFixedToday:   number;
  /** Distinct failures currently in ManualRequired that had at least one
   *  Success=false FixExecutionLog since today-midnight. Drives the "Fix
   *  Failures Today" KPI tile; clicking it drills into /failures?view=fix-failed
   *  with the same predicate, so KPI count and drill-down list always agree. */
  fixFailedToday:       number;
  /** Active (Failed) failures the system can't act on for lack of config:
   *  unclassified (no ErrorType) OR classified with no enabled FixPolicyRule
   *  for its scope. Drives the "Unconfigured" KPI; drill-down is
   *  /failures?view=unconfigured (same predicate). `unconfiguredNoPolicy` is
   *  the no-fix-policy half; the no-classification half is `unclassified`. */
  unconfigured:         number;
  unconfiguredNoPolicy: number;
}

@Injectable({ providedIn: 'root' })
export class FailuresService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/data`;

  getFailures(page = 1, pageSize = 50, view?: string): Observable<PagedResult<JobFailure>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (view) params = params.set('view', view);
    return this.http.get<PagedResult<JobFailure>>(`${this.base}/failures`, { params });
  }

  getFailureStatus(failureId: number): Observable<FailureStatus> {
    return this.http.get<FailureStatus>(`${this.base}/failures/${failureId}/status`);
  }

  /**
   * Operator confirms the manual work is done — flips JobFailure.Status to
   * Resolved and writes a ManuallyResolved audit row. Idempotent on the
   * backend: re-marking an already-resolved failure is a no-op 204, not 4xx.
   * Lives under /api/failures (operator-action namespace), not /api/data
   * (read-only namespace).
   */
  markResolved(failureId: number, operatorId: string): Observable<void> {
    return this.http.post<void>(
      `${environment.apiUrl}/failures/${failureId}/mark-resolved`,
      { operatorId });
  }

  getDashboardStats(): Observable<DashboardStats> {
    return this.http.get<DashboardStats>(`${this.base}/dashboard-stats`);
  }

  getFailuresOverTime(range: '24h' | '7d' | '30d'): Observable<FailuresOverTimeResponse> {
    return this.http.get<FailuresOverTimeResponse>(
      `${this.base}/analytics/failures-over-time`,
      { params: new HttpParams().set('range', range) });
  }

  /**
   * Top-N monitored jobs by failure count within the chosen range. Shares the
   * range toggle with Errors Over Time. Returns a plain array (no wrapper) —
   * see CLAUDE.md follow-up about the response-shape inconsistency.
   */
  getFailuresByJob(range: '24h' | '7d' | '30d', limit = 10): Observable<FailuresByJobItem[]> {
    return this.http.get<FailuresByJobItem[]>(
      `${this.base}/analytics/failures-by-job`,
      { params: new HttpParams().set('range', range).set('limit', limit) });
  }

  /** 7-day stacked resolution breakdown — always returns exactly 7 day-buckets. */
  getResolutionMix(): Observable<ResolutionMixItem[]> {
    return this.http.get<ResolutionMixItem[]>(
      `${this.base}/analytics/resolution-mix`,
      { params: new HttpParams().set('range', '7d') });
  }
}

export interface FailuresByJobItem {
  monitoredJobId: number;
  jobName:        string;
  failureCount:   number;
}

export interface ResolutionMixItem {
  /** YYYY-MM-DD, server-local midnight day key. */
  bucketDay:        string;
  autoHealed:       number;
  operatorApproved: number;
  manualRequired:   number;
  stillActive:      number;
}

export interface FailuresOverTimeBucket {
  bucketStart:      string;
  errorTypeId:      number;
  errorTypeCode:    string;
  errorTypeDisplay: string;
  count:            number;
}

export interface FailuresOverTimeResponse {
  range:      '24h' | '7d' | '30d';
  bucketSize: 'hour' | 'day';
  rangeStart: string;
  rangeEnd:   string;
  buckets:    FailuresOverTimeBucket[];
}
