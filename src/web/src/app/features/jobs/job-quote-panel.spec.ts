import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { provideTestHttp, settle } from '../../testing/session';
import { JobQuotePanel } from './job-quote-panel';
import { Job, JobQuote } from './jobs.api';

const quote = (overrides: Partial<JobQuote> = {}): JobQuote => ({
  status: 'notRequested',
  sentAt: null,
  sentByName: null,
  approvedAmount: null,
  decidedAt: null,
  decidedByName: null,
  comment: null,
  canSend: true,
  cannotSend: null,
  ...overrides,
});

describe('job quote panel', () => {
  let fixture: ComponentFixture<JobQuotePanel>;

  async function render(q: JobQuote): Promise<HTMLElement> {
    TestBed.configureTestingModule({ providers: [provideTestHttp(), provideCspSafeCdkStyles()] });
    fixture = TestBed.createComponent(JobQuotePanel);
    fixture.componentRef.setInput('organizationId', 'o1');
    fixture.componentRef.setInput('job', {
      id: 'j1',
      label: 'JOB-0042',
      quotedAmount: 180,
      quote: q,
    } as unknown as Job);
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('sends the quote after asking, and hands the job back', async () => {
    const element = await render(quote());
    const changed: Job[] = [];
    fixture.componentInstance.changed.subscribe((job) => changed.push(job));

    [...element.querySelectorAll('button')].find((b) => text(b) === 'Send for approval')!.click();
    await settle(fixture);
    [...document.querySelectorAll('[role="alertdialog"] button')]
      .find((b) => text(b) === 'Send for approval')!
      .dispatchEvent(new MouseEvent('click'));
    await settle(fixture);
    TestBed.inject(HttpTestingController)
      .expectOne('/api/orgs/o1/jobs/j1/quote')
      .flush({ id: 'j1' });
    await settle(fixture);

    expect(changed.map((j) => j.id)).toEqual(['j1']);
  });

  it('says what holds the work back while the quote is pending', async () => {
    const pending = await render(
      quote({
        status: 'pending',
        sentAt: '2026-11-18T09:00:00Z',
        sentByName: 'Adam Clarke',
        canSend: false,
      }),
    );
    expect(text(pending.querySelector('[role="status"]'))).toContain(
      'Work cannot start until it is approved.',
    );
    expect(pending.querySelector('button')).toBeNull();
  });

  it('explains why a quote cannot be sent, to whoever could send one', async () => {
    const element = await render(
      quote({
        canSend: false,
        cannotSend: 'Add a portal contact to the client first: they are the ones who approve.',
      }),
    );

    expect(text(element)).toContain('Add a portal contact to the client first');
    expect(element.querySelector('button')).toBeNull();
  });

  it('shows nothing to people who cannot send and when nothing was sent', async () => {
    const element = await render(quote({ canSend: false, cannotSend: null }));

    expect(element.querySelector('section')).toBeNull();
  });
});
