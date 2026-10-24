import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { DemoInfo } from '../core/demo/demo.store';
import { authInterceptor } from '../core/session/auth.interceptor';
import { Permission, Role, SessionUser } from '../core/session/session.models';

/** Test data only, mirroring the API's role map (the app itself always uses what the API sends). */
export const permissionsFor: Readonly<Record<Role, readonly Permission[]>> = {
  owner: [
    'org.read',
    'org.manage',
    'org.delete',
    'billing.manage',
    'members.manage',
    'clients.read',
    'clients.write',
    'clients.delete',
    'jobs.read',
    'jobs.create',
    'jobs.manage',
    'jobs.work',
    'audit.read',
  ],
  admin: [
    'org.read',
    'org.manage',
    'members.manage',
    'clients.read',
    'clients.write',
    'clients.delete',
    'jobs.read',
    'jobs.create',
    'jobs.manage',
    'jobs.work',
    'audit.read',
  ],
  member: ['org.read', 'clients.read', 'clients.write', 'jobs.read', 'jobs.create', 'jobs.work'],
  viewer: ['org.read', 'clients.read', 'jobs.read'],
};
import { SessionStore } from '../core/session/session.store';
import { tenantAccessInterceptor } from '../core/session/tenant-access.interceptor';

export const olivia: SessionUser = {
  id: '0199f0a0-0000-7000-8000-000000000001',
  email: 'olivia@brightline.example',
  displayName: 'Olivia Bennett',
  memberships: [
    {
      organizationId: '0199f0a0-0000-7000-8000-0000000000a1',
      slug: 'brightline',
      name: 'Brightline Cleaning Co.',
      role: 'owner',
      permissions: permissionsFor.owner,
      grantableRoles: ['owner', 'admin', 'member', 'viewer'],
      invitableRoles: ['admin', 'member', 'viewer'],
    },
    {
      organizationId: '0199f0a0-0000-7000-8000-0000000000a2',
      slug: 'harbor-pine',
      name: 'Harbor & Pine Landscaping',
      role: 'admin',
      permissions: permissionsFor.admin,
      grantableRoles: ['admin', 'member', 'viewer'],
      invitableRoles: ['admin', 'member', 'viewer'],
    },
  ],
};

/** HttpClient with the app's interceptor, answered by HttpTestingController. */
export function provideTestHttp(): (Provider | EnvironmentProviders)[] {
  return [
    provideHttpClient(withInterceptors([authInterceptor, tenantAccessInterceptor])),
    provideHttpClientTesting(),
  ];
}

/** Loads the session as the app initializer would, with the given user (null = signed out). */
export async function loadSession(user: SessionUser | null): Promise<void> {
  const loading = TestBed.inject(SessionStore).load();
  TestBed.inject(HttpTestingController).expectOne('/api/auth/session').flush({ user });
  await loading;
}

/** Lets awaited HTTP promises continue, then waits for rendering and navigation to finish. */
export async function settle(fixture: { whenStable(): Promise<unknown> }): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
}

/** The demo lookup the public pages make: 404 as outside demo mode (the default), or the demo's accounts. */
export function answerDemo(http: HttpTestingController, info: DemoInfo | null = null): void {
  const request = http.expectOne('/api/demo/accounts');
  if (info) {
    request.flush(info);
  } else {
    request.flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
  }
}
