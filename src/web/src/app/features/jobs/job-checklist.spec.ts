import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { provideTestHttp, settle } from '../../testing/session';
import { JobChecklist } from './job-checklist';
import { Checklist, ChecklistItem } from './jobs.api';

const item = (
  id: string,
  position: number,
  text: string,
  overrides: Partial<ChecklistItem> = {},
): ChecklistItem => ({
  id,
  position,
  text,
  required: true,
  done: false,
  doneAt: null,
  doneByName: null,
  ...overrides,
});

const checklist = (overrides: Partial<Checklist> = {}): Checklist => ({
  items: [
    item('i1', 0, 'Vacuum'),
    item('i2', 1, 'Empty the bins'),
    item('i3', 2, 'Water the plants', { required: false }),
  ],
  canEdit: true,
  readOnlyReason: null,
  maxItems: 30,
  ...overrides,
});

describe('job checklist', () => {
  let fixture: ComponentFixture<JobChecklist>;
  let http: HttpTestingController;
  const url = '/api/orgs/o1/jobs/j1/checklist';

  async function render(c: Checklist): Promise<HTMLElement> {
    TestBed.configureTestingModule({ providers: [provideTestHttp(), provideCspSafeCdkStyles()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(JobChecklist);
    fixture.componentRef.setInput('organizationId', 'o1');
    fixture.componentRef.setInput('jobId', 'j1');
    await settle(fixture);
    http.expectOne(url).flush(c);
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const live = (element: HTMLElement) => text(element.querySelector('[aria-live]'));

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('ticks an item, says so and tells the page', async () => {
    const element = await render(checklist());
    let changed = 0;
    fixture.componentInstance.changed.subscribe(() => changed++);

    const box = element.querySelector<HTMLInputElement>('#item-i1')!;
    expect(element.querySelector('label[for="item-i1"]')).not.toBeNull(); // each checkbox has its label
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    await settle(fixture);
    const put = http.expectOne(`${url}/i1`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ done: true });
    put.flush(
      checklist({
        items: [
          item('i1', 0, 'Vacuum', {
            done: true,
            doneAt: '2026-11-19T10:00:00Z',
            doneByName: 'Maya Torres',
          }),
        ],
      }),
    );
    await settle(fixture);

    expect(live(element)).toBe('Ticked Vacuum.');
    expect(text(element)).toContain('Ticked by Maya Torres');
    expect(changed).toBe(1);
  });

  it('moves an item by keyboard and announces its new place', async () => {
    const element = await render(checklist());
    const buttons = [...element.querySelectorAll('button')];

    expect(buttons.find((b) => b.getAttribute('aria-label') === 'Move “Vacuum” up')!.disabled).toBe(
      true,
    );
    buttons.find((b) => b.getAttribute('aria-label') === 'Move “Vacuum” down')!.click();
    await settle(fixture);
    const put = http.expectOne(`${url}/i1`);
    expect(put.request.body).toEqual({ position: 1 });
    put.flush(checklist({ items: [item('i2', 0, 'Empty the bins'), item('i1', 1, 'Vacuum')] }));
    await settle(fixture);

    expect(live(element)).toBe('Moved Vacuum to place 2 of 3.');
    expect([...element.querySelectorAll('li label')].map((l) => text(l))).toEqual([
      'Empty the bins',
      'Vacuum',
    ]);
  });

  it('asks for text before adding and adds a required item by default', async () => {
    const element = await render(checklist({ items: [] }));
    const form = element.querySelector('form')!;

    form.dispatchEvent(new Event('submit'));
    await settle(fixture);
    expect(text(element.querySelector('#new-item-error'))).toBe('Write what has to be done.');
    http.expectNone(url);

    const field = element.querySelector<HTMLInputElement>('#new-item')!;
    field.value = 'Lock the back door';
    field.dispatchEvent(new Event('input'));
    form.dispatchEvent(new Event('submit'));
    await settle(fixture);
    const post = http.expectOne((r) => r.url === url && r.method === 'POST');
    expect(post.request.body).toEqual({ text: 'Lock the back door', required: true });
  });

  it('shows a completed job’s checklist read-only, with why', async () => {
    const element = await render(
      checklist({
        canEdit: false,
        readOnlyReason: 'JOB-0042 is Completed. Reopen it to change its checklist.',
      }),
    );

    expect(text(element)).toContain('Reopen it to change its checklist.');
    expect(
      [...element.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].every(
        (b) => b.disabled,
      ),
    ).toBe(true);
    expect(element.querySelector('#new-item')).toBeNull();
    expect(element.querySelector('button')).toBeNull();
  });

  it('offers a retry when the checklist cannot be loaded', async () => {
    TestBed.configureTestingModule({ providers: [provideTestHttp(), provideCspSafeCdkStyles()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(JobChecklist);
    fixture.componentRef.setInput('organizationId', 'o1');
    fixture.componentRef.setInput('jobId', 'j1');
    await settle(fixture);
    http.expectOne(url).flush({ title: 'Server error' }, { status: 500, statusText: 'Error' });
    await settle(fixture);

    expect(text(fixture.nativeElement as HTMLElement)).toContain('Try again');
  });
});
