import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';

/**
 * The one message for a reached plan limit (US-50), wherever it happens: the server's sentence ("Your Free plan
 * allows 3 seats, and 3 are in use.") and, for whoever may change the plan, a link to it.
 */
@Component({
  selector: 'app-limit-reached',
  imports: [RouterLink],
  template: `
    <div class="th-alert-error" role="alert">
      <p class="font-medium">{{ problem().title }}</p>
      @if (canChangePlan()) {
        <p class="mt-1">
          <a
            [routerLink]="['/o', slug(), 'settings']"
            fragment="plan"
            class="font-medium text-accent underline underline-offset-2"
            >See plans and usage</a
          >
        </p>
      } @else {
        <p class="mt-1 text-muted">Ask an Owner of this organization to change the plan.</p>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LimitReached {
  readonly problem = input.required<ApiProblem>();
  readonly slug = input.required<string>();

  private readonly session = inject(SessionStore);
  protected readonly canChangePlan = computed(() =>
    this.session.can(this.slug(), 'billing.manage'),
  );
}
