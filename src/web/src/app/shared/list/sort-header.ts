import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

/**
 * A sortable column header. The sort is one string like the API's (<c>name</c>, <c>-name</c>); clicking a column
 * sorts by it ascending, clicking again flips it. <c>aria-sort</c> sits on the header cell, as ARIA requires, and
 * the button announces what a click will do.
 */
@Component({
  // An attribute selector on purpose: aria-sort must sit on the th itself, which an element inside it cannot set.
  // eslint-disable-next-line @angular-eslint/component-selector
  selector: 'th[appSortHeader]',
  host: {
    scope: 'col',
    '[attr.aria-sort]': 'ariaSort()',
    class: 'px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted',
  },
  template: `
    <button
      type="button"
      class="inline-flex min-h-8 items-center gap-1 rounded font-semibold uppercase tracking-wide hover:text-fg"
      (click)="sortChange.emit(next())"
    >
      <ng-content />
      <span class="sr-only">{{ hint() }}</span>
      <svg
        viewBox="0 0 24 24"
        class="size-3.5"
        fill="none"
        stroke="currentColor"
        stroke-width="2.5"
        aria-hidden="true"
      >
        @switch (direction()) {
          @case ('ascending') {
            <path d="m6 15 6-6 6 6" />
          }
          @case ('descending') {
            <path d="m6 9 6 6 6-6" />
          }
          @default {
            <path d="m8 9 4-4 4 4M8 15l4 4 4-4" class="opacity-50" />
          }
        }
      </svg>
    </button>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SortHeader {
  /** The API's sort key for this column, e.g. "name". */
  readonly appSortHeader = input.required<string>();
  /** The current sort, e.g. "-name". */
  readonly sort = input.required<string>();
  readonly sortChange = output<string>();

  protected readonly direction = computed(() => {
    const sort = this.sort();
    if (sort === this.appSortHeader()) {
      return 'ascending';
    }
    return sort === `-${this.appSortHeader()}` ? 'descending' : 'none';
  });

  protected readonly ariaSort = computed(() =>
    this.direction() === 'none' ? null : this.direction(),
  );

  protected readonly next = computed(() =>
    this.direction() === 'ascending' ? `-${this.appSortHeader()}` : this.appSortHeader(),
  );

  protected readonly hint = computed(() =>
    this.direction() === 'ascending'
      ? '(sorted ascending; activate to sort descending)'
      : '(activate to sort ascending)',
  );
}
