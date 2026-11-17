import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { SessionStore } from '../../core/session/session.store';
import { answerDemo, loadSession, olivia, provideTestHttp, settle } from '../../testing/session';

describe('Account recovery pages', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function open(url: string, options: { user?: typeof olivia | null; demo?: boolean } = {}) {
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(options.user ?? null, options.demo ?? false);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    if (options.demo) {
      answerDemo(http, { resetIntervalHours: 6, nextResetAt: null, accounts: [] });
    }
    await settle(harness.fixture);
    return harness.routeNativeElement as HTMLElement;
  }

  const type = (page: HTMLElement, id: string, value: string) => {
    const input = page.querySelector<HTMLInputElement>(`#${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };

  const submit = async (page: HTMLElement) => {
    page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await settle(harness.fixture);
  };

  afterEach(() => http.verify());

  describe('Forgot password', () => {
    it('checks the address before calling the API', async () => {
      const page = await open('/forgot-password');

      await submit(page);

      expect(page.querySelector('#email-error')?.textContent).toContain(
        'Enter your email address.',
      );
      expect(document.activeElement?.id).toBe('email');
    });

    it('answers the same way whether or not the address has an account', async () => {
      const page = await open('/forgot-password');
      type(page, 'email', ' someone@test.example ');
      await submit(page);

      const request = http.expectOne('/api/auth/password-reset/request');
      expect(request.request.body).toEqual({ email: 'someone@test.example' });
      request.flush(null, { status: 202, statusText: 'Accepted' });
      await settle(harness.fixture);

      const status = page.querySelector('[role="status"]');
      expect(status?.textContent).toContain('If an account exists for someone@test.example');
      expect(status?.textContent).toContain('expires in 30 minutes');
      expect(page.querySelector('form')).toBeNull();
    });

    it('shows the rate limit as a form error', async () => {
      const page = await open('/forgot-password');
      type(page, 'email', 'someone@test.example');
      await submit(page);

      http
        .expectOne('/api/auth/password-reset/request')
        .flush({ title: 'Too many attempts.' }, { status: 429, statusText: 'Too Many Requests' });
      await settle(harness.fixture);

      expect(page.querySelector('[role="alert"]')?.textContent).toContain('Too many attempts.');
    });

    it('explains in the demo that no email is sent', async () => {
      const page = await open('/forgot-password', { demo: true });

      expect(page.textContent).toContain('This demo sends no email');
      expect(page.querySelector('form')).toBeNull();
    });
  });

  describe('Reset password', () => {
    it('says an incomplete link does not work, without calling the API', async () => {
      const page = await open('/reset-password');

      expect(page.querySelector('h1')?.textContent).toContain('This link no longer works');
      expect(page.textContent).toContain('It is incomplete');
      expect(page.querySelector('a[href="/forgot-password"]')).not.toBeNull();
    });

    it('shows the server password rules on the field, then sets the password', async () => {
      const page = await open('/reset-password#tok-123');
      type(page, 'password', 'longenough1');
      await submit(page);

      const refused = http.expectOne('/api/auth/password-reset/confirm');
      expect(refused.request.body).toEqual({ token: 'tok-123', password: 'longenough1' });
      refused.flush(
        { title: 'Invalid', errors: { password: ['Passwords must have at least one uppercase.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);
      expect(page.querySelector('#password-error')?.textContent).toContain('one uppercase');

      type(page, 'password', 'Fresh-Start-42');
      await submit(page);
      http
        .expectOne('/api/auth/password-reset/confirm')
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle(harness.fixture);

      expect(page.querySelector('h1')?.textContent).toContain('Password changed');
      expect(page.querySelector('[role="status"]')?.textContent).toContain('signed out');
      expect(page.querySelector('a[href="/sign-in"]')).not.toBeNull();
    });

    it('turns an expired or used link into a request for a new one', async () => {
      const page = await open('/reset-password#tok-old', { user: olivia });
      type(page, 'password', 'Fresh-Start-42');
      await submit(page);

      http.expectOne('/api/auth/password-reset/confirm').flush(
        { title: 'This link has expired.', code: 'reset-link-invalid' },
        {
          status: 400,
          statusText: 'Bad Request',
        },
      );
      await settle(harness.fixture);

      expect(page.querySelector('h1')?.textContent).toContain('This link no longer works');
      expect(page.textContent).toContain('expired or has already been used');
      // A failed reset changes nothing: whoever was signed in here still is.
      expect(TestBed.inject(SessionStore).user()).toEqual(olivia);
    });
  });

  describe('Verify email', () => {
    it('confirms at once and offers to sign in when nobody is signed in', async () => {
      const page = await open('/verify-email#tok-v');

      expect(page.textContent).toContain('Confirming your email address');
      const request = http.expectOne('/api/auth/verify-email');
      expect(request.request.body).toEqual({ token: 'tok-v' });
      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle(harness.fixture);

      expect(page.querySelector('h1')?.textContent).toContain('Email address confirmed');
      expect(page.querySelector('a[href="/sign-in"]')).not.toBeNull();
    });

    it('reads the session again when someone is signed in, and continues to their organization', async () => {
      const unverified = { ...olivia, emailVerified: false };
      const page = await open('/verify-email#tok-v', { user: unverified });

      http
        .expectOne('/api/auth/verify-email')
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle(harness.fixture);
      http.expectOne('/api/auth/session').flush({ user: olivia });
      await settle(harness.fixture);

      expect(TestBed.inject(SessionStore).user()?.emailVerified).toBe(true);
      expect(page.querySelector('a.th-button')?.getAttribute('href')).toBe('/orgs');
    });

    it('explains an expired link', async () => {
      const page = await open('/verify-email#tok-old');

      http
        .expectOne('/api/auth/verify-email')
        .flush(
          { title: 'Expired.', code: 'verification-link-invalid' },
          { status: 400, statusText: 'Bad Request' },
        );
      await settle(harness.fixture);

      expect(page.querySelector('h1')?.textContent).toContain('This link no longer works');
      expect(page.textContent).toContain('Sign in to ask for a new one.');
    });

    it('offers to try again when the server cannot be reached', async () => {
      const page = await open('/verify-email#tok-v');

      http.expectOne('/api/auth/verify-email').error(new ProgressEvent('error'));
      await settle(harness.fixture);
      expect(page.querySelector('[role="alert"]')?.textContent).toContain(
        'Cannot reach the server',
      );

      page.querySelector<HTMLButtonElement>('[role="alert"] button')!.click();
      await settle(harness.fixture);
      http
        .expectOne('/api/auth/verify-email')
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle(harness.fixture);

      expect(page.querySelector('h1')?.textContent).toContain('Email address confirmed');
    });
  });
});
