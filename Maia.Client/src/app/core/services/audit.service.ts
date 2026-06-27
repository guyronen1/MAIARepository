import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuditLogPage } from '../models/audit.model';

export interface AuditQuery {
  entityType?: string;
  entityId?: string;
  actor?: string;
  eventType?: string;
  fromDate?: string;
  toDate?: string;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class AuditService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/admin/audit-log`;

  query(params: AuditQuery): Observable<AuditLogPage> {
    let p = new HttpParams();
    if (params.entityType) p = p.set('entityType', params.entityType);
    if (params.entityId)   p = p.set('entityId', params.entityId);
    if (params.actor)      p = p.set('actor', params.actor);
    if (params.eventType)  p = p.set('eventType', params.eventType);
    if (params.fromDate)   p = p.set('fromDate', params.fromDate);
    if (params.toDate)     p = p.set('toDate', params.toDate);
    if (params.page)       p = p.set('page', String(params.page));
    if (params.pageSize)   p = p.set('pageSize', String(params.pageSize));
    return this.http.get<AuditLogPage>(this.base, { params: p });
  }
}
