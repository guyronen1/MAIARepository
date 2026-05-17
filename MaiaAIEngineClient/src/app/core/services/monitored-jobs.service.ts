import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { MonitoredJob } from '../models';

@Injectable({ providedIn: 'root' })
export class MonitoredJobsService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/data`;

  getAll(): Observable<MonitoredJob[]> {
    return this.http.get<MonitoredJob[]>(`${this.base}/monitored-jobs`);
  }
}
