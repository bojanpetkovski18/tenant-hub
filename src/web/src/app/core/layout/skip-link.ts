import { ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';

/**
 * "Skip to content" for keyboard users. With <base href="/">, a plain href="#main" resolves to "/#main" and
 * leaves the current page, so the click is cancelled and focus moves to <main id="main" tabindex="-1">.
 * The href stays for semantics (it is still a link to the content).
 */
@Component({
  selector: 'app-skip-link',
  template: `
    <a
      href="#main"
      class="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-50 focus:rounded-md focus:bg-surface focus:px-4 focus:py-2"
      (click)="skip($event)"
      >Skip to content</a
    >
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SkipLink {
  private readonly document = inject(DOCUMENT);

  protected skip(event: Event): void {
    event.preventDefault();
    this.document.getElementById('main')?.focus();
  }
}
