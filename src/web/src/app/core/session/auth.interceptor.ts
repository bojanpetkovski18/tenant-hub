import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { SessionStore } from './session.store';

/**
 * A 401 on any API call except the auth endpoints means the session ended (signed out elsewhere, password
 * changed, cookie expired): forget it and send the user to sign in, then back to where they were. The portal has
 * its own session and its own sign-in (D040), so its calls are left to the portal's interceptor.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        !request.url.startsWith('/api/auth/') &&
        !request.url.startsWith('/api/portal/')
      ) {
        session.clear();
        void router.navigate(['/sign-in'], { queryParams: { returnUrl: router.url } });
      }
      return throwError(() => error);
    }),
  );
};
