import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
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
import { Dashboard } from './dashboard.api';
import { formatDay } from './dashboard-page';

const orgId = olivia.memberships[0]!.organizationId;

const dashboard = (overrides: Partial<Dashboard> = {}): Dashboard => ({
  today: '2026-10-24',
  monthStart: '2026-10-01',
  timeZone: 'Europe/Amsterdam',
  tiles: { openJobs: 41, overdueJobs: 6, completedThisMonth: 17, activeClients: 35 },
  jobsByStatus: [
    { status: 'new', count: 12 },
    { status: 'scheduled', count: 20 },
    { status: 'inProgress', count: 9 },
    { status: 'completed', count: 100 },
    { status: 'cancelled', count: 9 },
  ],
  myNextSevenDays: [
    {
      id: '0199f0a0-0000-7000-8000-0000000000j1',
      label: 'JOB-0142',
      title: 'Window cleaning',
      clientName: 'Northgate Offices',
      scheduledOn: '2026-10-26',
      status: 'scheduled',
      isOverdue: false,
    },
  ],
  plan: 'team',
  usage: { seats: 6, activeClients: 35, openJobs: 41 },
  limits: { seats: 10, activeClients: 500, openJobs: 2000 },
  recentActivity: [
    {
      id: 9,
      occurredAt: '2026-10-24T09:00:00Z',
      actorUserId: olivia.id,
      actorName: 'Olivia Bennett',
      action: 'job.created',
      entityType: 'job',
      entityId: '0199f0a0-0000-7000-8000-0000000000j1',
      summary: 'Created JOB-0142 Window cleaning',
      changes: [],
      entityExists: true,
    },
  ],
  ...overrides,
});

const viewer: SessionUser = {
  ...olivia,
  memberships: [{ ...olivia.memberships[0]!, role: 'viewer', permissions: permissionsFor.viewer }],
};

describe('dashboard page', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  async function open(
    user: SessionUser = olivia,
    answer: Dashboard | null = dashboard(),
  ): Promise<HTMLElement> {
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
    await harness.navigateByUrl('/o/brightline/dashboard');
    const request = http.expectOne(`/api/orgs/${orgId}/dashboard`);
    if (answer) {
      request.flush(answer);
    } else {
      request.flush({ title: 'Something went wrong.' }, { status: 500, statusText: 'Error' });
    }
    await settle(harness.fixture);
    return harness.routeNativeElement as HTMLElement;
  }

  const text = (element: Element | null) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('links each tile to the list that counts the same jobs or clients', async () => {
    const page = await open();

    const tiles = [...page.querySelectorAll('[aria-label="Key figures"] a')];
    expect(tiles.map((a) => [text(a), a.getAttribute('href')])).toEqual([
      ['41 Open jobs', '/o/brightline/jobs?status=new,scheduled,inProgress'],
      ['6 Overdue jobs', '/o/brightline/jobs?overdue=true'],
      ['17 Completed this month', '/o/brightline/jobs?status=completed&completedFrom=2026-10-01'],
      ['35 Active clients', '/o/brightline/clients'],
    ]);
    expect(text(page)).toContain('Saturday 24 October in Europe/Amsterdam');
  });

  it('draws jobs by status with labelled bars on one scale, and the same numbers as a table', async () => {
    const page = await open();

    const rows = [...page.querySelectorAll('figure li a')];
    expect(rows.map((a) => text(a))).toEqual([
      'New 12',
      'Scheduled 20',
      'In progress 9',
      'Completed 100',
      'Cancelled 9',
    ]);
    expect(rows[3]!.getAttribute('href')).toBe('/o/brightline/jobs?status=completed');
    expect(rows[3]!.querySelector('[aria-hidden="true"] span')!.className).toContain('w-full');
    expect(rows[0]!.querySelector('[aria-hidden="true"] span')!.className).toContain('w-[10%]');

    const toggle = [...page.querySelectorAll('figure button')][0] as HTMLButtonElement;
    toggle.click();
    await settle(harness.fixture);

    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    const cells = [...page.querySelectorAll('figure tbody tr')].map((tr) =>
      [...tr.children].map((c) => text(c)),
    );
    expect(cells[0]).toEqual(['New', '12', '8%']);
    expect([...page.querySelectorAll('figure tfoot tr > *')].map((c) => text(c))).toEqual([
      'Total',
      '150',
      '',
    ]);
  });

  it('lists my next jobs, plan usage and, for Owners, recent activity', async () => {
    const page = await open();

    expect(text(page)).toContain('Window cleaning');
    expect(text(page)).toContain('JOB-0142 · Northgate Offices');
    expect(text(page)).toContain('Mon 26 Oct');
    expect(page.querySelectorAll('[role="meter"]').length).toBe(3);
    expect(text(page)).toContain('Recent activity');
    expect(text(page)).toContain('Created JOB-0142 Window cleaning');
  });

  it('leaves out recent activity and the plan link for a Viewer', async () => {
    const page = await open(viewer, dashboard({ recentActivity: null, myNextSevenDays: [] }));

    expect(text(page)).not.toContain('Recent activity');
    expect(page.querySelector('a[href="/o/brightline/settings#plan"]')).toBeNull();
    expect(text(page)).toContain('Nothing scheduled for you in the next 7 days.');
  });

  it('welcomes an empty organization with its first steps', async () => {
    const empty = dashboard({
      tiles: { openJobs: 0, overdueJobs: 0, completedThisMonth: 0, activeClients: 0 },
      jobsByStatus: dashboard().jobsByStatus.map((s) => ({ ...s, count: 0 })),
      myNextSevenDays: [],
      recentActivity: [],
    });
    const page = await open(olivia, empty);

    expect(text(page)).toContain('Nothing here yet');
    expect(text(page)).toContain('No jobs yet.');
    expect(page.querySelector('a[href="/o/brightline/clients/new"]')).not.toBeNull();
  });

  it('shows the error with a retry when loading fails', async () => {
    const page = await open(olivia, null);

    expect(text(page.querySelector('[role="alert"]'))).toContain('Try again');
  });

  it('formats API dates as calendar days, whatever the browser zone', () => {
    expect(formatDay('2026-11-01', { day: 'numeric', month: 'long' })).toBe('1 November');
  });
});
