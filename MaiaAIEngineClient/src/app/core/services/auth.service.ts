import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { catchError, map, tap } from 'rxjs/operators';
import { environment } from '../../../environments/environment';

export type MaiaRole = 'User' | 'Operator' | 'Administrator';

export interface CurrentUser {
  username: string;
  role: MaiaRole;
  mustChangePassword: boolean;
}

interface MeResponse {
  authenticated: boolean;
  username?: string;
  role?: MaiaRole;
  mustChangePassword?: boolean;
}

/**
 * Client view of the authenticated session. The session itself is a server-side,
 * httpOnly cookie — never readable here — so this only mirrors identity/role for UX
 * (menu/button gating, the change-password gate). The API is the real boundary.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}/auth`;

  readonly currentUser = signal<CurrentUser | null>(null);
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly role = computed<MaiaRole | null>(() => this.currentUser()?.role ?? null);

  /** Role floor check mirroring the backend tiers (User < Operator < Administrator). */
  hasAtLeast(min: MaiaRole): boolean {
    const order: Record<MaiaRole, number> = { User: 1, Operator: 2, Administrator: 3 };
    const r = this.role();
    return r !== null && order[r] >= order[min];
  }

  login(username: string, password: string): Observable<CurrentUser> {
    return this.http.post<MeResponse>(`${this.base}/login`, { username, password }).pipe(
      map(r => ({ username: r.username!, role: r.role!, mustChangePassword: !!r.mustChangePassword })),
      tap(u => this.currentUser.set(u)),
    );
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${this.base}/logout`, {}).pipe(
      tap(() => this.currentUser.set(null)),
    );
  }

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`${this.base}/change-password`, { currentPassword, newPassword }).pipe(
      tap(() => this.clearMustChange()),
    );
  }

  /** "Skip for now" — acknowledge the one-time prompt without changing the password. */
  dismissPasswordChange(): Observable<void> {
    return this.http.post<void>(`${this.base}/dismiss-password-change`, {}).pipe(
      tap(() => this.clearMustChange()),
    );
  }

  private clearMustChange(): void {
    const u = this.currentUser();
    if (u) this.currentUser.set({ ...u, mustChangePassword: false });
  }

  /** Ask the server who we are (cookie-based) and cache it. Used by the route guard
   *  on first load / refresh. Never throws — anonymous resolves to null. */
  refresh(): Observable<CurrentUser | null> {
    return this.http.get<MeResponse>(`${this.base}/me`).pipe(
      map(r => r.authenticated
        ? { username: r.username!, role: r.role!, mustChangePassword: !!r.mustChangePassword }
        : null),
      tap(u => this.currentUser.set(u)),
      catchError(() => { this.currentUser.set(null); return of(null); }),
    );
  }

  /** Drop the cached identity (called by the 401 interceptor on session loss). */
  clear(): void {
    this.currentUser.set(null);
  }
}
