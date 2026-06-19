import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Recommendation, PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class RecommendationsService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}`;

  getRecommendations(page = 1, pageSize = 50): Observable<PagedResult<Recommendation>> {
    return this.http.get<PagedResult<Recommendation>>(
      `${this.base}/data/recommendations?page=${page}&pageSize=${pageSize}`
    );
  }

  // The acting operator is the authenticated session (server-side) — no operatorId
  // travels from the client.
  approveRecommendation(id: number): Observable<unknown> {
    return this.http.post(`${this.base}/recommendations/${id}/approve`, {});
  }

  rejectRecommendation(id: number): Observable<unknown> {
    return this.http.post(`${this.base}/recommendations/${id}/reject`, {});
  }

  /** Re-run a fix that failed to execute (failure is in ManualRequired).
   *  Re-arms the failure + rec and drains synchronously with the current
   *  policy — use after fixing the root cause. */
  retryRecommendation(id: number): Observable<unknown> {
    return this.http.post(`${this.base}/recommendations/${id}/retry`, {});
  }

  generateSuggestions(): Observable<{ generated: number }> {
    return this.http.post<{ generated: number }>(`${this.base}/fix/generate-suggestions`, {});
  }

  executeFixes(): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(`${this.base}/fix/execute-fixes`, {});
  }

}
