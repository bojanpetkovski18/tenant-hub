import { HttpClient } from '@angular/common/http';
import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { loadSession, olivia, provideTestHttp } from '../../testing/session';
import { SessionStore } from './session.store';

describe('authInterceptor', () => {
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideTestHttp(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await loadSession(olivia);
  });

  it('clears the session and asks the user to sign in again after a 401', async () => {
    const call = firstValueFrom(TestBed.inject(HttpClient).get('/api/orgs/x'));
    http.expectOne('/api/orgs/x').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(call).rejects.toBeTruthy();
    expect(TestBed.inject(SessionStore).user()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/sign-in'], { queryParams: { returnUrl: '/' } });
  });

  it('leaves 401s from the auth endpoints to the page (wrong password is not a lost session)', async () => {
    const call = firstValueFrom(TestBed.inject(HttpClient).post('/api/auth/sign-in', {}));
    http.expectOne('/api/auth/sign-in').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(call).rejects.toBeTruthy();
    expect(TestBed.inject(SessionStore).user()).not.toBeNull();
    expect(navigate).not.toHaveBeenCalled();
  });
});
