import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AppNotification, NotificationList } from '../../features/notifications/notifications.api';
import { provideTestHttp, settle } from '../../testing/session';
import { provideCspSafeCdkStyles } from './csp-safe-style-loader';
import { NotificationBell, unreadRefreshMs } from './notification-bell';

const url = '/api/orgs/o1/notifications';

const notification = (id: string, overrides: Partial<AppNotification> = {}): AppNotification => ({
  id,
  type: 'assigned',
  text: `Adam Clarke assigned JOB-00${id} “Deep clean” to you.`,
  link: 'job',
  linkId: `job-${id}`,
  createdAt: '2026-11-21T09:00:00Z',
  readAt: null,
  ...overrides,
});

const list = (
  items: AppNotification[],
  unread = items.filter((i) => !i.readAt).length,
): NotificationList => ({
  items,
  page: 1,
  pageSize: 10,
  total: items.length,
  unread,
});

describe('notification bell', () => {
  let fixture: ComponentFixture<NotificationBell>;
  let http: HttpTestingController;

  async function render(unread: number): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideTestHttp(),
        provideRouter([{ path: '**', children: [] }]),
        provideCspSafeCdkStyles(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(NotificationBell);
    fixture.componentRef.setInput('organizationId', 'o1');
    fixture.componentRef.setInput('slug', 'brightline');
    await settle(fixture);
    http.match(`${url}?pageSize=1`).forEach((r) => r.flush(list([], unread)));
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  async function open(element: HTMLElement, answer: NotificationList): Promise<void> {
    element.querySelector<HTMLButtonElement>('button')!.click();
    await settle(fixture);
    http.expectOne(`${url}?pageSize=10`).flush(answer);
    await settle(fixture);
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const panel = () => document.querySelector('#notification-panel');

  afterEach(() => {
    vi.useRealTimers();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('shows the unread count in its name and its badge', async () => {
    const element = await render(3);
    const bell = element.querySelector('button')!;

    expect(bell.getAttribute('aria-label')).toBe('Notifications, 3 unread');
    expect(text(bell)).toBe('3');
    expect(bell.getAttribute('aria-expanded')).toBe('false');
  });

  it('opens the latest notifications, moves focus into the panel and closes with Escape', async () => {
    const element = await render(1);
    await open(
      element,
      list([notification('1'), notification('2', { readAt: '2026-11-21T10:00:00Z' })]),
    );

    const bell = element.querySelector('button')!;
    expect(bell.getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement?.id).toBe('notification-heading');
    const items = [...panel()!.querySelectorAll('li')].map((li) => text(li));
    expect(items).toHaveLength(2);
    expect(items[0]).toContain('assigned JOB-001 “Deep clean” to you.');
    expect(items[0]).toContain('unread');
    expect(items[1]).not.toContain('unread');
    expect(panel()!.querySelector('a')!.getAttribute('href')).toBe('/o/brightline/jobs/job-1');

    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle(fixture);
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(bell);
  });

  it('marks a notification read as it opens where it leads', async () => {
    const element = await render(1);
    await open(element, list([notification('1')]));

    panel()!
      .querySelector('a')!
      .dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
    await settle(fixture);
    const read = http.expectOne(`${url}/read`);
    expect(read.request.body).toEqual({ ids: ['1'] });
    read.flush({ unread: 0 });
    await settle(fixture);

    expect(TestBed.inject(Router).url).toBe('/o/brightline/jobs/job-1');
    expect(element.querySelector('button')!.getAttribute('aria-label')).toBe('Notifications');
  });

  it('marks all as read and says so', async () => {
    const element = await render(2);
    await open(element, list([notification('1'), notification('2')]));

    [...panel()!.querySelectorAll('button')].find((b) => text(b) === 'Mark all as read')!.click();
    await settle(fixture);
    const read = http.expectOne(`${url}/read`);
    expect(read.request.body).toEqual({ all: true });
    read.flush({ unread: 0 });
    await settle(fixture);
    http
      .expectOne(`${url}?pageSize=10`)
      .flush(list([notification('1', { readAt: 'x' }), notification('2', { readAt: 'x' })]));
    await settle(fixture);

    expect(text(element.querySelector('[aria-live]'))).toBe('All notifications marked as read.');
    expect(element.querySelector('button')!.getAttribute('aria-label')).toBe('Notifications');
  });

  it('has an empty state and a retry', async () => {
    const element = await render(0);
    element.querySelector<HTMLButtonElement>('button')!.click();
    await settle(fixture);
    http
      .expectOne(`${url}?pageSize=10`)
      .flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle(fixture);
    expect(text(panel())).toContain('could not be loaded');

    [...panel()!.querySelectorAll('button')].find((b) => text(b) === 'Try again')!.click();
    await settle(fixture);
    http.expectOne(`${url}?pageSize=10`).flush(list([]));
    await settle(fixture);
    expect(text(panel())).toContain('Nothing yet.');
  });

  it('asks for the count every minute while the tab is visible, and not at all while it is hidden', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    await render(0);
    let visibility: DocumentVisibilityState = 'visible';
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => visibility,
    });

    vi.advanceTimersByTime(unreadRefreshMs);
    await settle(fixture);
    http.expectOne(`${url}?pageSize=1`).flush(list([], 4));

    visibility = 'hidden';
    document.dispatchEvent(new Event('visibilitychange'));
    vi.advanceTimersByTime(unreadRefreshMs * 3);
    await settle(fixture);
    http.expectNone(`${url}?pageSize=1`);

    visibility = 'visible';
    document.dispatchEvent(new Event('visibilitychange'));
    await settle(fixture);
    http.expectOne(`${url}?pageSize=1`).flush(list([], 5)); // back at once
    await settle(fixture);
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('button')!.getAttribute('aria-label'),
    ).toBe('Notifications, 5 unread');
    delete (document as unknown as Record<string, unknown>)['visibilityState'];
  });
});
