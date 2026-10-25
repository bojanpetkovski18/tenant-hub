import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { SessionStore } from '../session/session.store';

/** The signed-in user's menu: organizations, account and sign-out (keyboard support from the CDK menu). */
@Component({
  selector: 'app-user-menu',
  imports: [CdkMenuTrigger, CdkMenu, CdkMenuItem, RouterLink],
  template: `
    @if (session.user(); as user) {
      <button
        type="button"
        class="flex size-10 items-center justify-center rounded-full bg-surface-2 text-sm font-semibold text-fg hover:bg-border"
        [cdkMenuTriggerFor]="menu"
        data-testid="user-menu"
      >
        <!-- Named by content, not aria-label, so the visible initials never contradict the name (WCAG 2.5.3). -->
        <span aria-hidden="true">{{ initials() }}</span>
        <span class="sr-only">Account menu for {{ user.displayName }}</span>
      </button>

      <ng-template #menu>
        <div class="th-menu" cdkMenu>
          <div class="border-b border-border px-3 pb-2 pt-1.5">
            <p class="truncate text-sm font-medium">{{ user.displayName }}</p>
            <p class="truncate text-xs text-muted">{{ user.email }}</p>
          </div>
          <a class="th-menu-item mt-1" cdkMenuItem routerLink="/orgs">Your organizations</a>
          <a class="th-menu-item" cdkMenuItem routerLink="/account">Account</a>
          <button type="button" class="th-menu-item" cdkMenuItem (cdkMenuItemTriggered)="signOut()">
            Sign out
          </button>
        </div>
      </ng-template>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserMenu {
  protected readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly initials = computed(() =>
    (this.session.user()?.displayName ?? '?')
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part.charAt(0).toUpperCase())
      .join(''),
  );

  protected async signOut(): Promise<void> {
    await this.session.signOut();
    await this.router.navigateByUrl('/sign-in');
  }
}
