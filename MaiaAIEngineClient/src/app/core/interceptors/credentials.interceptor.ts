import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Sends the httpOnly session cookie on every request. Required because the SPA and
 * API are different origins (localhost:4200 → :5095), and the browser only attaches
 * credentials cross-origin when withCredentials is set (and the server replies with
 * Access-Control-Allow-Credentials, which the API's CORS policy does).
 */
export const credentialsInterceptor: HttpInterceptorFn = (req, next) =>
  next(req.clone({ withCredentials: true }));
