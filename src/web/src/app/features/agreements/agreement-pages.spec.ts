import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { SessionUser } from '../../core/session/session.models';
import {
  loadSession,
  olivia,
  permissionsFor,
  provideTestHttp,
  settle,
} from '../../testing/session';
import { Agreement, AgreementListItem } from './agreements.api';

const org = olivia.memberships[0]!.organizationId;
const agreementsUrl = `/api/orgs/${org}/agreements`;

const viewer: SessionUser = {
  ...olivia,
  memberships: [{ ...olivia.memberships[0]!, role: 'viewer', permissions: permissionsFor.viewer }],
};

const listItem = (overrides: Partial<AgreementListItem> = {}): AgreementListItem => ({
  id: 'a1',
  title: 'Weekly office cleaning',
  clientId: 'c1',
  clientName: 'Acme Offices',
  schedule: 'Weekly on Mon, Thu',
  status: 'active',
  pausedReason: null,
  assigneeName: 'Maya Torres',
  nextVisitOn: '2027-01-07',
  ...overrides,
});

const agreement = (overrides: Partial<Agreement> = {}): Agreement => ({
  id: 'a1',
  clientId: 'c1',
  clientName: 'Acme Offices',
  title: 'Weekly office cleaning',
  description: null,
  assigneeUserId: 'u3',
  assigneeName: 'Maya Torres',
  priority: 'normal',
  quotedAmount: 180,
  estimatedMinutes: 120,
  recurrence: {
    kind: 'weekly',
    interval: 1,
    weekdays: ['monday', 'thursday'],
    day: null,
    lastDay: false,
    week: null,
    weekday: null,
  },
  schedule: 'Weekly on Mon, Thu',
  startsOn: '2027-01-04',
  endsOn: null,
  maxVisits: null,
  status: 'active',
  pausedReason: null,
  generatedThrough: '2027-03-05',
  createdAt: '2027-01-04T09:00:00Z',
  updatedAt: '2027-01-04T09:00:00Z',
  version: 3,
  upcomingVisits: [
    {
      id: 'j1',
      label: 'JOB-0001',
      occurrenceOn: '2027-01-07',
      scheduledOn: '2027-01-07',
      status: 'scheduled',
      assigneeName: 'Maya Torres',
      changedByHand: false,
    },
  ],
  changedByHand: [],
  canManage: true,
  ...overrides,
});

