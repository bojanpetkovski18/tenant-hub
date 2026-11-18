import { CurrencyPipe, DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { toApiProblem } from '../../core/http/api-problem';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { Job, JobsApi } from './jobs.api';

/**
 * US-83, staff side: where the job's quote stands with the client, who decided, and "Send for approval" for those
 * who may (the API's <c>canSend</c>; <c>cannotSend</c> says why not). Shown when there is something to show.
 */
@Component({
  selector: 'app-job-quote-panel',
  imports: [CurrencyPipe, DatePipe],
  template: `
    @if (quote(); as q) {
      @if (q.status !== 'notRequested' || q.canSend || q.cannotSend) {
        <section class="th-card mt-5" aria-labelledby="quote-heading">
          <h2 id="quote-heading" class="text-lg font-semibold">Client approval</h2>
          @if (message(); as text) {
            <div class="th-alert-error mt-3" role="alert">{{ text }}</div>
          }
          @switch (q.status) {
            @case ('pending') {
              <p class="mt-2 text-sm" role="status">
                Waiting for the client: sent {{ q.sentAt | date: 'd MMM y' }}
                @if (q.sentByName) {
                  by {{ q.sentByName }}
                }
                for {{ job().quotedAmount | currency: 'EUR' }}. Work cannot start until it is
                approved.
              </p>
            }
            @case ('approved') {
              <p class="mt-2 text-sm">
                <span class="th-badge bg-success/15 text-fg">Approved</span>
                {{ q.approvedAmount | currency: 'EUR' }} by {{ q.decidedByName ?? 'the client' }} on
                {{ q.decidedAt | date: 'd MMM y' }}.
              </p>
            }
            @case ('declined') {
              <p class="mt-2 text-sm">
                <span class="th-badge">Declined</span>
                by {{ q.decidedByName ?? 'the client' }} on {{ q.decidedAt | date: 'd MMM y' }}.
                Change the job and send a new quote before work starts.
              </p>
            }
            @default {
              <p class="mt-2 text-sm text-muted">
                The client's portal contacts can approve the quoted amount before work starts.
              </p>
            }
          }
          @if (q.comment) {
            <p class="mt-2 text-sm">
              <span class="text-muted">Their comment:</span> “{{ q.comment }}”
            </p>
          }
          @if (q.canSend) {
            <button type="button" class="th-button mt-3" [disabled]="busy()" (click)="send()">
              Send for approval
            </button>
          } @else if (q.cannotSend && q.status === 'notRequested') {
            <p class="mt-2 text-sm text-muted">{{ q.cannotSend }}</p>
          }
        </section>
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobQuotePanel {
  readonly job = input.required<Job>();
  readonly organizationId = input.required<string>();
  /** The job as the API returned it after sending. */
  readonly changed = output<Job>();

  private readonly api = inject(JobsApi);
  private readonly confirm = inject(Confirm);

  protected readonly quote = computed(() => this.job().quote ?? null);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);

  protected async send(): Promise<void> {
    const job = this.job();
    const confirmed = await this.confirm.ask({
      title: `Send the quote for ${job.label}?`,
      message:
        "The client's portal contacts can then approve or decline it. Work cannot start until they approve.",
      confirmLabel: 'Send for approval',
    });
    if (!confirmed) {
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    try {
      this.changed.emit(await this.api.sendQuote(this.organizationId(), job.id));
    } catch (error) {
      this.message.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
    }
  }
}
