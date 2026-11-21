import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTestHttp, settle } from '../../testing/session';
import { NotificationPreferencesPage } from './notification-preferences-page';
import { NotificationPreference } from './notifications.api';

const preferences: NotificationPreference[] = [
  { type: 'assigned', label: 'A job is assigned to me', email: true },
  { type: 'visitChanged', label: 'My visit is rescheduled or cancelled', email: false },
];

describe('notification preferences page', () => {
  let fixture: ComponentFixture<NotificationPreferencesPage>;
  let http: HttpTestingController;
  const url = '/api/me/notification-preferences';

  async function render(): Promise<HTMLElement> {
    TestBed.configureTestingModule({ providers: [provideTestHttp(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(NotificationPreferencesPage);
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('shows each type with its email choice and saves the changed ones', async () => {
    const element = await render();
    http.expectOne(url).flush(preferences);
    await settle(fixture);

    const boxes = [...element.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')];
    expect(boxes.map((b) => b.checked)).toEqual([true, false]);
    expect(text(boxes[1]!.closest('label'))).toBe('My visit is rescheduled or cancelled');
    boxes[0]!.checked = false;
    boxes[0]!.dispatchEvent(new Event('change'));
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle(fixture);
    const saved = http.expectOne((r) => r.url === url && r.method === 'PUT');
    expect(saved.request.body).toEqual({
      preferences: [
        { type: 'assigned', email: false },
        { type: 'visitChanged', email: false },
      ],
    });
    saved.flush([{ ...preferences[0]!, email: false }, preferences[1]]);
    await settle(fixture);

    expect(text(element.querySelector('[role="status"]'))).toBe('Saved.');
  });

  it('offers a retry when the choices cannot be loaded', async () => {
    const element = await render();
    http.expectOne(url).flush({ title: 'Server error' }, { status: 500, statusText: 'Error' });
    await settle(fixture);

    expect(text(element)).toContain('Try again');
  });
});
