import { HttpEventType } from '@angular/common/http';
import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { loadSession, olivia, provideTestHttp, settle } from '../../testing/session';
import { JobPhotos } from './job-photos';
import { Photo, Photos } from './jobs.api';

const photo = (id: string): Photo => ({
  id,
  contentType: 'image/jpeg',
  width: 1600,
  height: 1200,
  bytes: 200_000,
  createdAt: '2026-11-19T10:00:00Z',
  createdByName: 'Maya Torres',
});

const photos = (overrides: Partial<Photos> = {}): Photos => ({
  items: [],
  canAdd: true,
  canRemove: true,
  cannotAdd: null,
  maxPhotos: 10,
  ...overrides,
});

describe('job photos', () => {
  let fixture: ComponentFixture<JobPhotos>;
  let http: HttpTestingController;
  const url = '/api/orgs/o1/jobs/j1/photos';

  beforeEach(() => {
    // The preview is a blob: URL; the test environment has no object URLs of its own.
    URL.createObjectURL = vi.fn(() => 'blob:preview-1');
    URL.revokeObjectURL = vi.fn();
  });

  async function render(p: Photos): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter([]), provideCspSafeCdkStyles()],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(olivia);
    fixture = TestBed.createComponent(JobPhotos);
    fixture.componentRef.setInput('organizationId', 'o1');
    fixture.componentRef.setInput('slug', 'brightline');
    fixture.componentRef.setInput('jobId', 'j1');
    fixture.componentRef.setInput('jobLabel', 'JOB-0042');
    await settle(fixture);
    http.expectOne(url).flush(p);
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  function choose(element: HTMLElement, name: string, type: string): void {
    const input = element.querySelector<HTMLInputElement>('input[type="file"]')!;
    expect(input.accept).toBe('image/*');
    Object.defineProperty(input, 'files', {
      value: [new File(['x'], name, { type })],
      configurable: true,
    });
    input.dispatchEvent(new Event('change'));
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('shows the preview and progress while a photo uploads, then the stored photo', async () => {
    const element = await render(photos());
    let changed = 0;
    fixture.componentInstance.changed.subscribe(() => changed++);

    choose(element, 'kitchen.jpg', 'image/jpeg');
    await settle(fixture);
    const upload = http.expectOne((r) => r.url === url && r.method === 'POST');
    expect(upload.request.body).toBeInstanceOf(FormData);
    expect(element.querySelector('img')?.getAttribute('src')).toBe('blob:preview-1');
    upload.event({ type: HttpEventType.UploadProgress, loaded: 60, total: 100 });
    await settle(fixture);
    expect(element.querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('60');

    upload.flush(photo('p1'));
    await settle(fixture);
    http.expectOne(url).flush(photos({ items: [photo('p1')] }));
    await settle(fixture);

    expect(element.querySelector('[role="progressbar"]')).toBeNull();
    expect(element.querySelector('img')?.getAttribute('src')).toBe(`${url}/p1/thumbnail`);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:preview-1');
    expect(text(element.querySelector('[aria-live]'))).toBe('Added kitchen.jpg.');
    expect(changed).toBe(1);
  });

  it('says why the server refused a file, naming it', async () => {
    const element = await render(photos());

    choose(element, 'notes.txt', 'text/plain');
    await settle(fixture);
    http
      .expectOne((r) => r.url === url && r.method === 'POST')
      .flush(
        { title: 'Only JPEG, PNG or WebP photos can be added.', code: 'photo-not-accepted' },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);

    expect(text(element.querySelector('[role="alert"]'))).toBe(
      'notes.txt: Only JPEG, PNG or WebP photos can be added.',
    );
    expect(element.querySelector('[role="progressbar"]')).toBeNull();
  });

  it('shows the plan message when storage is full', async () => {
    const element = await render(photos());

    choose(element, 'big.jpg', 'image/jpeg');
    await settle(fixture);
    http
      .expectOne((r) => r.url === url && r.method === 'POST')
      .flush(
        {
          title: 'Your Free plan allows 100 MB of photo storage, and 99.5 MB is in use.',
          code: 'plan-limit-reached',
          plan: 'free',
          limit: 'storage',
          allowed: 104857600,
          usage: 104333312,
        },
        { status: 403, statusText: 'Forbidden' },
      );
    await settle(fixture);

    expect(text(element.querySelector('app-limit-reached'))).toContain('100 MB of photo storage');
  });

  it('opens a photo in the viewer and walks through the others by keyboard', async () => {
    const element = await render(photos({ items: [photo('p1'), photo('p2')] }));

    element.querySelector<HTMLButtonElement>('button[aria-label="Open photo 1 of 2"]')!.click();
    await settle(fixture);
    const dialog = document.querySelector('[role="dialog"]')!;
    expect(dialog.querySelector('img')?.getAttribute('src')).toBe(`${url}/p1`);
    expect(dialog.querySelector('img')?.getAttribute('alt')).toContain('Photo 1 of 2 of JOB-0042');

    dialog
      .querySelector('app-photo-viewer')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    await settle(fixture);
    expect(dialog.querySelector('img')?.getAttribute('src')).toBe(`${url}/p2`);
  });

  it('explains why no more photos can be added', async () => {
    const element = await render(
      photos({ canAdd: false, cannotAdd: 'A job has at most 10 photos. Remove one first.' }),
    );

    expect(text(element)).toContain('A job has at most 10 photos.');
    expect(element.querySelector('input[type="file"]')).toBeNull();
  });
});
