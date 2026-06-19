import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { NotificationService } from '../services/notification.service';

/**
 * Distinguishes the THREE failure cases — never collapse them:
 *   • 401 (no/expired session)        → clear identity, redirect to /login
 *   • 403 reason=PasswordChangeRequired → redirect to /change-password
 *   • plain 403 (wrong role)          → show a "no permission" message, STAY PUT
 *
 * Collapsing all 403s to a redirect would bounce an over-permissioned UI action
 * confusingly and hide the real cause. Auth endpoints are skipped so a bad-credential
 * 401 on /auth/login surfaces in the login form instead of self-redirecting.
 */
export const authErrorInterceptor: HttpInterceptorFn = (req, next) => {
  const router = inject(Router);
  const auth = inject(AuthService);
  const notify = inject(NotificationService);
  const isAuthEndpoint = req.url.includes('/auth/');

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      if (!isAuthEndpoint) {
        if (err.status === 401) {
          auth.clear();
          router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
        } else if (err.status === 403) {
          if (err.error?.error === 'PasswordChangeRequired') {
            router.navigate(['/change-password']);
          } else {
            notify.error("You don't have permission to do that.");
          }
        }
      }
      return throwError(() => err);
    }),
  );
};
