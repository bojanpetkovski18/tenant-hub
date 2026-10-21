import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { organizationHome } from '../session/landing-path';
import { Membership, roleLabels } from '../session/session.models';
import { SessionStore } from '../session/session.store';

/** Switches between the user's organizations (US-03); the organization is part of the URL, so tabs are independent. */
@Component({
  selector: 'app-org-switcher',
  imports: [CdkMenuTrigger, CdkMenu, CdkMenuItem, RouterLink],
  template: `
    <button
      type="button"
      class="flex w-full items-center gap-3 rounded-md border border-border bg-surface px-3 py-2 text-left hover:bg-surface-2"
      [cdkMenuTriggerFor]="menu"
      data-testid="org-switcher"
    >
      <!-- Named by its visible text plus this prefix, so the name contains what is shown (WCAG 2.5.3). -->
      <span class="sr-only">Switch organization. Current:</span>
      <span
        class="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent text-sm font-semibold uppercase text-on-accent"
        aria-hidden="true"
        >{{ current().name.charAt(0) }}</span
      >
      <span class="min-w-0 flex-1">
        <span class="block truncate text-sm font-medium" data-testid="current-org">{{
          current().name
        }}</span>
        <span class="block text-xs text-muted">{{ roleLabels[current().role] }}</span>
      </span>
      <svg
        viewBox="0 0 24 24"
        class="size-4 shrink-0 text-muted"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        aria-hidden="true"
      >
        <path d="m7 10 5 5 5-5" />
      </svg>
    </button>

    <ng-template #menu>
      <div class="th-menu w-64" cdkMenu>
        @for (membership of session.memberships(); track membership.organizationId) {
          <a
            class="th-menu-item"
            cdkMenuItem
            [routerLink]="home(membership.slug)"
            [attr.aria-current]="membership.slug === current().slug ? 'true' : null"
          >
            <span class="min-w-0 flex-1 truncate">{{ membership.name }}</span>
            @if (membership.slug === current().slug) {
              <svg
                viewBox="0 0 24 24"
                class="size-4 text-accent"
                fill="none"
                stroke="currentColor"
                stroke-width="2.5"
                aria-hidden="true"
              >
                <path d="m5 12 5 5 9-10" />
              </svg>
            }
          </a>
        }
        <div class="my-1 border-t border-border" role="separator"></div>
        <a class="th-menu-item" cdkMenuItem routerLink="/orgs">All organizations</a>
      </div>
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrgSwitcher {
  readonly current = input.required<Membership>();

  protected readonly session = inject(SessionStore);
  protected readonly roleLabels = roleLabels;
  protected readonly home = organizationHome;
}
