import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { loadSession, olivia, provideTestHttp } from '../../testing/session';

describe('auth guards', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
  });

  it('sends a signed-out visitor to sign-in with the return URL', async () => {
    await loadSession(null);
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/o/brightline/jobs');

    expect(TestBed.inject(Router).url).toBe('/sign-in?returnUrl=%2Fo%2Fbrightline%2Fjobs');
  });

  it('protects the organization picker and account pages', async () => {
    await loadSession(null);
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/account');

    expect(TestBed.inject(Router).url).toBe('/sign-in?returnUrl=%2Faccount');
  });

  it('sends a signed-in user away from sign-in to where they would land', async () => {
    await loadSession(olivia);
    localStorage.setItem('th-last-org', 'harbor-pine');
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/sign-in');

    expect(TestBed.inject(Router).url).toBe('/o/harbor-pine/dashboard');
  });
});
