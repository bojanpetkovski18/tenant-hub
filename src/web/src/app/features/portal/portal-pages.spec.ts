import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  Router,
  provideRouter,
  withComponentInputBinding,
  withRouterConfig,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { authInterceptor } from '../../core/session/auth.interceptor';
import { loadSession, settle } from '../../testing/session';
import { PortalSession, PortalVisit, PortalVisitDetail } from './portal.api';
import { portalSessionInterceptor } from './portal-session';

const signedOut: PortalSession = {
  organization: { name: 'Brightline Cleaning Co.', slug: 'brightline' },
  contact: null,
};
const signedIn: PortalSession = {
  ...signedOut,
  contact: { id: 'k1', name: 'Dana Reyes', email: 'dana@acme.example', clientName: 'Acme Offices' },
};

const visit = (overrides: Partial<PortalVisit> = {}): PortalVisit => ({
  id: 'v1',
  label: 'JOB-0042',
  title: 'Window clean',
  scheduledOn: '2026-11-20',
  status: 'scheduled',
  assigneeName: 'Maya Torres',
  ...overrides,
});

describe('portal pages', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function open(url: string, session: PortalSession, demoMode = false): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor, portalSessionInterceptor])),
        provideHttpClientTesting(),
        provideRouter(
          routes,
          withComponentInputBinding(),
          withRouterConfig({ paramsInheritanceStrategy: 'always' }),
        ),
        provideCspSafeCdkStyles(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(null, demoMode);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(url);
    await answerSession(session);
    await navigation;
    await answerSession(session);
    return harness.routeNativeElement as HTMLElement;
  }

  /** The layout and the pages ask for the session; answer every such request. */
  async function answerSession(session: PortalSession): Promise<void> {
    for (let i = 0; i < 3; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      http.match((r) => r.url === '/api/portal/session').forEach((r) => r.flush(session));
    }
    await settle(harness.fixture);
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const main = (element: HTMLElement) => element.querySelector('main') ?? element;

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  describe('sign-in', () => {
    it('asks for a link and says the same whatever the address', async () => {
      const page = await open('/portal/brightline/sign-in', signedOut);
      expect(text(main(page).querySelector('h1'))).toBe('Sign in to Brightline Cleaning Co.');

      const email = page.querySelector<HTMLInputElement>('#portal-email')!;
      email.value = 'dana@acme.example';
      email.dispatchEvent(new Event('input'));
      page.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);
      const request = http.expectOne('/api/portal/sign-in/request');
      expect(request.request.body).toEqual({ orgSlug: 'brightline', email: 'dana@acme.example' });
      request.flush(null, { status: 202, statusText: 'Accepted' });
      await settle(harness.fixture);

      expect(text(main(page).querySelector('[role="status"]'))).toContain(
        'If dana@acme.example belongs to a contact of Brightline Cleaning Co., a sign-in link is on its way. It works once, within 15 minutes.',
      );
    });

    it('refuses an empty address without asking the API', async () => {
      const page = await open('/portal/brightline/sign-in', signedOut);

      page.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);

      http.expectNone('/api/portal/sign-in/request');
      expect(text(page.querySelector('#portal-email-error'))).toBe('Enter your email address.');
    });

    it('signs in with the link in the address and opens the visits', async () => {
      await open('/portal/brightline/sign-in#a-token-from-the-email', signedOut);

      const confirm = http.expectOne('/api/portal/sign-in/confirm');
      expect(confirm.request.body).toEqual({
        orgSlug: 'brightline',
        token: 'a-token-from-the-email',
      });
      confirm.flush(signedIn);
      await settle(harness.fixture);
      http
        .match((r) => r.url === '/api/portal/visits')
        .forEach((r) => r.flush({ items: [], page: 1, pageSize: 20, total: 0 }));
      await settle(harness.fixture);

      expect(TestBed.inject(Router).url).toBe('/portal/brightline/visits');
    });

    // Regression (found in the browser pass): the layout asks for the session as the link opens; when that "signed out"
    // answer arrived after the link was spent, it overwrote the new session and the guard sent the contact back here.
    it('keeps the new session when an older session answer arrives late', async () => {
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(withInterceptors([authInterceptor, portalSessionInterceptor])),
          provideHttpClientTesting(),
          provideRouter(
            routes,
            withComponentInputBinding(),
            withRouterConfig({ paramsInheritanceStrategy: 'always' }),
          ),
          provideCspSafeCdkStyles(),
        ],
      });
      http = TestBed.inject(HttpTestingController);
      await loadSession(null);
      harness = await RouterTestingHarness.create();
      await harness.navigateByUrl('/portal/brightline/sign-in#a-token-from-the-email');
      await settle(harness.fixture);

      const stale = http.match((r) => r.url === '/api/portal/session');
      http.expectOne('/api/portal/sign-in/confirm').flush(signedIn);
      await settle(harness.fixture);
      stale.forEach((r) => r.flush(signedOut));
      await settle(harness.fixture);
      http
        .match((r) => r.url === '/api/portal/visits')
        .forEach((r) => r.flush({ items: [], page: 1, pageSize: 20, total: 0 }));
      await settle(harness.fixture);

      expect(stale.length).toBeGreaterThan(0);
      expect(TestBed.inject(Router).url).toBe('/portal/brightline/visits');
      expect(text(harness.routeNativeElement?.closest('body')?.querySelector('header'))).toContain(
        'Dana Reyes',
      );
    });

    // Regression (found in the browser pass): with the sign-in page already open, following the link changes only the
    // fragment, a same-document navigation, and the page never read the token.
    it('spends a link opened over the sign-in page that is already showing', async () => {
      await open('/portal/brightline/sign-in', signedOut);

      await harness.navigateByUrl('/portal/brightline/sign-in#a-token-pasted-later');
      await settle(harness.fixture);

      const confirm = http.expectOne('/api/portal/sign-in/confirm');
      expect(confirm.request.body).toEqual({
        orgSlug: 'brightline',
        token: 'a-token-pasted-later',
      });
    });

    it('says when a link has expired and offers a new one', async () => {
      const page = await open('/portal/brightline/sign-in#an-old-token', signedOut);

      http
        .expectOne('/api/portal/sign-in/confirm')
        .flush(
          { title: 'This sign-in link has expired.', code: 'portal-link-invalid' },
          { status: 400, statusText: 'Bad Request' },
        );
      await answerSession(signedOut);

      expect(text(page.querySelector('[role="alert"]'))).toContain(
        'This sign-in link has expired or has already been used.',
      );
      expect(page.querySelector('#portal-email')).not.toBeNull();
    });

    it('explains a portal that does not exist or is off', async () => {
      const unknown = await open('/portal/nowhere/sign-in', { organization: null, contact: null });
      expect(text(main(unknown).querySelector('h1'))).toBe('This portal is not available');
    });

    it('says the session ended when sent back here', async () => {
      const page = await open('/portal/brightline/sign-in?notice=ended', signedOut);

      expect(text(page.querySelector('.th-alert-info'))).toBe(
        'Your portal session has ended. Sign in again to continue.',
      );
    });

    it('offers the seeded clients in the public demo', async () => {
      const page = await open('/portal/brightline/sign-in', signedOut, true);
      http.expectOne('/api/demo/accounts').flush({
        resetIntervalHours: 6,
        nextResetAt: null,
        accounts: [],
        portalSlug: 'brightline',
        portalContacts: [
          { name: 'Dana Reyes', email: 'dana@acme.example', clientName: 'Acme Offices' },
        ],
      });
      await settle(harness.fixture);

      const button = [...page.querySelectorAll('button')].find((b) =>
        text(b).startsWith('Dana Reyes'),
      )!;
      button.click();
      await settle(harness.fixture);
      const signIn = http.expectOne('/api/portal/demo/sign-in');
      expect(signIn.request.body).toEqual({ email: 'dana@acme.example' });
    });
  });

  describe('signed in', () => {
    it('lists upcoming visits, switches to past ones through the address, and shows who is coming', async () => {
      const page = await open('/portal/brightline/visits', signedIn);

      const upcoming = http.expectOne((r) => r.url === '/api/portal/visits');
      expect(upcoming.request.params.get('when')).toBe('upcoming');
      upcoming.flush({ items: [visit()], page: 1, pageSize: 20, total: 1 });
      await settle(harness.fixture);
      expect(text(page.querySelector('ul li'))).toContain('Window clean');
      expect(text(page.querySelector('ul li'))).toContain('Maya Torres');
      expect(text(page.querySelector('header'))).toContain('Dana Reyes, Acme Offices');

      await harness.navigateByUrl('/portal/brightline/visits?when=past');
      const past = http.expectOne((r) => r.url === '/api/portal/visits');
      expect(past.request.params.get('when')).toBe('past');
      past.flush({ items: [], page: 1, pageSize: 20, total: 0 });
      await settle(harness.fixture);
      expect(text(main(page))).toContain('No past visits');
    });

    it('sends the contact to sign in again when the API ends the session', async () => {
      await open('/portal/brightline/visits', signedIn);

      http
        .expectOne((r) => r.url === '/api/portal/visits')
        .flush(
          { title: 'Your portal session has ended.', code: 'portal-session-ended' },
          { status: 401, statusText: 'Unauthorized' },
        );
      await answerSession(signedOut);

      expect(TestBed.inject(Router).url).toBe('/portal/brightline/sign-in?notice=ended');
    });

    // M22 audit: the error state had no heading, so the page had none (one h1 per screen).
    it('keeps a heading when a visit cannot be loaded, and offers to try again', async () => {
      const page = await open('/portal/brightline/visits/v1', signedIn);
      http
        .expectOne('/api/portal/visits/v1')
        .flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });
      await settle(harness.fixture);

      expect([...main(page).querySelectorAll('h1')].map((h) => text(h))).toEqual(['Visit']);
      expect(text(main(page).querySelector('[role="alert"] button'))).toBe('Try again');
    });

    describe('a completed visit (M19)', () => {
      const completed = (overrides: Partial<PortalVisitDetail> = {}): PortalVisitDetail => ({
        ...visit({ status: 'completed' }),
        description: null,
        dueOn: null,
        completedAt: '2026-11-18T15:00:00Z',
        checklist: [
          { text: 'Vacuum all floors', required: true, done: true },
          { text: 'Water the plants', required: false, done: false },
        ],
        photos: [{ id: 'p1', width: 1600, height: 1200 }],
        feedback: null,
        canGiveFeedback: true,
        ...overrides,
      });

      it('shows what was done and the photos, and confirms the visit after asking', async () => {
        const page = await open('/portal/brightline/visits/v1', signedIn);
        http.expectOne('/api/portal/visits/v1').flush(completed());
        await settle(harness.fixture);

        expect(text(main(page))).toContain('Vacuum all floors (done)');
        expect(text(main(page))).toContain('Water the plants (not done)');
        expect(page.querySelector('img')?.getAttribute('src')).toBe(
          '/api/portal/visits/v1/photos/p1/thumbnail',
        );

        [...page.querySelectorAll('button')].find((b) => text(b) === 'Looks good')!.click();
        await settle(harness.fixture);
        [...document.querySelectorAll('[role="alertdialog"] button')]
          .find((b) => text(b) === 'Looks good')!
          .dispatchEvent(new MouseEvent('click'));
        await settle(harness.fixture);
        http.expectOne('/api/portal/visits/v1/confirm').flush(
          completed({
            canGiveFeedback: false,
            feedback: {
              kind: 'confirmed',
              text: null,
              at: '2026-11-19T09:00:00Z',
              byName: 'Dana Reyes',
            },
          }),
        );
        await settle(harness.fixture);

        expect(text(main(page).querySelector('[role="status"]'))).toContain(
          'Dana Reyes confirmed this visit',
        );
        expect([...page.querySelectorAll('button')].some((b) => text(b) === 'Looks good')).toBe(
          false,
        );
      });

      it('asks for the problem in words before sending it', async () => {
        const page = await open('/portal/brightline/visits/v1', signedIn);
        http.expectOne('/api/portal/visits/v1').flush(completed());
        await settle(harness.fixture);

        [...page.querySelectorAll('button')].find((b) => text(b) === 'Report a problem')!.click();
        await settle(harness.fixture);
        expect(document.activeElement?.id).toBe('problem');
        page.querySelector('form')!.dispatchEvent(new Event('submit'));
        await settle(harness.fixture);
        expect(text(page.querySelector('#problem-error'))).toContain('Describe the problem');
        http.expectNone('/api/portal/visits/v1/report-problem');

        const field = page.querySelector<HTMLTextAreaElement>('#problem')!;
        field.value = '  The hallway was missed  ';
        field.dispatchEvent(new Event('input'));
        page.querySelector('form')!.dispatchEvent(new Event('submit'));
        await settle(harness.fixture);
        const sent = http.expectOne('/api/portal/visits/v1/report-problem');
        expect(sent.request.body).toEqual({ text: 'The hallway was missed' });
      });

      it('shows no sign-off before the visit is completed', async () => {
        const page = await open('/portal/brightline/visits/v1', signedIn);
        http.expectOne('/api/portal/visits/v1').flush(
          completed({
            status: 'scheduled',
            completedAt: null,
            checklist: [],
            photos: [],
            canGiveFeedback: false,
          }),
        );
        await settle(harness.fixture);

        expect(text(main(page))).not.toContain('Your sign-off');
        expect(page.querySelector('img')).toBeNull();
      });
    });

    it('approves a quote with an optional comment', async () => {
      const page = await open('/portal/brightline/quotes', signedIn);
      http.expectOne('/api/portal/quotes').flush([
        {
          jobId: 'j1',
          label: 'JOB-0042',
          title: 'Window clean',
          scheduledOn: '2026-11-20',
          amount: 180,
          status: 'pending',
          sentAt: '2026-11-18T09:00:00Z',
          decidedAt: null,
          decidedByName: null,
          comment: null,
        },
      ]);
      await settle(harness.fixture);

      [...page.querySelectorAll('button')].find((b) => text(b).startsWith('Approve'))!.click();
      await settle(harness.fixture);
      const comment = document.querySelector<HTMLTextAreaElement>('#reason')!;
      comment.value = 'Side entrance please';
      comment.dispatchEvent(new Event('input'));
      document.querySelector('app-reason-dialog form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);

      const decision = http.expectOne('/api/portal/quotes/j1/approve');
      expect(decision.request.body).toEqual({ comment: 'Side entrance please' });
    });

    it('checks a request before sending it and comes back to the list', async () => {
      const page = await open('/portal/brightline/requests/new', signedIn);

      page.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);
      http.expectNone('/api/portal/requests');
      expect(text(page.querySelector('#request-title-error'))).toBe(
        'Describe the work in a few words.',
      );

      const title = page.querySelector<HTMLInputElement>('#request-title')!;
      title.value = 'Clean the stairwell';
      title.dispatchEvent(new Event('input'));
      page.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);
      const sent = http.expectOne((r) => r.url === '/api/portal/requests' && r.method === 'POST');
      expect(sent.request.body).toEqual({
        title: 'Clean the stairwell',
        description: null,
        preferredOn: null,
      });
      sent.flush({});
      await settle(harness.fixture);

      expect(TestBed.inject(Router).url).toBe('/portal/brightline/requests?notice=sent');
    });

    it('keeps a signed-out visitor out of the signed-in pages', async () => {
      await open('/portal/brightline/quotes', signedOut);

      expect(TestBed.inject(Router).url).toBe('/portal/brightline/sign-in');
      http.expectNone('/api/portal/quotes');
    });
  });
});
