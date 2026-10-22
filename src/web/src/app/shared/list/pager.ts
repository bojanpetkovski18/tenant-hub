import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

/** "Showing 26–50 of 112" with previous and next; hidden when everything fits on one page. */
@Component({
  selector: 'app-pager',
  template: `
    @if (total() > 0) {
      <nav class="flex flex-wrap items-center justify-between gap-3 text-sm" aria-label="Pages">
        <p class="text-muted" aria-live="polite">
          Showing {{ from() }}–{{ to() }} of {{ total() }}
        </p>
        @if (pageCount() > 1) {
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="th-button-secondary"
              [disabled]="page() <= 1"
              (click)="pageChange.emit(page() - 1)"
            >
              Previous<span class="sr-only"> page</span>
            </button>
            <span class="text-muted">Page {{ page() }} of {{ pageCount() }}</span>
            <button
              type="button"
              class="th-button-secondary"
              [disabled]="page() >= pageCount()"
              (click)="pageChange.emit(page() + 1)"
            >
              Next<span class="sr-only"> page</span>
            </button>
          </div>
        }
      </nav>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Pager {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly total = input.required<number>();
  readonly pageChange = output<number>();

  protected readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.pageSize())),
  );
  protected readonly from = computed(() =>
    Math.min(this.total(), (this.page() - 1) * this.pageSize() + 1),
  );
  protected readonly to = computed(() => Math.min(this.total(), this.page() * this.pageSize()));
}
