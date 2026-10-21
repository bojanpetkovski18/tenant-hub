import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * Shown in place of the app shell when the URL names an organization the user does not belong to. It reads the
 * same whether the organization exists or not, so the page does not reveal other tenants (SPEC 3.2).
 */
@Component({
  selector: 'app-org-not-found',
  imports: [RouterLink],
  template: `
    <main
      id="main"
      tabindex="-1"
      class="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-16"
    >
      <p class="text-sm font-semibold text-accent">404</p>
      <h1 class="mt-2 text-3xl font-semibold tracking-tight">Organization not found</h1>
      <p class="mt-3 text-muted">
        There is no organization “{{ slug() }}” in your account. It may not exist, or you may not
        have access to it.
      </p>
      <a routerLink="/orgs" class="th-button mt-8 w-fit">Go to your organizations</a>
    </main>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrgNotFound {
  readonly slug = input.required<string>();
}
