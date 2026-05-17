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

  approveRecommendation(id: number): Observable<unknown> {
    return this.http.patch(`${this.base}/data/recommendations/${id}/approve`, {});
  }

  rejectRecommendation(id: number): Observable<unknown> {
    return this.http.patch(`${this.base}/data/recommendations/${id}/reject`, {});
  }

  setAutoHeal(id: number, enabled: boolean): Observable<unknown> {
    return this.http.patch(`${this.base}/data/recommendations/${id}/auto-heal`, { enabled });
  }

  generateSuggestions(): Observable<{ generated: number }> {
    return this.http.post<{ generated: number }>(`${this.base}/fix/generate-suggestions`, {});
  }

  executeFixes(): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(`${this.base}/fix/execute-fixes`, {});
  }

  runPipeline(): Observable<{ classifications: number; message: string }> {
    return this.http.post<{ classifications: number; message: string }>(
      `${this.base}/process/run-pipeline`, {}
    );
  }
}
