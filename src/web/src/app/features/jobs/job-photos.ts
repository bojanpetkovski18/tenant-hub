import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { LimitReached } from '../../shared/ui/limit-reached';
import { ViewPhotos } from '../../shared/ui/photo-viewer';
import { ErrorState } from '../../shared/ui/states';
import { JobsApi, Photo, Photos } from './jobs.api';

/** An upload in progress: the local preview (a blob: URL, revoked when done) and how much has been sent. */
interface Upload {
  readonly preview: string;
  readonly name: string;
  readonly percent: number;
}

/**
 * US-90: the job's photos. Whoever works on an open job adds them from a phone's camera or files (any image the
 * device offers; the server accepts JPEG, PNG and WebP by content and says why it refuses anything else), with a
 * preview and progress while each uploads. Thumbnails open the viewer; removing asks first.
 */
@Component({
  selector: 'app-job-photos',
  imports: [ErrorState, LimitReached],
  template: `
    <section class="th-card mt-5" aria-labelledby="photos-heading">
      <div class="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="photos-heading" class="text-lg font-semibold">Photos</h2>
        @if (photos(); as p) {
          <p class="text-sm text-muted">{{ p.items.length }} of {{ p.maxPhotos }}</p>
        }
      </div>
      <p class="sr-only" aria-live="polite" aria-atomic="true">{{ announcement() }}</p>

      @if (loadError(); as error) {
        <div class="mt-3"><app-error-state [message]="error.title" (retry)="load()" /></div>
      } @else if (photos(); as p) {
        @if (limit(); as problem) {
          <div class="mt-3"><app-limit-reached [problem]="problem" [slug]="slug()" /></div>
        } @else if (message(); as text) {
          <div class="th-alert-error mt-3" role="alert">{{ text }}</div>
        }
        @if (p.items.length === 0 && uploads().length === 0) {
          <p class="mt-2 text-sm text-muted">
            {{
              p.canAdd
                ? 'No photos yet. Add photos of the finished work; the client sees them once the job is completed.'
                : 'No photos.'
            }}
          </p>
        } @else {
          <ul class="mt-3 grid grid-cols-3 gap-2 sm:grid-cols-4 lg:grid-cols-5">
            @for (photo of p.items; track photo.id; let i = $index) {
              <li class="relative">
                <button
                  type="button"
                  class="block aspect-square w-full overflow-hidden rounded-md border border-border bg-surface-2 focus-visible:outline-2"
                  [attr.aria-label]="'Open photo ' + (i + 1) + ' of ' + p.items.length"
                  (click)="view(p, i)"
                >
                  <img
                    class="size-full object-cover"
                    [src]="api.photoUrl(organizationId(), jobId(), photo.id, true)"
                    alt=""
                    loading="lazy"
                  />
                </button>
                @if (p.canRemove) {
                  <button
                    type="button"
                    class="th-icon-button absolute right-1 top-1 bg-surface/90"
                    [disabled]="busy()"
                    [attr.aria-label]="'Remove photo ' + (i + 1)"
                    (click)="remove(photo, i)"
                  >
                    ✕
                  </button>
                }
              </li>
            }
            @for (upload of uploads(); track upload.preview) {
              <li class="relative" aria-busy="true">
                <img
                  class="aspect-square w-full rounded-md object-cover opacity-60"
                  [src]="upload.preview"
                  alt=""
                />
                <div class="absolute inset-x-1 bottom-1">
                  <div
                    role="progressbar"
                    class="h-1.5 overflow-hidden rounded-full bg-surface-2"
                    aria-valuemin="0"
                    aria-valuemax="100"
                    [attr.aria-valuenow]="upload.percent"
                    [attr.aria-label]="'Uploading ' + upload.name"
                  >
                    <div [class]="'h-full bg-accent ' + width(upload.percent)"></div>
                  </div>
                </div>
              </li>
            }
          </ul>
        }
        @if (p.canAdd) {
          <div class="mt-3">
            <!-- The picker offers the camera on a phone (accept="image/*"); the button is what people use. -->
            <input
              #picker
              type="file"
              accept="image/*"
              multiple
              class="hidden"
              tabindex="-1"
              aria-hidden="true"
              (change)="picked(picker)"
            />
            <button
              type="button"
              class="th-button-secondary"
              aria-describedby="photo-hint"
              (click)="picker.click()"
            >
              Add photos
            </button>
            <p id="photo-hint" class="th-hint">
              JPEG, PNG or WebP, up to 10 MB each. Location and other details are removed.
            </p>
          </div>
        } @else if (p.cannotAdd) {
          <p class="mt-2 text-sm text-muted">{{ p.cannotAdd }}</p>
        }
      } @else {
        <div class="mt-3 h-24 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
      }
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobPhotos {
  readonly organizationId = input.required<string>();
  readonly slug = input.required<string>();
  readonly jobId = input.required<string>();
  readonly jobLabel = input.required<string>();
  /** Changes when the job does (its status decides whether photos can change). */
  readonly jobVersion = input<number>(0);
  /** A photo was added or removed: the job's history changed. */
  readonly changed = output<void>();

  protected readonly api = inject(JobsApi);
  private readonly confirm = inject(Confirm);
  private readonly viewer = inject(ViewPhotos);
  private readonly running = new Set<Subscription>();

  protected readonly photos = signal<Photos | null>(null);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly limit = signal<ApiProblem | null>(null);
  protected readonly uploads = signal<readonly Upload[]>([]);
  protected readonly busy = signal(false);
  protected readonly announcement = signal('');

  constructor() {
    effect(() => {
      if (this.organizationId() && this.jobId()) {
        this.jobVersion();
        void this.load();
      }
    });
    inject(DestroyRef).onDestroy(() => {
      this.running.forEach((s) => s.unsubscribe());
      this.uploads().forEach((u) => URL.revokeObjectURL(u.preview));
    });
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.photos.set(await this.api.photos(this.organizationId(), this.jobId()));
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    }
  }

  protected width(percent: number): string {
    const steps = ['w-0', 'w-1/4', 'w-1/2', 'w-3/4', 'w-full'];
    return steps[Math.min(4, Math.floor(percent / 25))]!;
  }

  protected view(p: Photos, index: number): void {
    this.viewer.open({
      title: `Photos of ${this.jobLabel()}`,
      index,
      photos: p.items.map((photo, i) => ({
        url: this.api.photoUrl(this.organizationId(), this.jobId(), photo.id),
        alt: `Photo ${i + 1} of ${p.items.length} of ${this.jobLabel()}, added by ${photo.createdByName}`,
        width: photo.width,
        height: photo.height,
      })),
    });
  }

  /** Each chosen file uploads on its own; the server decides what it accepts, so nothing is filtered here. */
  protected picked(picker: HTMLInputElement): void {
    const files = Array.from(picker.files ?? []);
    picker.value = '';
    this.message.set(null);
    this.limit.set(null);
    for (const file of files) {
      this.upload(file);
    }
  }

  private upload(file: File): void {
    const preview = URL.createObjectURL(file);
    this.uploads.update((list) => [...list, { preview, name: file.name, percent: 0 }]);
    this.busy.set(true);
    const finish = () => {
      URL.revokeObjectURL(preview);
      this.uploads.update((list) => list.filter((u) => u.preview !== preview));
      this.busy.set(this.uploads().length > 0);
    };
    const subscription = this.api.uploadPhoto(this.organizationId(), this.jobId(), file).subscribe({
      next: (event) => {
        if (event.kind === 'progress') {
          this.uploads.update((list) =>
            list.map((u) => (u.preview === preview ? { ...u, percent: event.percent } : u)),
          );
        } else {
          this.announcement.set(`Added ${file.name}.`);
          void this.load();
          this.changed.emit();
        }
      },
      error: (error: unknown) => {
        const problem = toApiProblem(error);
        if (problem.code === 'plan-limit-reached') {
          this.limit.set(problem);
        } else {
          this.message.set(`${file.name}: ${problem.title}`);
        }
        finish();
        this.running.delete(subscription);
      },
      complete: () => {
        finish();
        this.running.delete(subscription);
      },
    });
    this.running.add(subscription);
  }

  protected async remove(photo: Photo, index: number): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Remove photo ${index + 1}?`,
      message: 'It is deleted for good. The history keeps a record that it was removed.',
      confirmLabel: 'Remove photo',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    try {
      await this.api.removePhoto(this.organizationId(), this.jobId(), photo.id);
      this.announcement.set(`Removed photo ${index + 1}.`);
      await this.load();
      this.changed.emit();
    } catch (error) {
      this.message.set(toApiProblem(error).title);
      void this.load();
    } finally {
      this.busy.set(false);
    }
  }
}