describe('agreement pages', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function open(url: string, user: SessionUser = olivia): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(user);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    // The shell checks the membership as it opens the organization.
    http.match(`/api/orgs/${org}`).forEach((r) => r.flush({}));
    return harness.routeNativeElement as HTMLElement;
  }

  const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

  describe('list', () => {
    it('lists agreements with their schedule and next visit, and offers a new one to managers', async () => {
      const page = await open('/o/brightline/agreements?q=office');

      const request = http.expectOne((r) => r.url === agreementsUrl);
      expect(request.request.params.get('q')).toBe('office');
      request.flush({ items: [listItem()], page: 1, pageSize: 25, total: 1 });
      await settle(harness.fixture);

      expect(page.querySelector('table tbody')?.textContent).toContain('Weekly on Mon, Thu');
      expect(page.querySelector('table tbody')?.textContent).toContain('Acme Offices');
      expect(page.querySelector('a.th-button')?.textContent).toContain('New agreement');
    });

    it('shows the empty state, and no "New" action to a viewer', async () => {
      const page = await open('/o/brightline/agreements', viewer);

      http
        .expectOne((r) => r.url === agreementsUrl)
        .flush({ items: [], page: 1, pageSize: 25, total: 0 });
      await settle(harness.fixture);

      expect(page.textContent).toContain('No agreements yet');
      expect(page.querySelector('a.th-button')).toBeNull();
    });

    it('offers to try again after an error', async () => {
      const page = await open('/o/brightline/agreements');

      http
        .expectOne((r) => r.url === agreementsUrl)
        .flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });
      await settle(harness.fixture);
      page.querySelector<HTMLButtonElement>('[role="alert"] button')?.click();
      await settle(harness.fixture);

      http
        .expectOne((r) => r.url === agreementsUrl)
        .flush({ items: [listItem()], page: 1, pageSize: 25, total: 1 });
      await settle(harness.fixture);
      expect(page.textContent).toContain('Weekly office cleaning');
    });
  });

  describe('form', () => {
    async function openForm(): Promise<HTMLElement> {
      const page = await open('/o/brightline/agreements/new?clientId=c1');
      http.expectOne((r) => r.url.endsWith('/members')).flush([]);
      http
        .expectOne((r) => r.url.endsWith('/clients'))
        .flush({ items: [{ id: 'c1', name: 'Acme Offices', status: 'active' }] });
      await settle(harness.fixture);
      return page;
    }

    const check = (page: HTMLElement, label: string) => {
      const box = [...page.querySelectorAll<HTMLLabelElement>('fieldset label')].find((l) =>
        l.textContent?.includes(label),
      );
      box!.querySelector<HTMLInputElement>('input')!.click();
    };

    it('asks for a weekday before previewing, then shows the next dates from the API', async () => {
      const page = await openForm();
      await pause(350);

      expect(page.textContent).toContain('Choose at least one day to see the dates.');
      http.expectNone(`${agreementsUrl}/preview`);

      check(page, 'Thursday');
      check(page, 'Monday');
      await pause(350);
      const preview = http.expectOne(`${agreementsUrl}/preview`);
      expect(preview.request.body.recurrence).toEqual({
        kind: 'weekly',
        interval: 1,
        weekdays: ['monday', 'thursday'],
      });
      preview.flush({ dates: ['2027-01-04', '2027-01-07'] });
      await settle(harness.fixture);

      const dates = [...page.querySelectorAll('ol li')].map((li) => li.textContent?.trim());
      expect(dates).toEqual(['Monday 4 January 2027', 'Thursday 7 January 2027']);
    });

    it('sends only the fields of the chosen pattern, such as the last day of the month', async () => {
      const page = await openForm();
      const kind = page.querySelector<HTMLSelectElement>('#kind')!;
      kind.value = 'monthlyByDay';
      kind.dispatchEvent(new Event('change'));
      await settle(harness.fixture);
      const day = page.querySelector<HTMLSelectElement>('#day')!;
      day.value = 'last';
      day.dispatchEvent(new Event('change'));
      await pause(350);

      const preview = http.expectOne(`${agreementsUrl}/preview`);
      expect(preview.request.body.recurrence).toEqual({
        kind: 'monthlyByDay',
        interval: 1,
        lastDay: true,
      });
      preview.flush({ dates: [] });
      await settle(harness.fixture);
      expect(page.textContent).toContain('No visit dates in the next five years.');
    });

    it('checks the form before saving and shows the server rules on their fields', async () => {
      const page = await openForm();
      await pause(350);
      page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);

      expect(page.querySelector('#title-error')?.textContent).toContain('Enter a title.');
      expect(page.querySelector('#weekdays-error')?.textContent).toContain(
        'Choose at least one weekday.',
      );
      http.expectNone((r) => r.method === 'POST' && r.url === agreementsUrl);

      const title = page.querySelector<HTMLInputElement>('#title')!;
      title.value = 'Weekly office cleaning';
      title.dispatchEvent(new Event('input'));
      check(page, 'Monday');
      await pause(350);
      http.match(`${agreementsUrl}/preview`).forEach((r) => r.flush({ dates: [] }));
      page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);

      const create = http.expectOne((r) => r.method === 'POST' && r.url === agreementsUrl);
      expect(create.request.body).toMatchObject({
        clientId: 'c1',
        title: 'Weekly office cleaning',
        recurrence: { kind: 'weekly', interval: 1, weekdays: ['monday'] },
        endsOn: null,
        maxVisits: null,
      });
      create.flush(
        { title: 'Invalid', errors: { startsOn: ['The first visit cannot be before today.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);
      expect(page.querySelector('#startsOn-error')?.textContent).toContain(
        'The first visit cannot be before today.',
      );
    });

    it('goes to the new agreement once it is saved', async () => {
      const page = await openForm();
      const title = page.querySelector<HTMLInputElement>('#title')!;
      title.value = 'Weekly office cleaning';
      title.dispatchEvent(new Event('input'));
      check(page, 'Monday');
      await pause(350);
      http.match(`${agreementsUrl}/preview`).forEach((r) => r.flush({ dates: [] }));

      page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);
      http
        .expectOne((r) => r.method === 'POST' && r.url === agreementsUrl)
        .flush(agreement({ id: 'a9' }));
      await settle(harness.fixture);

      expect(TestBed.inject(Router).url).toBe('/o/brightline/agreements/a9');
      http.match((r) => r.url.startsWith(agreementsUrl)).forEach((r) => r.flush(agreement()));
    });
  });

  describe('detail', () => {
    it('shows the schedule, the next visits and the actions a manager may take', async () => {
      const page = await open('/o/brightline/agreements/a1');
      http.expectOne(`${agreementsUrl}/a1`).flush(agreement());
      await settle(harness.fixture);

      expect(page.querySelector('h1')?.textContent).toContain('Weekly office cleaning');
      expect(page.textContent).toContain('Weekly on Mon, Thu');
      expect(page.textContent).toContain('JOB-0001');
      const actions = [...page.querySelectorAll('main a.th-button-secondary, main button')].map(
        (b) => b.textContent?.trim(),
      );
      expect(actions).toEqual(['Edit', 'Pause', 'End agreement']);
    });

    it('explains a plan-limit hold and offers no actions to whoever cannot manage', async () => {
      const page = await open('/o/brightline/agreements/a1', viewer);
      http
        .expectOne(`${agreementsUrl}/a1`)
        .flush(agreement({ canManage: false, pausedReason: 'planLimit' }));
      await settle(harness.fixture);

      expect(page.querySelector('[role="note"]')?.textContent).toContain(
        "the plan's open-jobs limit is reached",
      );
      expect(page.querySelector('main button')).toBeNull();
      expect(page.textContent).toContain('Active, limit reached');
    });

    it('pauses and says what that means', async () => {
      const page = await open('/o/brightline/agreements/a1');
      http.expectOne(`${agreementsUrl}/a1`).flush(agreement());
      await settle(harness.fixture);

      [...page.querySelectorAll<HTMLButtonElement>('main button')]
        .find((b) => b.textContent?.trim() === 'Pause')!
        .click();
      await settle(harness.fixture);
      http.expectOne(`${agreementsUrl}/a1/pause`).flush(agreement({ status: 'paused' }));
      await settle(harness.fixture);

      expect(page.querySelector('[role="status"]')?.textContent).toContain(
        'No new visits are added',
      );
      expect([...page.querySelectorAll('main button')].map((b) => b.textContent?.trim())).toContain(
        'Resume',
      );
    });
  });
});
