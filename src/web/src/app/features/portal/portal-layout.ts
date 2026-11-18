import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SkipLink } from '../../core/layout/skip-link';
import { ThemeToggle } from '../../core/theme/theme-toggle';
import { PortalSessionStore, initials } from './portal-session';

/**
 * The portal's own small layout (SPEC-V2 6): the business's name and initials, the contact's pages, both themes and a
 * skip link. Nothing of the staff app (no organization switcher, no staff navigation) appears here.
 */
@Component({
  selector: 'app-portal-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SkipLink, ThemeToggle],
  template: `
    <app-skip-link />
    <div class="flex min-h-dvh flex-col">
      <header class="border-b border-border bg-surface">
        <div
          class="mx-auto flex max-w-4xl flex-wrap items-center justify-between gap-3 px-4 py-3 sm:px-6"
        >
          <div class="flex min-w-0 items-center gap-3">
            @if (organization(); as org) {
              <span
                class="flex size-10 shrink-0 items-center justify-center rounded-full bg-accent font-semibold text-on-accent"
                aria-hidden="true"
                >{{ orgInitials() }}</span
              >
              <div class="min-w-0">
                <p class="truncate font-semibold">{{ org.name }}</p>
                <p class="text-xs text-muted">Client portal</p>
              </div>
            } @else {
              <p class="font-semibold">Client portal</p>
            }
          </div>
          <div class="flex items-center gap-2">
            @if (contact(); as who) {
              <span class="hidden text-sm text-muted sm:inline"
                >{{ who.name }}, {{ who.clientName }}</span
              >
              <button type="button" class="th-button-secondary" (click)="signOut()">
                Sign out
              </button>
            }
            <app-theme-toggle />
          </div>
        </div>
        @if (contact()) {
          <nav
            class="mx-auto flex max-w-4xl gap-1 overflow-x-auto px-4 pb-2 sm:px-6"
            aria-label="Portal"
          >
            @for (item of nav; track item.path) {
              <a
                [routerLink]="['/portal', slug(), item.path]"
                routerLinkActive
                ariaCurrentWhenActive="page"
                class="th-nav-link"
                >{{ item.label }}</a
              >
            }
          </nav>
        }
      </header>
      <main id="main" tabindex="-1" class="mx-auto w-full max-w-4xl flex-1 px-4 py-8 sm:px-6">
        <router-outlet />
      </main>
      <footer class="border-t border-border">
        <p class="mx-auto max-w-4xl px-4 py-6 text-sm text-muted sm:px-6">
          A client portal on TenantHub, a portfolio project built with AI assistance. All data is
          fictional.
        </p>
      </footer>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalLayout {
  /** Route parameter: the business's portal. */
  readonly slug = input.required<string>();

  private readonly store = inject(PortalSessionStore);
  private readonly router = inject(Router);

  protected readonly nav = [
    { path: 'visits', label: 'Visits' },
    { path: 'quotes', label: 'Quotes' },
    { path: 'requests', label: 'Requests' },
  ] as const;
  protected readonly organization = computed(() => this.store.session()?.organization ?? null);
  protected readonly contact = computed(() => this.store.session()?.contact ?? null);
  protected readonly orgInitials = computed(() => initials(this.organization()?.name ?? ''));

  constructor() {
    effect(() => void this.store.load(this.slug()));
  }

  protected async signOut(): Promise<void> {
    await this.store.signOut();
    await this.router.navigate(['/portal', this.slug(), 'sign-in'], {
      queryParams: { notice: 'signed-out' },
    });
  }
}
