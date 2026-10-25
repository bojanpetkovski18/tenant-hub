import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { landingPath } from './landing-path';
import { readLastOrganization } from './last-organization';
import { SessionStore } from './session.store';

/** Signed-in users only; others go to sign-in and come back afterwards. */
export const authGuard: CanActivateFn = (_route, state) => {
  if (inject(SessionStore).isSignedIn()) {
    return true;
  }
  return inject(Router).createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } });
};

/** Sign-in and sign-up are for guests; a signed-in user goes where they would land after signing in. */
export const guestGuard: CanActivateFn = (route) => {
  const user = inject(SessionStore).user();
  if (!user) {
    return true;
  }
  const returnUrl = route.queryParamMap.get('returnUrl');
  return inject(Router).parseUrl(landingPath(user, returnUrl, readLastOrganization()));
};
