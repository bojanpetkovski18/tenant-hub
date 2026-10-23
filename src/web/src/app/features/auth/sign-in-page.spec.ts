import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { loadSession, olivia, provideTestHttp, settle } from '../../testing/session';

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
