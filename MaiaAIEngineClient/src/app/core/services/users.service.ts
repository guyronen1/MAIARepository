import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { MaiaRole } from './auth.service';

/** Read shape from GET /api/users. Note: the server DTO deliberately omits the
 *  password hash — it never reaches the client. */
export interface AppUser {
  userId: number;
  username: string;
  role: MaiaRole;
  isActive: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

export interface CreateUserRequest { username: string; password: string; role: MaiaRole; }
export interface UpdateUserRequest { role: MaiaRole; isActive: boolean; }
export interface ResetPasswordRequest { newPassword: string; }

/**
 * Admin user management over UsersController (RequireAdmin). Identity for audit is
 * server-side (the admin's cookie principal) — no operatorId travels from the client.
 */
@Injectable({ providedIn: 'root' })
export class UsersService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/users`;

  list(): Observable<AppUser[]> {
    return this.http.get<AppUser[]>(this.base);
  }
  create(req: CreateUserRequest): Observable<{ userId: number }> {
    return this.http.post<{ userId: number }>(this.base, req);
  }
  update(id: number, req: UpdateUserRequest): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}`, req);
  }
  resetPassword(id: number, req: ResetPasswordRequest): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/reset-password`, req);
  }
}
