import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { DemoInfo } from '../../core/demo/demo.store';
import { answerDemo, loadSession, olivia, provideTestHttp, settle } from '../../testing/session';

describe('SignInPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let page: HTMLElement;

  beforeEach(async () => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(null);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sign-in?returnUrl=%2Fo%2Fharbor-pine%2Fjobs');
    answerDemo(http);
    await settle(harness.fixture);
    page = harness.routeNativeElement as HTMLElement;
  });

  afterEach(() => http.verify());

  const input = (id: string) => page.querySelector<HTMLInputElement>(`#${id}`)!;
  const type = (id: string, value: string) => {
    input(id).value = value;
    input(id).dispatchEvent(new Event('input'));
  };
  const submit = async () => {
    page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await harness.fixture.whenStable();
  };

  it('shows field errors without calling the API and links them to the inputs', async () => {
    await submit();

    expect(page.querySelector('#email-error')?.textContent).toContain('Enter your email address.');
    expect(page.querySelector('#password-error')?.textContent).toContain('Enter your password.');
    expect(input('email').getAttribute('aria-invalid')).toBe('true');
    expect(input('email').getAttribute('aria-describedby')).toBe('email-error');
    expect(document.activeElement).toBe(input('email'));
  });

  it('shows one generic message when the credentials are refused', async () => {
    type('email', 'olivia@brightline.example');
    type('password', 'wrong-password');
    await submit();

    http
      .expectOne('/api/auth/sign-in')
      .flush({ title: 'Invalid email or password.' }, { status: 401, statusText: 'Unauthorized' });
    await settle(harness.fixture);

    expect(page.querySelector('[role="alert"]')?.textContent).toContain(
      'Invalid email or password.',
    );
  });

  it('signs in and goes to the return URL', async () => {
    type('email', 'olivia@brightline.example');
    type('password', 'Correct-Horse-9');
    await submit();

    http.expectOne('/api/auth/sign-in').flush({ user: olivia });
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/o/harbor-pine/jobs');
    // The shell checks the membership on the server as it opens the organization.
    http.expectOne(`/api/orgs/${olivia.memberships[1]!.organizationId}`);
    // The jobs page it lands on loads its list and its filter options.
    const jobsPage = http.match((r) =>
      r.url.startsWith(`/api/orgs/${olivia.memberships[1]!.organizationId}/`),
    );
    expect(jobsPage.map((r) => r.request.url.split('/').pop())).toEqual([
      'jobs',
      'members',
      'clients',
    ]);
  });
});

describe('SignInPage in demo mode', () => {
  const demo: DemoInfo = {
    resetIntervalHours: 6,
    nextResetAt: '2026-10-24T14:00:00Z',
    accounts: [
      { role: 'owner', displayName: 'Olivia Bennett', email: 'olivia@brightline.example' },
      { role: 'admin', displayName: 'Adam Clarke', email: 'adam@brightline.example' },
      { role: 'member', displayName: 'Maya Torres', email: 'maya@brightline.example' },
      { role: 'viewer', displayName: 'Victor Lindqvist', email: 'victor@brightline.example' },
    ],
  };
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let page: HTMLElement;

  beforeEach(async () => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(null);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sign-in');
    answerDemo(http, demo);
    await settle(harness.fixture);
    page = harness.routeNativeElement as HTMLElement;
  });

  afterEach(() => http.verify());

  const demoButtons = () => [...page.querySelectorAll<HTMLButtonElement>('section button')];

  it('offers one button per role, each described in one line', () => {
    expect(demoButtons().map((b) => b.textContent!.trim())).toEqual([
      'Try as Owner',
      'Try as Admin',
      'Try as Member',
      'Try as Viewer',
    ]);
    const hint = page.querySelector(`#${demoButtons()[3]!.getAttribute('aria-describedby')}`);
    expect(hint?.textContent).toContain(
      'Victor Lindqvist. Sees clients and jobs but changes nothing.',
    );
    expect(page.textContent).toContain('resets every 6 hours');
  });

  it('signs in as the role with one click and opens the organization', async () => {
    demoButtons()[2]!.click();
    await settle(harness.fixture);

    const request = http.expectOne('/api/demo/sign-in');
    expect(request.request.body).toEqual({ role: 'member' });
    request.flush({ user: { ...olivia, memberships: [olivia.memberships[0]] } });
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/o/brightline/dashboard');
    http.match(() => true).forEach((r) => r.flush({}));
  });
});
