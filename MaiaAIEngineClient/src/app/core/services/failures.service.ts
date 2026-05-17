import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { JobFailure, FailureStatus, PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class FailuresService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/data`;

  getFailures(page = 1, pageSize = 50): Observable<PagedResult<JobFailure>> {
    return this.http.get<PagedResult<JobFailure>>(
      `${this.base}/failures?page=${page}&pageSize=${pageSize}`
    );
  }

  getFailureStatus(failureId: number): Observable<FailureStatus> {
    return this.http.get<FailureStatus>(`${this.base}/failures/${failureId}/status`);
  }
}
