import { CdkDrag, CdkDragDrop, CdkDropList } from '@angular/cdk/drag-drop';
import { _CdkPrivateStyleLoader } from '@angular/cdk/private';
import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, withComponentInputBinding } from '@angular/router';
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
import { BoardCard, BoardView, addDays, dayText, hoursText } from './board.api';

const org = olivia.memberships[0]!.organizationId;
const boardUrl = `/api/orgs/${org}/board`;
const jobsUrl = `/api/orgs/${org}/jobs`;
const maya = '0199f0a0-0000-7000-8000-000000000003';
const noah = '0199f0a0-0000-7000-8000-000000000004';

const member: SessionUser = {
  ...olivia,
  memberships: [{ ...olivia.memberships[0]!, role: 'member', permissions: permissionsFor.member }],
};

const card = (overrides: Partial<BoardCard> = {}): BoardCard => ({
  id: 'j42',
  number: 42,
  label: 'JOB-0042',
  title: 'Deep clean, 3rd floor',
  clientId: 'c1',
  clientName: 'Acme Offices',
  assigneeUserId: maya,
  scheduledOn: '2026-11-17',
  dueOn: '2026-11-20',
  status: 'scheduled',
  priority: 'normal',
  estimatedMinutes: 240,
  isVisit: false,
  version: 7,
  ...overrides,
});

const board = (overrides: Partial<BoardView> = {}): BoardView => ({
  weekStart: '2026-11-16',
  today: '2026-11-18',
  dailyCapacityMinutes: 480,
  people: [
    { userId: maya, name: 'Maya Torres', active: true },
    { userId: noah, name: 'Noah Kim', active: true },
  ],
  cards: [card()],
  unscheduled: [
    card({
      id: 'j50',
      number: 50,
      label: 'JOB-0050',
      title: 'Gutter check',
      assigneeUserId: null,
      scheduledOn: null,
      status: 'new',
      version: 2,
    }),
  ],
  unscheduledTotal: 1,
  load: [{ assigneeUserId: maya, day: '2026-11-17', minutes: 240, jobs: 1 }],
  canPlan: true,
  ...overrides,
});

