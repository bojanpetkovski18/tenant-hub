import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SessionStore } from '../session/session.store';
import { ThemeToggle } from '../theme/theme-toggle';
import { SkipLink } from './skip-link';
import { UserMenu } from './user-menu';
import { Wordmark } from './wordmark';

/** Layout for pages outside an organization: landing, sign-in, sign-up, invitations, picker, account. */
@Component({
  selector: 'app-public-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeToggle, SkipLink, UserMenu, Wordmark],
  template: `
    <app-skip-link />
    <div class="flex min-h-dvh flex-col">
      <header class="border-b border-border bg-surface">
        <div class="mx-auto flex h-16 max-w-6xl items-center justify-between gap-4 px-4 sm:px-6">
          <a routerLink="/" class="rounded-md" aria-label="TenantHub home"><app-wordmark /></a>
          <nav aria-label="Account" class="flex items-center gap-1 sm:gap-2">
            @if (session.isSignedIn()) {
              <a
                routerLink="/orgs"
                routerLinkActive
                ariaCurrentWhenActive="page"
                class="th-nav-link"
                >Organizations</a
              >
              <app-theme-toggle />
              <app-user-menu />
            } @else {
              <a
                routerLink="/sign-in"
                routerLinkActive
                ariaCurrentWhenActive="page"
                class="th-nav-link"
                >Sign in</a
              >
              <a
                routerLink="/sign-up"
                class="inline-flex min-h-10 items-center rounded-md bg-accent px-3 text-sm font-semibold text-on-accent hover:bg-accent-strong"
                >Sign up</a
              >
              <app-theme-toggle />
            }
          </nav>
        </div>
      </header>
      <main id="main" tabindex="-1" class="mx-auto w-full max-w-6xl flex-1 px-4 py-10 sm:px-6">
        <router-outlet />
      </main>
      <footer class="border-t border-border">
        <p class="mx-auto max-w-6xl px-4 py-6 text-sm text-muted sm:px-6">
          TenantHub is a portfolio project built with AI assistance. All data is fictional.
        </p>
      </footer>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PublicLayout {
  protected readonly session = inject(SessionStore);
}
