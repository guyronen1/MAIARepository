import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ScanResult } from '../models';

@Injectable({ providedIn: 'root' })
export class ScanService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/JobScan`;

  scanById(monitoredJobId: number): Observable<ScanResult> {
    return this.http.get<ScanResult>(`${this.base}/${monitoredJobId}`);
  }

  scanAll(): Observable<ScanResult[]> {
    return this.http.post<ScanResult[]>(`${this.base}/scan-all`, {});
  }

  classifyPending(): Observable<{ classified: number; suggestions: number; fixesQueued: number }> {
    return this.http.get<{ classified: number; suggestions: number; fixesQueued: number }>(
      `${this.base}/classify-pending`
    );
  }
}
