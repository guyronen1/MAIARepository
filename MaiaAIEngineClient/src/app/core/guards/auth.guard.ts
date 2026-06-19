import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs/operators';
import { AuthService, CurrentUser } from '../services/auth.service';

/**
 * Gate for the authenticated shell. Cosmetic relative to the API (which enforces
 * independently): not authenticated → /login. The change-password prompt is a soft,
 * skippable one-time nudge handled at login (not forced here), so the guard does not
 * redirect on MustChangePassword. On first load the cached identity is empty, so it
 * asks the server (/me) once.
 */
export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const decide = (u: CurrentUser | null): boolean | UrlTree =>
    u ? true : router.parseUrl('/login');

  const cached = auth.currentUser();
  return cached ? decide(cached) : auth.refresh().pipe(map(decide));
};
