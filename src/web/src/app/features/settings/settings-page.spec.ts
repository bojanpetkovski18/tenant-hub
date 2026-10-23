import { HttpTestingController } from '@angular/common/http/testing';
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
import { SessionUser } from '../../core/session/session.models';
import {
  loadSession,
  olivia,
  permissionsFor,
  provideTestHttp,
  settle,
} from '../../testing/session';
import { blockingText, usageText } from './settings-page';
import { OrganizationOverview } from './settings.api';

const orgId = olivia.memberships[0]!.organizationId;
const orgUrl = `/api/orgs/${orgId}`;

const overview = (overrides: Partial<OrganizationOverview> = {}): OrganizationOverview => ({
  id: orgId,
  name: 'Brightline Cleaning Co.',
  slug: 'brightline',
  plan: 'free',
  timeZone: 'Europe/Amsterdam',
  createdAt: '2026-10-01T09:00:00Z',
  usage: { seats: 3, activeClients: 20, openJobs: 12 },
  limits: { seats: 3, activeClients: 25, openJobs: 50 },
  plans: [
    { plan: 'free', limits: { seats: 3, activeClients: 25, openJobs: 50 } },
    { plan: 'team', limits: { seats: 10, activeClients: 500, openJobs: 2000 } },
    { plan: 'business', limits: { seats: 50, activeClients: null, openJobs: null } },
  ],
  ...overrides,
});

const admin: SessionUser = {
  ...olivia,
  memberships: [{ ...olivia.memberships[0]!, role: 'admin', permissions: permissionsFor.admin }],
};

describe('settings texts', () => {
  it('reads usage and blocking limits like people say them', () => {
    expect(usageText('seats', 3, 3)).toBe('3 of 3 seats');
    expect(usageText('activeClients', 1, 25)).toBe('1 of 25 active clients');
    expect(usageText('openJobs', 12, null)).toBe('12 open jobs, no limit');
    expect(blockingText({ limit: 'seats', allowed: 3, usage: 4 }, 'Free')).toBe(
      'Seats: 4 in use, Free allows 3',
    );
  });
});

