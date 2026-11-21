import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/session/auth.guards';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./core/layout/public-layout').then((m) => m.PublicLayout),
    children: [
      {
        path: '',
        pathMatch: 'full',
        title: 'TenantHub',
        loadComponent: () => import('./features/landing/landing-page').then((m) => m.LandingPage),
      },
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
        path: 'forgot-password',
        title: 'Reset your password',
        loadComponent: () =>
          import('./features/auth/forgot-password-page').then((m) => m.ForgotPasswordPage),
      },
      {
        // Like the invitation links below, the token is in the fragment (D028). Open whoever is signed in.
        path: 'reset-password',
        title: 'Choose a new password',
        loadComponent: () =>
          import('./features/auth/reset-password-page').then((m) => m.ResetPasswordPage),
      },
      {
        path: 'verify-email',
        title: 'Confirm your email address',
        loadComponent: () =>
          import('./features/auth/verify-email-page').then((m) => m.VerifyEmailPage),
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
      {
        // Linked from every notification email (M20).
        path: 'account/notifications',
        title: 'Notifications',
        canActivate: [authGuard],
        loadComponent: () =>
          import('./features/notifications/notification-preferences-page').then(
            (m) => m.NotificationPreferencesPage,
          ),
      },
    ],
  },
  {
    path: 'o/:slug',
    canActivate: [authGuard],
    loadComponent: () => import('./core/layout/app-shell').then((m) => m.AppShell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      {
        path: 'dashboard',
        title: 'Dashboard',
        loadComponent: () =>
          import('./features/dashboard/dashboard-page').then((m) => m.DashboardPage),
      },
      {
        path: 'jobs',
        title: 'Jobs',
        loadComponent: () => import('./features/jobs/jobs-list-page').then((m) => m.JobsListPage),
      },
      {
        path: 'jobs/new',
        title: 'New job',
        loadComponent: () => import('./features/jobs/job-form-page').then((m) => m.JobFormPage),
      },
      {
        path: 'jobs/:id',
        title: 'Job',
        loadComponent: () => import('./features/jobs/job-detail-page').then((m) => m.JobDetailPage),
      },
      {
        path: 'jobs/:id/edit',
        title: 'Edit job',
        loadComponent: () => import('./features/jobs/job-form-page').then((m) => m.JobFormPage),
      },
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
        path: 'board',
        title: 'Board',
        loadComponent: () => import('./features/board/board-page').then((m) => m.BoardPage),
      },
      {
        path: 'agreements',
        title: 'Agreements',
        loadComponent: () =>
          import('./features/agreements/agreements-list-page').then((m) => m.AgreementsListPage),
      },
      {
        path: 'agreements/new',
        title: 'New agreement',
        loadComponent: () =>
          import('./features/agreements/agreement-form-page').then((m) => m.AgreementFormPage),
      },
      {
        path: 'agreements/:id',
        title: 'Agreement',
        loadComponent: () =>
          import('./features/agreements/agreement-detail-page').then((m) => m.AgreementDetailPage),
      },
      {
        path: 'agreements/:id/edit',
        title: 'Edit agreement',
        loadComponent: () =>
          import('./features/agreements/agreement-form-page').then((m) => m.AgreementFormPage),
      },
      {
        path: 'requests',
        title: 'Requests',
        loadComponent: () =>
          import('./features/requests/requests-page').then((m) => m.RequestsPage),
      },
      {
        path: 'members',
        title: 'Members',
        loadComponent: () => import('./features/members/members-page').then((m) => m.MembersPage),
      },
      {
        path: 'audit',
        title: 'Audit log',
        loadComponent: () => import('./features/audit/audit-page').then((m) => m.AuditLogPage),
      },
      {
        path: 'settings',
        title: 'Settings',
        loadComponent: () =>
          import('./features/settings/settings-page').then((m) => m.SettingsPage),
      },
    ],
  },
  {
    // The client portal: its own area, layout and session (SPEC-V2 4.4, D040), loaded only when opened.
    path: 'portal/:slug',
    loadChildren: () => import('./features/portal/portal.routes').then((m) => m.portalRoutes),
  },
  {
    path: '**',
    title: 'Page not found',
    loadComponent: () => import('./features/not-found/not-found-page').then((m) => m.NotFoundPage),
  },
];
