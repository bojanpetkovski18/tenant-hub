import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { loadSession, olivia, provideTestHttp } from '../../testing/session';
import { SessionStore } from './session.store';

describe('SessionStore', () => {
  let store: InstanceType<typeof SessionStore>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideTestHttp(), provideRouter([])] });
    store = TestBed.inject(SessionStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loads a signed-in user and finds memberships by slug', async () => {
    await loadSession(olivia);

    expect(store.loaded()).toBe(true);
    expect(store.isSignedIn()).toBe(true);
    expect(store.membershipBySlug('harbor-pine')?.role).toBe('admin');
    expect(store.membershipBySlug('unknown')).toBeUndefined();
  });

  it('treats a failed session load as signed out', async () => {
    const loading = store.load();
    http.expectOne('/api/auth/session').flush(null, { status: 500, statusText: 'Server Error' });
    await loading;

    expect(store.loaded()).toBe(true);
    expect(store.isSignedIn()).toBe(false);
  });

  it('signs in with the session the API returns', async () => {
    await loadSession(null);

    const signingIn = store.signIn('olivia@brightline.example', 'pw');
    const request = http.expectOne('/api/auth/sign-in');
    expect(request.request.body).toEqual({ email: 'olivia@brightline.example', password: 'pw' });
    request.flush({ user: olivia });

    expect((await signingIn).id).toBe(olivia.id);
    expect(store.user()?.email).toBe(olivia.email);
  });

  it('forgets the user on sign-out even when the request fails', async () => {
    await loadSession(olivia);

    const signingOut = store.signOut();
    http.expectOne('/api/auth/sign-out').flush(null, { status: 500, statusText: 'Server Error' });
    await expect(signingOut).rejects.toBeTruthy();

    expect(store.user()).toBeNull();
  });

  it('adds a created organization to the memberships in name order', async () => {
    await loadSession(olivia);

    const creating = store.createOrganization('Acme Repairs', 'UTC');
    http.expectOne('/api/orgs').flush({
      organizationId: 'x',
      slug: 'acme-repairs',
      name: 'Acme Repairs',
      role: 'owner',
    });
    await creating;

    expect(store.memberships().map((m) => m.slug)).toEqual([
      'acme-repairs',
      'brightline',
      'harbor-pine',
    ]);
  });
});