describe('settings page', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  async function open(user: SessionUser = olivia, answer = overview()): Promise<HTMLElement> {
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
    await loadSession(user);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/o/brightline/settings');
    // The shell loads the organization too; the page asks for its own copy with usage.
    http.match(orgUrl).forEach((r) => r.flush(answer));
    await settle(harness.fixture);
    return harness.routeNativeElement as HTMLElement;
  }

  const text = (element: Element | null) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const main = (element: HTMLElement) => element.querySelector('main') ?? element;
  const buttons = (element: HTMLElement) =>
    [...main(element).querySelectorAll('button')].map((b) => text(b));

  it('shows an Owner the meters, every other plan to switch to, and the danger zone', async () => {
    const element = await open();

    const meters = [...main(element).querySelectorAll('[role="meter"]')];
    expect(meters.map((m) => m.getAttribute('aria-valuetext'))).toEqual([
      '3 of 3 seats',
      '20 of 25 active clients',
      '12 of 50 open jobs',
    ]);
    expect(meters[0]!.firstElementChild!.className).toContain('bg-danger');
    expect(meters[0]!.firstElementChild!.className).toContain('w-full');
    expect(meters[1]!.firstElementChild!.className).toContain('bg-warning');
    expect(buttons(element)).toEqual([
      'Save changes',
      'Switch to Team',
      'Switch to Business',
      'Delete organization',
    ]);
    expect(text(main(element))).toContain('Unlimited open jobs');
  });

  it('shows an Admin the general settings and usage, but no plan switch or danger zone', async () => {
    const element = await open(admin);

    expect(buttons(element)).toEqual(['Save changes']);
    expect(text(main(element))).toContain('Only Owners change the plan.');
    expect(main(element).querySelector('#danger-heading')).toBeNull();
  });

  it('lists every limit that blocks a downgrade', async () => {
    const element = await open(
      olivia,
      overview({ plan: 'team', limits: { seats: 10, activeClients: 500, openJobs: 2000 } }),
    );

    [...main(element).querySelectorAll('button')]
      .find((b) => text(b) === 'Switch to Free')!
      .click();
    await settle(harness.fixture);
    [...document.querySelectorAll('button')]
      .find((b) => text(b) === 'Switch to Free' && b.closest('app-confirm-dialog'))!
      .click();
    await settle(harness.fixture);
    http.expectOne(`${orgUrl}/plan`).flush(
      {
        title: 'Your organization uses more than the Free plan allows.',
        code: 'plan-downgrade-blocked',
        plan: 'free',
        blocking: [
          { limit: 'seats', allowed: 3, usage: 4 },
          { limit: 'activeClients', allowed: 25, usage: 26 },
        ],
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);

    const alert = main(element).querySelector('#plan [role="alert"]')!;
    expect(text(alert.querySelector('p'))).toBe('You cannot move to Free yet.');
    expect([...alert.querySelectorAll('li')].map((li) => text(li))).toEqual([
      'Seats: 4 in use, Free allows 3',
      'Active clients: 26 in use, Free allows 25',
    ]);
  });

  it('deletes only after the exact name is typed, then returns to the picker', async () => {
    const element = await open();
    const deleteButton = () =>
      [...main(element).querySelectorAll('button')].find((b) => text(b) === 'Delete organization')!;
    const input = main(element).querySelector('#confirm-name') as HTMLInputElement;

    input.value = 'brightline cleaning co.';
    input.dispatchEvent(new Event('input'));
    await settle(harness.fixture);
    expect(deleteButton().disabled).toBe(true);

    input.value = ' Brightline   Cleaning Co. ';
    input.dispatchEvent(new Event('input'));
    await settle(harness.fixture);
    expect(deleteButton().disabled).toBe(false);

    deleteButton().click();
    await settle(harness.fixture);
    [...document.querySelectorAll('button')].find((b) => text(b) === 'Delete for good')!.click();
    await settle(harness.fixture);
    const request = http.expectOne((r) => r.url === orgUrl && r.method === 'DELETE');
    expect(request.request.body).toEqual({ confirmName: ' Brightline   Cleaning Co. ' });
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle(harness.fixture);
    http
      .expectOne('/api/auth/session')
      .flush({ user: { ...olivia, memberships: olivia.memberships.slice(1) } });
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/orgs?notice=deleted');
  });
});

describe('limit reached message', () => {
  it('shows the server’s sentence and the plan link to an Owner, and asks others to contact one', async () => {
    for (const [user, link] of [
      [olivia, true],
      [admin, false],
    ] as const) {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideTestHttp(),
          provideRouter(
            routes,
            withComponentInputBinding(),
            withRouterConfig({ paramsInheritanceStrategy: 'always' }),
          ),
        ],
      });
      const http = TestBed.inject(HttpTestingController);
      await loadSession(user);
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl('/o/brightline/clients/new');
      const element = harness.routeNativeElement as HTMLElement;
      await settle(harness.fixture);
      const name = element.querySelector('#name') as HTMLInputElement;
      name.value = 'One Too Many';
      name.dispatchEvent(new Event('input'));
      (element.querySelector('main form') as HTMLFormElement).dispatchEvent(new Event('submit'));
      await settle(harness.fixture);
      http
        .expectOne((r) => r.url === `${orgUrl}/clients` && r.method === 'POST')
        .flush(
          {
            title: 'Your Free plan allows 25 active clients, and 25 are in use.',
            code: 'plan-limit-reached',
            plan: 'free',
            limit: 'activeClients',
            allowed: 25,
            usage: 25,
          },
          { status: 403, statusText: 'Forbidden' },
        );
      await settle(harness.fixture);

      const alert = element.querySelector('app-limit-reached [role="alert"]')!;
      expect(alert.textContent).toContain(
        'Your Free plan allows 25 active clients, and 25 are in use.',
      );
      expect(alert.querySelector('a[href="/o/brightline/settings#plan"]') !== null).toBe(link);
      if (!link) {
        expect(alert.textContent).toContain('Ask an Owner');
      }
    }
  });
});
