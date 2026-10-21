import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { authInterceptor } from '../core/session/auth.interceptor';
import { SessionUser } from '../core/session/session.models';
import { SessionStore } from '../core/session/session.store';

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
    },
    {
      organizationId: '0199f0a0-0000-7000-8000-0000000000a2',
      slug: 'harbor-pine',
      name: 'Harbor & Pine Landscaping',
      role: 'admin',
    },
  ],
};

/** HttpClient with the app's interceptor, answered by HttpTestingController. */
export function provideTestHttp(): (Provider | EnvironmentProviders)[] {
  return [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()];
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
