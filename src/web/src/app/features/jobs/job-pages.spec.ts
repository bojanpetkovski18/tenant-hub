import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
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
import { Job, JobListItem } from './jobs.api';

const orgId = olivia.memberships[0]!.organizationId;
const jobsUrl = `/api/orgs/${orgId}/jobs`;

const item = (overrides: Partial<JobListItem> = {}): JobListItem => ({
  id: 'j1',
  number: 42,
  label: 'JOB-0042',
  title: 'Deep clean, ground floor',
  clientId: 'c1',
  clientName: 'Acme Offices',
  assigneeUserId: 'u3',
  assigneeName: 'Maya Torres',
  status: 'scheduled',
  priority: 'high',
  scheduledOn: '2026-10-20',
  dueOn: '2026-10-21',
  isOverdue: true,
  updatedAt: '2026-10-22T09:00:00Z',
  ...overrides,
});

const job = (overrides: Partial<Job> = {}): Job => ({
  id: 'j1',
  number: 42,
  label: 'JOB-0042',
  title: 'Deep clean, ground floor',
  description: null,
  clientId: 'c1',
  clientName: 'Acme Offices',
  assigneeUserId: 'u3',
  assigneeName: 'Maya Torres',
  status: 'scheduled',
  priority: 'normal',
  scheduledOn: '2026-10-20',
  dueOn: null,
  quotedAmount: 240.5,
  isOverdue: false,
  createdAt: '2026-10-01T09:00:00Z',
  createdByName: 'Olivia Bennett',
  updatedAt: '2026-10-22T09:00:00Z',
  completedAt: null,
  version: 7,
  canEdit: true,
  canAssign: true,
  canDelete: false,
  transitions: [
    { to: 'inProgress', needsReason: false, blockedReason: null },
    { to: 'cancelled', needsReason: true, blockedReason: null },
  ],
  ...overrides,
});

const member: SessionUser = {
  ...olivia,
  memberships: [
    {
      ...olivia.memberships[0]!,
      role: 'member',
      permissions: permissionsFor.member,
      grantableRoles: [],
      invitableRoles: [],
    },
  ],
};

