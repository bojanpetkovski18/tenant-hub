import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ThemeStore } from './theme.store';

@Component({
  selector: 'app-theme-toggle',
  template: `
    <button
      type="button"
      class="th-icon-button"
      [attr.aria-label]="label()"
      (click)="store.toggle()"
    >
      @if (store.theme() === 'dark') {
        <svg
          viewBox="0 0 24 24"
          class="size-5"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          aria-hidden="true"
        >
          <circle cx="12" cy="12" r="4" />
          <path
            d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"
          />
        </svg>
      } @else {
        <svg
          viewBox="0 0 24 24"
          class="size-5"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          aria-hidden="true"
        >
          <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z" />
        </svg>
      }
    </button>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ThemeToggle {
  protected readonly store = inject(ThemeStore);
  protected readonly label = computed(() =>
    this.store.theme() === 'dark' ? 'Switch to light theme' : 'Switch to dark theme',
  );
}
