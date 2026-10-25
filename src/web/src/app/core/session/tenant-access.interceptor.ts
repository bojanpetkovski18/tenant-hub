import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { SessionStore } from './session.store';

/** The API's answer when the caller is not (or no longer) an active member of the organization in the URL. */
export const organizationNotFoundCode = 'organization-not-found';

/**
 * Access to an organization can end while the user is signed in (removed by an admin, organization deleted).
 * The next tenant call answers 404 `organization-not-found`; then the session is reloaded, so the switcher no
 * longer lists the organization, and the user lands on the picker with an explanation (M03 acceptance).
 */
export const tenantAccessInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 404 &&
        request.url.startsWith('/api/orgs/') &&
        (error.error as { code?: unknown } | null)?.code === organizationNotFoundCode
      ) {
        void session
          .load()
          .then(() => router.navigate(['/orgs'], { queryParams: { notice: 'access-lost' } }));
      }
      return throwError(() => error);
    }),
  );
};
