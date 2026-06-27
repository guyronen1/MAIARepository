import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs/operators';
import { AuthService, CurrentUser } from '../services/auth.service';

/**
 * Gate for the authenticated shell. Cosmetic relative to the API (which enforces
 * independently): not authenticated → /login; must-change-password → /change-password
 * (the API blocks every /api/* call until rotation, so route them there). On first load
 * the cached identity is empty, so it asks the server (/me) once.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const decide = (u: CurrentUser | null): boolean | UrlTree => {
    if (!u) return router.parseUrl('/login');
    if (u.mustChangePassword && state.url !== '/change-password') return router.parseUrl('/change-password');
    return true;
  };

  const cached = auth.currentUser();
  return cached ? decide(cached) : auth.refresh().pipe(map(decide));
};
