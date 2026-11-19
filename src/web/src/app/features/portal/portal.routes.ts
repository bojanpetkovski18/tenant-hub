import { Routes } from '@angular/router';
import { portalSessionGuard } from './portal-session';

/**
 * The client portal (SPEC-V2 4.4, 6): its own lazy-loaded area under /portal/:slug, with its own layout and session.
 * Everything but the sign-in page needs a signed-in contact of this portal.
 */
export const portalRoutes: Routes = [
  {
    path: '',
    loadComponent: () => import('./portal-layout').then((m) => m.PortalLayout),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'visits' },
      {
        path: 'sign-in',
        title: 'Sign in to the portal',
        loadComponent: () => import('./portal-sign-in-page').then((m) => m.PortalSignInPage),
      },
      {
        path: 'visits',
        title: 'Visits',
        canActivate: [portalSessionGuard],
        loadComponent: () => import('./portal-visits-page').then((m) => m.PortalVisitsPage),
      },
      {
        path: 'visits/:id',
        title: 'Visit',
        canActivate: [portalSessionGuard],
        loadComponent: () => import('./portal-visit-page').then((m) => m.PortalVisitPage),
      },
      {
        path: 'quotes',
        title: 'Quotes',
        canActivate: [portalSessionGuard],
        loadComponent: () => import('./portal-quotes-page').then((m) => m.PortalQuotesPage),
      },
      {
        path: 'requests',
        title: 'Requests',
        canActivate: [portalSessionGuard],
        loadComponent: () => import('./portal-requests-page').then((m) => m.PortalRequestsPage),
      },
      {
        path: 'requests/new',
        title: 'Request work',
        canActivate: [portalSessionGuard],
        loadComponent: () =>
          import('./portal-request-form-page').then((m) => m.PortalRequestFormPage),
      },
    ],
  },
];
