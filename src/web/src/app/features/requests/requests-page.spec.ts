import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { loadSession, olivia, provideTestHttp, settle } from '../../testing/session';
import { WorkRequest } from './requests.api';

const org = olivia.memberships[0]!.organizationId;
const requestsUrl = `/api/orgs/${org}/requests/`;

const request = (overrides: Partial<WorkRequest> = {}): WorkRequest => ({
  id: 'r1',
  clientId: 'c1',
  clientName: 'Acme Offices',
  contactName: 'Dana Reyes',
  title: 'Clean the stairwell',
  description: 'All four floors',
  preferredOn: '2026-11-25',
  status: 'open',
  createdAt: '2026-11-18T09:00:00Z',
  jobId: null,
  jobLabel: null,
  declineReason: null,
  ...overrides,
});

describe('requests inbox', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function open(url: string): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideTestHttp(),
        provideRouter(
          routes,
          withComponentInputBinding(),
          withRouterConfig({ paramsInheritanceStrategy: 'always' }),
        ),
        provideCspSafeCdkStyles(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(olivia);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    http
      .match(`/api/orgs/${org}`)
      .forEach((r) =>
        r.flush({ id: org, name: 'Brightline', slug: 'brightline', plan: 'team', timeZone: 'UTC' }),
      );
    http.match(`/api/orgs/${org}/requests/open-count`).forEach((r) => r.flush({ open: 1 }));
    await settle(harness.fixture);
    return harness.routeNativeElement as HTMLElement;
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const main = (element: HTMLElement) => element.querySelector('main') ?? element;

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('shows open requests by default with a count in the navigation, and converts through the prefilled job form', async () => {
    const element = await open('/o/brightline/requests');

    const list = http.expectOne((r) => r.url === requestsUrl);
    expect(list.request.params.get('status')).toBe('open');
    list.flush({ items: [request()], page: 1, pageSize: 20, total: 1 });
    await settle(harness.fixture);

    expect(text(element.querySelector('aside nav a[href="/o/brightline/requests"]'))).toBe(
      'Requests 1 open',
    );
    const convert = main(element).querySelector<HTMLAnchorElement>('a.th-button')!;
    const target = new URL(convert.href);
    expect(target.pathname).toBe('/o/brightline/jobs/new');
    expect(Object.fromEntries(target.searchParams)).toEqual({
      fromRequest: 'r1',
      clientId: 'c1',
      title: 'Clean the stairwell',
      description: 'All four floors',
      scheduledOn: '2026-11-25',
    });
  });

  it('declines with a reason the client will read, then reloads', async () => {
    const element = await open('/o/brightline/requests');
    http
      .expectOne((r) => r.url === requestsUrl)
      .flush({ items: [request()], page: 1, pageSize: 20, total: 1 });
    await settle(harness.fixture);

    [...main(element).querySelectorAll('button')]
      .find((b) => text(b).startsWith('Decline'))!
      .click();
    await settle(harness.fixture);
    document.querySelector('app-reason-dialog form')!.dispatchEvent(new Event('submit'));
    await settle(harness.fixture);
    expect(text(document.querySelector('#reason-error'))).toBe('Say why; the client reads it.');
    const reason = document.querySelector<HTMLTextAreaElement>('#reason')!;
    reason.value = 'Not a service we offer';
    reason.dispatchEvent(new Event('input'));
    document.querySelector('app-reason-dialog form')!.dispatchEvent(new Event('submit'));
    await settle(harness.fixture);

    const declined = http.expectOne(`/api/orgs/${org}/requests/r1/decline`);
    expect(declined.request.body).toEqual({ reason: 'Not a service we offer' });
    declined.flush(request({ status: 'declined', declineReason: 'Not a service we offer' }));
    await settle(harness.fixture);
    // The sidebar count is asked again after the decision.
    const counts = http.match(`/api/orgs/${org}/requests/open-count`);
    expect(counts.length).toBeGreaterThan(0);
    counts.forEach((r) => r.flush({ open: 0 }));
    http
      .expectOne((r) => r.url === requestsUrl)
      .flush({ items: [], page: 1, pageSize: 20, total: 0 });
    await settle(harness.fixture);

    expect(text(main(element))).toContain('No open requests');
  });

  it('says which plan has the inbox instead of offering to try again', async () => {
    const element = await open('/o/brightline/requests');

    http
      .expectOne((r) => r.url === requestsUrl)
      .flush(
        {
          title:
            'The client portal is part of the Team plan. An Owner can change the plan in Settings.',
          code: 'plan-feature-unavailable',
          feature: 'portal',
          plan: 'team',
        },
        { status: 403, statusText: 'Forbidden' },
      );
    await settle(harness.fixture);

    expect(text(main(element))).toContain('The client portal is part of the Team plan.');
    expect(main(element).querySelector('a[href="/o/brightline/settings#plan"]')).not.toBeNull();
    expect([...main(element).querySelectorAll('button')].some((b) => text(b) === 'Try again')).toBe(
      false,
    );
  });

  it('keeps the status filter in the address', async () => {
    await open('/o/brightline/requests?status=declined');

    expect(http.expectOne((r) => r.url === requestsUrl).request.params.get('status')).toBe(
      'declined',
    );
  });
});
