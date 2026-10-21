import { HttpClient } from '@angular/common/http';
import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { loadSession, olivia, provideTestHttp, settle } from '../../testing/session';
import { SessionStore } from './session.store';

describe('tenantAccessInterceptor', () => {
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideTestHttp(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await loadSession(olivia);
  });

  afterEach(() => http.verify());

  const call = (url: string) => firstValueFrom(TestBed.inject(HttpClient).get(url));

  it('reloads the session and opens the picker with a notice when access to an organization is gone', async () => {
    const request = call('/api/orgs/a1/members');
    http
      .expectOne('/api/orgs/a1/members')
      .flush(
        { title: 'Organization not found.', code: 'organization-not-found' },
        { status: 404, statusText: 'Not Found' },
      );
    await expect(request).rejects.toBeTruthy();

    const withoutBrightline = { ...olivia, memberships: [olivia.memberships[1]!] };
    http.expectOne('/api/auth/session').flush({ user: withoutBrightline });
    await settle({ whenStable: () => Promise.resolve() });

    expect(TestBed.inject(SessionStore).membershipBySlug('brightline')).toBeUndefined();
    expect(navigate).toHaveBeenCalledWith(['/orgs'], { queryParams: { notice: 'access-lost' } });
  });

  it('leaves other 404s (a missing client or job) to the page', async () => {
    const request = call('/api/orgs/a1/clients/x');
    http
      .expectOne('/api/orgs/a1/clients/x')
      .flush({ title: 'Not found.' }, { status: 404, statusText: 'Not Found' });

    await expect(request).rejects.toBeTruthy();
    expect(navigate).not.toHaveBeenCalled();
  });
});
