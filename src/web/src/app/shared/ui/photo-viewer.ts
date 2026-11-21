import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  Injectable,
  computed,
  inject,
  signal,
} from '@angular/core';
import { CspSafeDialogContainer } from './dialog-container';

export interface ViewedPhoto {
  readonly url: string;
  /** What the photo is, for screen readers ("Photo 2 of 3 of JOB-0042"). */
  readonly alt: string;
  readonly width: number;
  readonly height: number;
}

export interface PhotoViewerData {
  readonly title: string;
  readonly photos: readonly ViewedPhoto[];
  readonly index: number;
}

/**
 * A photo at full size (SPEC-V2 4.5), with the others of the same job a key or a button away: left and right arrows,
 * Escape closes. Opened through `ViewPhotos`, with the CSP-safe container (D027).
 */
@Component({
  selector: 'app-photo-viewer',
  host: {
    class:
      'flex max-h-[calc(100vh-2rem)] w-[min(64rem,calc(100vw-2rem))] flex-col rounded-lg border border-border bg-surface p-4 shadow-xl',
    '(keydown.arrowleft)': 'step(-1)',
    '(keydown.arrowright)': 'step(1)',
  },
  template: `
    <div class="flex items-center justify-between gap-2">
      <h2 id="photo-viewer-title" class="text-base font-semibold">
        {{ data.title }}
        @if (data.photos.length > 1) {
          <span class="font-normal text-muted"
            >· {{ index() + 1 }} of {{ data.photos.length }}</span
          >
        }
      </h2>
      <button type="button" class="th-icon-button" aria-label="Close" (click)="ref.close()">
        ✕
      </button>
    </div>
    <div class="mt-3 flex min-h-0 flex-1 items-center justify-center">
      <img
        class="max-h-[calc(100vh-10rem)] max-w-full rounded object-contain"
        [src]="photo().url"
        [alt]="photo().alt"
        [width]="photo().width"
        [height]="photo().height"
      />
    </div>
    @if (data.photos.length > 1) {
      <div class="mt-3 flex justify-between gap-2">
        <button
          type="button"
          class="th-button-secondary"
          [disabled]="index() === 0"
          (click)="step(-1)"
        >
          ← Previous
        </button>
        <button
          type="button"
          class="th-button-secondary"
          [disabled]="index() === data.photos.length - 1"
          (click)="step(1)"
        >
          Next →
        </button>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PhotoViewer {
  protected readonly data = inject<PhotoViewerData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<void>>(DialogRef);
  protected readonly index = signal(this.data.index);
  protected readonly photo = computed(() => this.data.photos[this.index()]!);

  protected step(by: number): void {
    this.index.set(Math.min(this.data.photos.length - 1, Math.max(0, this.index() + by)));
  }
}

@Injectable({ providedIn: 'root' })
export class ViewPhotos {
  private readonly dialog = inject(Dialog);

  open(data: PhotoViewerData): void {
    this.dialog.open<void, PhotoViewerData>(PhotoViewer, {
      data,
      backdropClass: 'th-dialog-backdrop',
      container: CspSafeDialogContainer,
      role: 'dialog',
      ariaModal: true,
      ariaLabelledBy: 'photo-viewer-title',
      autoFocus: 'dialog',
    });
  }
}
