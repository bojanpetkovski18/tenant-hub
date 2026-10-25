import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** An empty list or page: what is missing and, through projected content, what to do about it. */
@Component({
  selector: 'app-empty-state',
  template: `
    <div class="th-card flex flex-col items-center gap-2 py-12 text-center">
      <svg
        viewBox="0 0 24 24"
        class="size-10 text-muted"
        fill="none"
        stroke="currentColor"
        stroke-width="1.5"
        aria-hidden="true"
      >
        <path d="M3 7h18M3 12h18M3 17h10" />
      </svg>
      <p class="font-medium">{{ title() }}</p>
      @if (message()) {
        <p class="max-w-md text-sm text-muted">{{ message() }}</p>
      }
      <div class="mt-3"><ng-content /></div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EmptyState {
  readonly title = input.required<string>();
  readonly message = input('');
}

/** A failed load with the reason and a retry. */
@Component({
  selector: 'app-error-state',
  template: `
    <div class="th-alert-error flex flex-wrap items-center justify-between gap-3" role="alert">
      <span>{{ message() }}</span>
      <button type="button" class="th-button-secondary" (click)="retry.emit()">Try again</button>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ErrorState {
  readonly message = input.required<string>();
  readonly retry = output();
}