describe('board page', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function open(url: string, user: SessionUser = olivia): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideTestHttp(),
        provideRouter(routes, withComponentInputBinding()),
        provideCspSafeCdkStyles(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(user);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    http.match(`/api/orgs/${org}`).forEach((r) =>
      r.flush({
        id: org,
        name: 'Brightline Cleaning Co.',
        slug: 'brightline',
        plan: 'team',
        timeZone: 'Europe/Amsterdam',
      }),
    );
    http
      .match((r) => r.url === `/api/orgs/${org}/clients`)
      .forEach((r) =>
        r.flush({
          items: [{ id: 'c1', name: 'Acme Offices', status: 'active' }],
          page: 1,
          pageSize: 100,
          total: 1,
        }),
      );
    return harness.routeNativeElement as HTMLElement;
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const boardRequest = () => http.expectOne((r) => r.url === boardUrl);
  const buttons = (root: ParentNode) => [...root.querySelectorAll<HTMLButtonElement>('button')];
  const announcement = (page: HTMLElement) => text(page.querySelector('[aria-live="polite"]'));

  async function openLoaded(
    view: BoardView = board(),
    user: SessionUser = olivia,
  ): Promise<HTMLElement> {
    const page = await open('/o/brightline/board', user);
    boardRequest().flush(view);
    await settle(harness.fixture);
    return page;
  }

  /** Opens "Move to…" on JOB-0042, picks a person and a day, and submits. */
  async function moveWithDialog(
    page: HTMLElement,
    personId: string,
    day: string,
  ): Promise<TestRequest> {
    buttons(page)
      .find((b) => text(b) === 'Move to… JOB-0042')!
      .click();
    await settle(harness.fixture);
    const person = document.querySelector<HTMLSelectElement>('#move-person')!;
    person.value = personId;
    person.dispatchEvent(new Event('change'));
    const date = document.querySelector<HTMLInputElement>('#move-day')!;
    date.value = day;
    date.dispatchEvent(new Event('input'));
    document.querySelector('app-move-dialog form')!.dispatchEvent(new Event('submit'));
    await settle(harness.fixture);
    return http.expectOne(`${jobsUrl}/j42/schedule`);
  }

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('asks for the week, client and status in the URL and shows people by days with their load', async () => {
    const page = await open('/o/brightline/board?week=2026-11-18&clientId=c1&status=scheduled');

    const request = boardRequest();
    expect(request.request.params.get('week')).toBe('2026-11-18');
    expect(request.request.params.get('clientId')).toBe('c1');
    expect(request.request.params.get('status')).toBe('scheduled');
    request.flush(board());
    await settle(harness.fixture);

    const rows = [...page.querySelectorAll('tbody tr')].map((r) => text(r.querySelector('th')));
    expect(rows).toEqual(['Maya Torres', 'Noah Kim', 'Unassigned']);
    const tuesday = page.querySelector('[aria-label="Maya Torres, Tuesday 17 November"]')!;
    expect(text(tuesday)).toContain('JOB-0042');
    expect(text(tuesday.parentElement)).toContain('4 h of 8 h');
    expect(text(page.querySelector('[aria-current="date"]'))).toContain('Wed 18 Nov');
    expect(text(page.querySelector('#queue-heading'))).toBe('Not scheduled (1)');
    expect(text(page.querySelector('h1 + p'))).toContain('Week of 16 November to 22 November 2026');
  });

  it('marks a day over the capacity with an icon and words, not colour alone', async () => {
    const page = await openLoaded(
      board({ load: [{ assigneeUserId: maya, day: '2026-11-17', minutes: 570, jobs: 3 }] }),
    );

    const load = page.querySelector(
      '[aria-label="Maya Torres, Tuesday 17 November"]',
    )!.parentElement!;
    expect(text(load)).toContain('Over: 9 h 30 min of 8 h');
    expect(load.querySelector('svg[aria-hidden="true"]')).not.toBeNull();
  });

  it('moves a card with "Move to…" by keyboard, sends the version shown and announces the move', async () => {
    const page = await openLoaded();

    const request = await moveWithDialog(page, noah, '2026-11-19');
    expect(request.request.body).toEqual({
      assigneeUserId: noah,
      scheduledOn: '2026-11-19',
      version: 7,
      moveDueDate: false,
    });
    request.flush({});
    await settle(harness.fixture);
    boardRequest().flush(
      board({ cards: [card({ assigneeUserId: noah, scheduledOn: '2026-11-19', version: 8 })] }),
    );
    await settle(harness.fixture);

    expect(announcement(page)).toBe('JOB-0042 moved to Noah Kim, Thursday 19 November.');
    // Focus is not left on <body> once the card is drawn in its new place (jsdom lays nothing out: the heading).
    expect(document.activeElement).toBe(page.querySelector('h1'));
    expect(text(page.querySelector('[aria-label="Noah Kim, Thursday 19 November"]'))).toContain(
      'JOB-0042',
    );
  });

  it('starts the dialog on the person and puts focus on the first field', async () => {
    const page = await openLoaded();

    buttons(page)
      .find((b) => text(b) === 'Move to… JOB-0042')!
      .click();
    await settle(harness.fixture);

    const dialog = document.querySelector('[role="dialog"]')!;
    expect(document.getElementById(dialog.getAttribute('aria-labelledby')!)?.textContent).toBe(
      'Move JOB-0042',
    );
    expect(document.querySelector<HTMLSelectElement>('#move-person')!.value).toBe(maya);
    expect(document.querySelector<HTMLInputElement>('#move-day')!.value).toBe('2026-11-17');
    expect(document.activeElement?.id).toBe('move-person');
  });

  it('puts a card back in "Not scheduled" when the day is left empty', async () => {
    const page = await openLoaded();

    const request = await moveWithDialog(page, maya, '');
    expect(request.request.body).toEqual({
      assigneeUserId: maya,
      scheduledOn: null,
      version: 7,
      moveDueDate: false,
    });
    request.flush({});
    await settle(harness.fixture);
    boardRequest().flush(board());
    await settle(harness.fixture);

    expect(announcement(page)).toBe('JOB-0042 moved to Not scheduled, assigned to Maya Torres.');
  });

  it('asks before moving past the due date, then moves both', async () => {
    const page = await openLoaded();

    (await moveWithDialog(page, maya, '2026-11-21')).flush(
      {
        title: 'JOB-0042 is due on 20 Nov 2026. Move the due date too, or choose an earlier day.',
        status: 409,
        code: 'after-due-date',
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);
    expect(text(document.querySelector('#confirm-title'))).toBe(
      'Move the due date of JOB-0042 too?',
    );
    buttons(document.querySelector('[role="alertdialog"]')!)
      .find((b) => text(b) === 'Move both')!
      .click();
    await settle(harness.fixture);

    const retry = http.expectOne(`${jobsUrl}/j42/schedule`);
    expect(retry.request.body).toEqual({
      assigneeUserId: maya,
      scheduledOn: '2026-11-21',
      version: 7,
      moveDueDate: true,
    });
    retry.flush({});
    await settle(harness.fixture);
    boardRequest().flush(board());
    await settle(harness.fixture);
    expect(announcement(page)).toBe('JOB-0042 moved to Maya Torres, Saturday 21 November.');
  });

  it('leaves the card where it was on a conflict, says why and shows the board as it is', async () => {
    const page = await openLoaded();

    (await moveWithDialog(page, noah, '2026-11-19')).flush(
      { title: 'Someone else changed this job.', status: 409, code: 'concurrency-conflict' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);
    // The board is read again: what the other person did is what shows.
    boardRequest().flush(
      board({ cards: [card({ assigneeUserId: maya, scheduledOn: '2026-11-18', version: 8 })] }),
    );
    await settle(harness.fixture);

    expect(text(page.querySelector('[role="alert"]'))).toContain(
      'Someone else changed JOB-0042 while you were planning',
    );
    expect(announcement(page)).toBe('JOB-0042 was not moved: someone else changed it.');
    expect(text(page.querySelector('[aria-label="Maya Torres, Wednesday 18 November"]'))).toContain(
      'JOB-0042',
    );
    expect(text(page.querySelector('[aria-label="Noah Kim, Thursday 19 November"]'))).not.toContain(
      'JOB-0042',
    );
  });

  it('shows members the plan without moves', async () => {
    const page = await openLoaded(board({ canPlan: false }), member);

    expect(text(page)).toContain('Admins and Owners move the work.');
    expect(buttons(page).filter((b) => text(b).startsWith('Move to…'))).toEqual([]);
    expect(page.querySelectorAll('.cdk-drag:not(.cdk-drag-disabled)').length).toBe(0);
  });

  it('lists the week day by day on a phone, with "Move to…" and no dragging', async () => {
    const page = await openLoaded();

    const phone = page.querySelector('app-board-page div.space-y-4.lg\\:hidden')!;
    const tuesday = [...phone.querySelectorAll('section')].find(
      (s) => text(s.querySelector('h2')) === dayText('2026-11-17'),
    )!;
    expect(text(tuesday.querySelector('h3'))).toBe('Maya Torres');
    expect(text(tuesday)).toContain('JOB-0042');
    expect(text(tuesday)).toContain('Move to…');
    expect(tuesday.querySelector('.cdk-drag')).toBeNull();
    const monday = [...phone.querySelectorAll('section')].find(
      (s) => text(s.querySelector('h2')) === dayText('2026-11-16'),
    )!;
    expect(text(monday)).toContain('Nothing planned.');
  });

  it('goes to the next week through the URL', async () => {
    const page = await openLoaded();

    buttons(page)
      .find((b) => text(b).startsWith('Next week'))!
      .click();
    await settle(harness.fixture);

    expect(boardRequest().request.params.get('week')).toBe(addDays('2026-11-16', 7));
  });

  it('offers to try again when the board cannot load', async () => {
    const page = await open('/o/brightline/board');
    boardRequest().flush(
      { title: 'Something broke.' },
      { status: 500, statusText: 'Server Error' },
    );
    await settle(harness.fixture);

    expect(text(page.querySelector('app-error-state'))).toContain('Try again');
  });

  // Regression guard (D023): picking up a card makes the CDK load its drag-and-drop resets, which would become an
  // inline <style> that the strict CSP blocks; the loader skips them and the rules live in styles.css instead.
  // Regression (found in the browser): rendered through a template outlet, a card's cdkDrag could not find the
  // cdkDropList around it (DI follows where a template is declared), so a dragged card never dropped into a cell.
  it('puts every card in the drop list of its cell, and a drop there moves it', async () => {
    const page = await openLoaded();
    const drags = harness.fixture.debugElement
      .queryAll(By.directive(CdkDrag))
      .map((d) => d.injector.get(CdkDrag));
    const lists = harness.fixture.debugElement
      .queryAll(By.directive(CdkDropList))
      .map((d) => d.injector.get(CdkDropList));

    expect(
      drags.filter((d) => d.dropContainer === undefined || !lists.includes(d.dropContainer)),
    ).toEqual([]);
    expect(drags.length).toBe(2); // the planned card and the unscheduled one; phones have no drags
    const planned = drags.find((d) => (d.data as BoardCard).id === 'j42')!;
    expect(planned.dropContainer.data).toEqual({ assigneeUserId: maya, day: '2026-11-17' });

    const target = lists.find(
      (l) => l.data?.assigneeUserId === noah && l.data.day === '2026-11-20',
    )!;
    target.dropped.emit({
      item: planned,
      container: target,
      previousContainer: planned.dropContainer,
    } as CdkDragDrop<unknown>);
    await settle(harness.fixture);

    const request = http.expectOne(`${jobsUrl}/j42/schedule`);
    expect(request.request.body).toEqual({
      assigneeUserId: noah,
      scheduledOn: '2026-11-20',
      version: 7,
      moveDueDate: false,
    });
    request.flush({});
    await settle(harness.fixture);
    boardRequest().flush(board());
    await settle(harness.fixture);
    expect(announcement(page)).toBe('JOB-0042 moved to Noah Kim, Friday 20 November.');
  });

  it('takes a card off the plan when it is dropped on "Not scheduled", keeping its person', async () => {
    await openLoaded();
    const lists = harness.fixture.debugElement
      .queryAll(By.directive(CdkDropList))
      .map((d) => d.injector.get(CdkDropList));
    const planned = harness.fixture.debugElement
      .queryAll(By.directive(CdkDrag))
      .map((d) => d.injector.get(CdkDrag))
      .find((d) => (d.data as BoardCard).id === 'j42')!;
    const queue = lists.find(
      (l) => l.element.nativeElement.getAttribute('aria-label') === 'Not scheduled',
    )!;

    queue.dropped.emit({ item: planned, container: queue } as CdkDragDrop<unknown>);
    await settle(harness.fixture);

    expect(http.expectOne(`${jobsUrl}/j42/schedule`).request.body).toEqual({
      assigneeUserId: maya,
      scheduledOn: null,
      version: 7,
      moveDueDate: false,
    });
  });

  it('picks up a card without adding an inline style element', async () => {
    const page = await openLoaded();
    const load = vi.spyOn(TestBed.inject(_CdkPrivateStyleLoader), 'load');

    // A real pointer press: the CDK ignores presses with no buttons or no click count (screen readers send those).
    page
      .querySelector<HTMLElement>('table .cdk-drag')!
      .dispatchEvent(
        new MouseEvent('mousedown', { bubbles: true, button: 0, buttons: 1, detail: 1 }),
      );
    document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    await settle(harness.fixture);

    expect(load).toHaveBeenCalled();
    expect(
      [...document.querySelectorAll('style')].filter((s) =>
        s.textContent?.includes('cdk-drag-preview'),
      ),
    ).toEqual([]);
  });

  it('words hours like the API', () => {
    expect([hoursText(480), hoursText(450), hoursText(45)]).toEqual([
      '8 h',
      '7 h 30 min',
      '45 min',
    ]);
  });
});
