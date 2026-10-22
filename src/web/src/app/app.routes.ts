import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/session/auth.guards';

// Pages not built yet are placeholders until the milestone named in their description (SPEC 6, prompts/).
const placeholder = () =>
  import('./shared/placeholder-page/placeholder-page').then((m) => m.PlaceholderPage);

const page = (path: string, title: string, description: string) => ({
  path,
  title,
  loadComponent: placeholder,
  data: { heading: title, description },
});

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./core/layout/public-layout').then((m) => m.PublicLayout),
    children: [
      page('', 'TenantHub', 'The landing page arrives in M09.'),
      {
        path: 'sign-in',
        title: 'Sign in',
        canActivate: [guestGuard],
        loadComponent: () => import('./features/auth/sign-in-page').then((m) => m.SignInPage),
      },
      {
        path: 'sign-up',
        title: 'Sign up',
        canActivate: [guestGuard],
        loadComponent: () => import('./features/auth/sign-up-page').then((m) => m.SignUpPage),
      },
      {
        // The token is in the fragment (/invite#…), never in the path (D028). Open to guests and signed-in users.
        path: 'invite',
        title: 'Accept invitation',
        loadComponent: () =>
          import('./features/invitations/accept-invitation-page').then(
            (m) => m.AcceptInvitationPage,
          ),
      },
      {
        path: 'orgs',
        title: 'Your organizations',
        canActivate: [authGuard],
        loadComponent: () => import('./features/orgs/org-picker-page').then((m) => m.OrgPickerPage),
      },
      {
        path: 'account',
        title: 'Account',
        canActivate: [authGuard],
        loadComponent: () => import('./features/account/account-page').then((m) => m.AccountPage),
      },
    ],
  },
  {
    path: 'o/:slug',
    canActivate: [authGuard],
    loadComponent: () => import('./core/layout/app-shell').then((m) => m.AppShell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      page('dashboard', 'Dashboard', 'The dashboard arrives in M09.'),
      page('jobs', 'Jobs', 'Jobs arrive in M06.'),
      page('jobs/new', 'New job', 'Jobs arrive in M06.'),
      page('jobs/:id', 'Job', 'Jobs arrive in M06.'),
      {
        path: 'clients',
        title: 'Clients',
        loadComponent: () =>
          import('./features/clients/clients-list-page').then((m) => m.ClientsListPage),
      },
      {
        path: 'clients/new',
        title: 'New client',
        loadComponent: () =>
          import('./features/clients/client-form-page').then((m) => m.ClientFormPage),
      },
      {
        path: 'clients/:id',
        title: 'Client',
        loadComponent: () =>
          import('./features/clients/client-detail-page').then((m) => m.ClientDetailPage),
      },
      {
        path: 'clients/:id/edit',
        title: 'Edit client',
        loadComponent: () =>
          import('./features/clients/client-form-page').then((m) => m.ClientFormPage),
      },
      {
        path: 'members',
        title: 'Members',
        loadComponent: () => import('./features/members/members-page').then((m) => m.MembersPage),
      },
      page('audit', 'Audit log', 'The audit log viewer arrives in M07.'),
      page('settings', 'Settings', 'Organization settings arrive in M08.'),
    ],
  },
  {
    path: '**',
    title: 'Page not found',
    loadComponent: () => import('./features/not-found/not-found-page').then((m) => m.NotFoundPage),
  },
];
