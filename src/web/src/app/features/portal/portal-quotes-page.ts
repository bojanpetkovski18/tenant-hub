import { CurrencyPipe, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { AskReason } from '../jobs/reason-dialog';
import { PortalApi, PortalQuote } from './portal.api';

/**
 * US-83: quotes waiting for the client's decision, and those decided in the last 90 days. Approving or declining is
 * once, with an optional comment; until it is approved, the work does not start.
 */
@Component({
  selector: 'app-portal-quotes-page',
  imports: [CurrencyPipe, DatePipe, EmptyState, ErrorState],
  template: `
    <h1 class="text-2xl font-semibold tracking-tight">Quotes</h1>
    <p class="mt-1 text-sm text-muted">Work starts only once you have approved its price.</p>
    <p class="sr-only" aria-live="polite">{{ announcement() }}</p>
    @if (message(); as text) {
      <div class="th-alert-error mt-4" role="alert">{{ text }}</div>
    }

    <div class="mt-4">
      @if (error(); as problem) {
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (!quotes()) {
        <div class="h-40 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else if (quotes()!.length === 0) {
        <app-empty-state
          title="No quotes"
          message="When the business sends you a price to approve, it shows here."
        />
      } @else {
        @if (pending().length > 0) {
          <section aria-labelledby="pending-heading">
            <h2 id="pending-heading" class="font-semibold">Waiting for your decision</h2>
            <ul class="mt-2 space-y-3">
              @for (quote of pending(); track quote.jobId) {
                <li class="th-card">
                  <div class="flex flex-wrap items-start justify-between gap-3">
                    <div class="min-w-0">
                      <p class="font-mono text-xs text-muted">{{ quote.label }}</p>
                      <p class="font-medium">{{ quote.title }}</p>
                      <p class="text-sm text-muted">
                        {{
                          quote.scheduledOn
                            ? (quote.scheduledOn | date: 'EEE d MMM y')
                            : 'Date to be agreed'
                        }}
                      </p>
                    </div>
                    <p class="text-xl font-semibold">{{ quote.amount | currency: 'EUR' }}</p>
                  </div>
                  <div class="mt-3 flex flex-wrap gap-2">
                    <button
                      type="button"
                      class="th-button"
                      [disabled]="busy()"
                      (click)="decide(quote, true)"
                    >
                      Approve<span class="sr-only"> {{ quote.label }}</span>
                    </button>
                    <button
                      type="button"
                      class="th-button-secondary"
                      [disabled]="busy()"
                      (click)="decide(quote, false)"
                    >
                      Decline<span class="sr-only"> {{ quote.label }}</span>
                    </button>
                  </div>
                </li>
              }
            </ul>
          </section>
        }
        @if (decided().length > 0) {
          <section class="mt-6" aria-labelledby="decided-heading">
            <h2 id="decided-heading" class="font-semibold">Decided</h2>
            <ul class="mt-2 space-y-2">
              @for (quote of decided(); track quote.jobId) {
                <li class="th-card py-3 text-sm">
                  <div class="flex flex-wrap items-center justify-between gap-2">
                    <span class="font-medium">{{ quote.label }} · {{ quote.title }}</span>
                    <span
                      [class]="
                        quote.status === 'approved' ? 'th-badge bg-success/15 text-fg' : 'th-badge'
                      "
                    >
                      {{ quote.status === 'approved' ? 'Approved' : 'Declined' }} ·
                      {{ quote.amount | currency: 'EUR' }}
                    </span>
                  </div>
                  <p class="mt-1 text-muted">
                    {{ quote.decidedByName ?? 'Someone' }} on
                    {{ quote.decidedAt | date: 'd MMM y' }}
                    @if (quote.comment) {
                      · “{{ quote.comment }}”
                    }
                  </p>
                </li>
              }
            </ul>
          </section>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalQuotesPage {
  readonly slug = input.required<string>();

  private readonly api = inject(PortalApi);
  private readonly askReason = inject(AskReason);

  protected readonly quotes = signal<PortalQuote[] | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly announcement = signal('');
  protected readonly pending = computed(() =>
    (this.quotes() ?? []).filter((q) => q.status === 'pending'),
  );
  protected readonly decided = computed(() =>
    (this.quotes() ?? []).filter((q) => q.status !== 'pending'),
  );

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.error.set(null);
    try {
      this.quotes.set(await this.api.quotes());
    } catch (error) {
      this.error.set(toApiProblem(error));
    }
  }

  protected async decide(quote: PortalQuote, approve: boolean): Promise<void> {
    const comment = await this.askReason.ask({
      title: `${approve ? 'Approve' : 'Decline'} the quote for ${quote.label}?`,
      message: approve
        ? 'The business can then start the work at this price.'
        : 'The business will see that you declined, and why if you say.',
      confirmLabel: approve ? 'Approve' : 'Decline',
      danger: !approve,
      label: 'Comment',
      optional: true,
      maxLength: 1000,
    });
    if (comment === null) {
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    try {
      await this.api.decide(quote.jobId, approve, comment || null);
      this.announcement.set(`${approve ? 'Approved' : 'Declined'} the quote for ${quote.label}.`);
    } catch (error) {
      this.message.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
      await this.load();
    }
  }
}
