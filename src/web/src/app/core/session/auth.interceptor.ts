import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { SessionStore } from './session.store';

/**
 * A 401 on any API call except the auth endpoints means the session ended (signed out elsewhere, password
 * changed, cookie expired): forget it and send the user to sign in, then back to where they were.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        !request.url.startsWith('/api/auth/')
      ) {
        session.clear();
        void router.navigate(['/sign-in'], { queryParams: { returnUrl: router.url } });
      }
      return throwError(() => error);
    }),
  );
};
