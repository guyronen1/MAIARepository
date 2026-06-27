import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs/operators';
import { AuthService, CurrentUser } from '../services/auth.service';

/**
 * Admin-only route gate (e.g. /config/users). Cosmetic relative to the API, which
 * enforces RequireAdmin independently — but it avoids dropping a non-admin onto a
 * screen that would just 403. Not authenticated → /login; authenticated non-admin →
 * /dashboard. Pair with authGuard (which handles the must-change redirect).
 */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const decide = (u: CurrentUser | null): boolean | UrlTree => {
    if (!u) return router.parseUrl('/login');
    return u.role === 'Administrator' ? true : router.parseUrl('/dashboard');
  };

  const cached = auth.currentUser();
  return cached ? decide(cached) : auth.refresh().pipe(map(decide));
};
