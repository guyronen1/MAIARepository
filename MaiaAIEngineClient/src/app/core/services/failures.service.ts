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

  getDashboardStats(): Observable<DashboardStats> {
    return this.http.get<DashboardStats>(`${this.base}/dashboard-stats`);
  }

  getFailuresOverTime(range: '24h' | '7d' | '30d'): Observable<FailuresOverTimeResponse> {
    return this.http.get<FailuresOverTimeResponse>(
      `${this.base}/analytics/failures-over-time`,
      { params: new HttpParams().set('range', range) });
  }
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