describe('job pages', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  async function open(url: string, user: SessionUser = olivia): Promise<HTMLElement> {
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
    await harness.navigateByUrl(url);
    return harness.routeNativeElement as HTMLElement;
  }

  /** The list request (pickers' members and clients answered empty). */
  function listRequest(): TestRequest {
    http
      .match((r) => r.url.endsWith('/members') || r.url.endsWith('/clients'))
      .forEach((r) =>
        r.request.url.endsWith('/members')
          ? r.flush([])
          : r.flush({ items: [], page: 1, pageSize: 100, total: 0 }),
      );
    return http.expectOne((r) => r.url === jobsUrl);
  }

  const text = (element: Element | null) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const main = (element: HTMLElement) => element.querySelector('main') ?? element;

  describe('list', () => {
    it('sends the URL filters to the API, as repeated parameters', async () => {
      await open(
        '/o/brightline/jobs?status=new,scheduled&priority=urgent&assignee=me&overdue=true&sort=due&page=2&q=window',
      );

      const request = listRequest();
      const params = request.request.params;
      expect(params.getAll('status')).toEqual(['new', 'scheduled']);
      expect(params.getAll('priority')).toEqual(['urgent']);
      expect([
        params.get('assignee'),
        params.get('overdue'),
        params.get('sort'),
        params.get('page'),
        params.get('q'),
      ]).toEqual(['me', 'true', 'due', '2', 'window']);
      request.flush({ items: [item()], page: 2, pageSize: 25, total: 26 });
      await settle(harness.fixture);
    });

    it('shows each row with its status, priority and overdue marker in text', async () => {
      const element = await open('/o/brightline/jobs');
      listRequest().flush({ items: [item()], page: 1, pageSize: 25, total: 1 });
      await settle(harness.fixture);

      const row = text(main(element).querySelector('tbody tr'));
      expect(row).toContain('JOB-0042');
      expect(row).toContain('Deep clean, ground floor');
      expect(row).toContain('Acme Offices');
      expect(row).toContain('Maya Torres');
      expect(row).toContain('Scheduled');
      expect(row).toContain('High');
      expect(row).toContain('Overdue');
    });

    it('puts a ticked status and "My jobs" into the URL and back to page 1', async () => {
      const element = await open('/o/brightline/jobs?page=3');
      listRequest().flush({ items: [item()], page: 3, pageSize: 25, total: 80 });
      await settle(harness.fixture);
      const router = TestBed.inject(Router);

      const inProgress = [
        ...main(element).querySelectorAll<HTMLLabelElement>('#job-filters label'),
      ].find((l) => text(l) === 'In progress')!;
      inProgress.querySelector('input')!.click();
      await settle(harness.fixture);
      expect(router.url).toBe('/o/brightline/jobs?status=inProgress');
      listRequest().flush({ items: [], page: 1, pageSize: 25, total: 0 });
      await settle(harness.fixture);

      const myJobs = [...main(element).querySelectorAll('button')].find(
        (b) => text(b) === 'My jobs',
      )!;
      myJobs.click();
      await settle(harness.fixture);
      expect(router.url).toBe('/o/brightline/jobs?status=inProgress&assignee=me');
      listRequest().flush({ items: [], page: 1, pageSize: 25, total: 0 });
      await settle(harness.fixture);
      expect(myJobs.getAttribute('aria-pressed')).toBe('true');
      expect(text(main(element))).toContain('No jobs match');
    });

    it('offers "New job" only with jobs.create', async () => {
      let element = await open('/o/brightline/jobs');
      listRequest().flush({ items: [item()], page: 1, pageSize: 25, total: 1 });
      await settle(harness.fixture);
      expect(text(main(element))).toContain('New job');

      TestBed.resetTestingModule();
      const viewer: SessionUser = {
        ...olivia,
        memberships: [
          { ...olivia.memberships[0]!, role: 'viewer', permissions: permissionsFor.viewer },
        ],
      };
      element = await open('/o/brightline/jobs', viewer);
      listRequest().flush({ items: [item()], page: 1, pageSize: 25, total: 1 });
      await settle(harness.fixture);
      expect([...main(element).querySelectorAll('a')].some((a) => text(a) === 'New job')).toBe(
        false,
      );
    });
  });

  describe('detail', () => {
    const buttons = (element: HTMLElement) =>
      [...main(element).querySelectorAll('section[aria-labelledby="actions-heading"] button')].map(
        (b) => text(b),
      );

    async function openJob(answer: Job, user: SessionUser = olivia): Promise<HTMLElement> {
      const element = await open('/o/brightline/jobs/j1', user);
      http.expectOne(`${jobsUrl}/j1`).flush(answer);
      await settle(harness.fixture);
      http.expectOne(`${jobsUrl}/j1/history`).flush([]);
      await settle(harness.fixture);
      return element;
    }

    it('shows exactly the moves the API allows, labelled for people', async () => {
      const element = await openJob(job());

      expect(buttons(element)).toEqual(['Start', 'Cancel job']);
      expect(text(main(element))).toContain('Edit');
    });

    it('shows a member nothing on someone else’s job', async () => {
      const element = await openJob(
        job({ transitions: [], canEdit: false, canAssign: false, canDelete: false }),
        member,
      );

      expect(main(element).querySelector('section[aria-labelledby="actions-heading"]')).toBeNull();
      expect([...main(element).querySelectorAll('a')].some((a) => text(a) === 'Edit')).toBe(false);
    });

    it('disables a move that still needs something and says what', async () => {
      const element = await openJob(
        job({
          status: 'new',
          assigneeUserId: null,
          assigneeName: null,
          transitions: [
            {
              to: 'scheduled',
              needsReason: false,
              blockedReason: 'Assign the job to someone first.',
            },
            { to: 'cancelled', needsReason: true, blockedReason: null },
          ],
        }),
      );

      const schedule = [...main(element).querySelectorAll('button')].find(
        (b) => text(b) === 'Schedule',
      )!;
      expect(schedule.disabled).toBe(true);
      expect(
        text(element.querySelector(`#${schedule.getAttribute('aria-describedby')}`)),
      ).toContain('Assign the job to someone first.');
    });

    it('asks for a reason before cancelling and sends it', async () => {
      const element = await openJob(job());

      [...main(element).querySelectorAll('button')].find((b) => text(b) === 'Cancel job')!.click();
      await settle(harness.fixture);
      expect(text(document.querySelector('#reason-title'))).toBe('Cancel JOB-0042?');
      const reason = document.querySelector('#reason') as HTMLTextAreaElement;
      reason.value = 'Client moved away';
      reason.dispatchEvent(new Event('input'));
      (document.querySelector('app-reason-dialog form') as HTMLFormElement).dispatchEvent(
        new Event('submit'),
      );
      await settle(harness.fixture);

      const request = http.expectOne(`${jobsUrl}/j1/transitions`);
      expect(request.request.body).toEqual({ to: 'cancelled', reason: 'Client moved away' });
      request.flush(job({ status: 'cancelled', transitions: [], canDelete: true }));
      await settle(harness.fixture);
      http.expectOne(`${jobsUrl}/j1/history`).flush([]);
      await settle(harness.fixture);
      expect(text(main(element).querySelector('[role="status"]'))).toBe(
        'JOB-0042 is now Cancelled.',
      );
    });

    it('shows the history with each change', async () => {
      const element = await open('/o/brightline/jobs/j1');
      http.expectOne(`${jobsUrl}/j1`).flush(job());
      await settle(harness.fixture);
      http.expectOne(`${jobsUrl}/j1/history`).flush([
        {
          id: 2,
          occurredAt: '2026-10-22T10:00:00Z',
          actorName: 'Olivia Bennett',
          action: 'job.status_changed',
          summary: 'Moved JOB-0042 from New to Scheduled',
          changes: [{ field: 'status', from: 'New', to: 'Scheduled' }],
        },
      ]);
      await settle(harness.fixture);

      const history = text(
        main(element).querySelector('section[aria-labelledby="history-heading"]'),
      );
      expect(history).toContain('Moved JOB-0042 from New to Scheduled');
      expect(history).toContain('Olivia Bennett');
      expect(history).toContain('Status: New → Scheduled');
    });
  });
});
