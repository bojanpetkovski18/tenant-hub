import { CurrencyPipe, DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { ErrorState } from '../../shared/ui/states';
import { JobStatusBadge } from '../jobs/job-badges';
import { priorityLabels } from '../jobs/jobs.api';
import { AgreementStatusBadge } from './agreement-status-badge';
import { Agreement, AgreementsApi, holdMessages } from './agreements.api';

/**
 * US-70, US-71: one agreement. What it does (template and schedule), whether visits are being generated, the next
 * visits, and the visits changed by hand, which edits to the agreement no longer reach (SPEC-V2 6).
 */
@Component({
  selector: 'app-agreement-detail-page',
  imports: [RouterLink, DatePipe, CurrencyPipe, ErrorState, AgreementStatusBadge, JobStatusBadge],
  template: `
    <div class="space-y-5">
      <a routerLink=".." class="text-sm text-muted hover:underline">← Agreements</a>

      @if (loading() && !agreement()) {
        <div class="space-y-3" aria-busy="true">
          <div class="h-8 w-72 animate-pulse rounded bg-surface-2"></div>
          <div class="h-48 animate-pulse rounded-lg bg-surface-2"></div>
        </div>
      } @else if (notFound()) {
        <h1 class="text-2xl font-semibold tracking-tight">Agreement not found</h1>
        <p class="text-muted">It may have been deleted with its client, or the link is wrong.</p>
      } @else if (loadError(); as error) {
        <app-error-state [message]="error.title" (retry)="load()" />
      } @else if (agreement(); as a) {
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div class="min-w-0">
            <h1 class="text-2xl font-semibold tracking-tight break-words">{{ a.title }}</h1>
            <p class="mt-1 flex flex-wrap items-center gap-2 text-sm text-muted">
              <app-agreement-status-badge [status]="a.status" [hold]="a.pausedReason" />
              <span>{{ a.schedule }}</span>
              <span aria-hidden="true">·</span>
              <a [routerLink]="['/o', slug(), 'clients', a.clientId]" class="hover:underline">{{
                a.clientName
              }}</a>
            </p>
          </div>
          @if (a.canManage && a.status !== 'ended') {
            <div class="flex flex-wrap gap-2">
              <a routerLink="edit" class="th-button-secondary">Edit</a>
              @if (a.status === 'active') {
                <button
                  type="button"
                  class="th-button-secondary"
                  [disabled]="busy()"
                  (click)="act(a, 'pause')"
                >
                  Pause
                </button>
              } @else {
                <button
                  type="button"
                  class="th-button-secondary"
                  [disabled]="busy()"
                  (click)="act(a, 'resume')"
                >
                  Resume
                </button>
              }
              <button type="button" class="th-button-danger" [disabled]="busy()" (click)="end(a)">
                End agreement
              </button>
            </div>
          }
        </div>

        @if (actionError(); as message) {
          <div class="th-alert-error" role="alert">{{ message }}</div>
        }
        @if (notice(); as message) {
          <div class="th-alert-success" role="status">{{ message }}</div>
        }
        @if (a.pausedReason; as hold) {
          <div class="th-alert-error" role="note">{{ holdMessages[hold] }}</div>
        }

        <section class="th-card" aria-labelledby="template-heading">
          <h2 id="template-heading" class="font-semibold">Each visit</h2>
          <dl class="mt-3 grid gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
            <div>
              <dt class="text-xs font-semibold uppercase tracking-wide text-muted">Assignee</dt>
              <dd>{{ a.assigneeName ?? 'Unassigned (visits are New)' }}</dd>
            </div>
            <div>
              <dt class="text-xs font-semibold uppercase tracking-wide text-muted">Priority</dt>
              <dd>{{ priorityLabels[a.priority] }}</dd>
            </div>
            <div>
              <dt class="text-xs font-semibold uppercase tracking-wide text-muted">
                Quoted per visit
              </dt>
              <dd>{{ a.quotedAmount === null ? '—' : (a.quotedAmount | currency: 'EUR') }}</dd>
            </div>
            <div>
              <dt class="text-xs font-semibold uppercase tracking-wide text-muted">
                Estimated time
              </dt>
              <dd>{{ a.estimatedMinutes === null ? '—' : a.estimatedMinutes + ' minutes' }}</dd>
            </div>
            <div>
              <dt class="text-xs font-semibold uppercase tracking-wide text-muted">Starts</dt>
              <dd>{{ a.startsOn | date: 'EEE d MMM y' }}</dd>
            </div>
            <div>
              <dt class="text-xs font-semibold uppercase tracking-wide text-muted">Ends</dt>
              <dd>
                @if (a.maxVisits !== null) {
                  After {{ a.maxVisits }} {{ a.maxVisits === 1 ? 'visit' : 'visits' }}
                } @else if (a.endsOn) {
                  {{ a.endsOn | date: 'EEE d MMM y' }}
                } @else {
                  No end date
                }
              </dd>
            </div>
            @if (a.description) {
              <div class="sm:col-span-2">
                <dt class="text-xs font-semibold uppercase tracking-wide text-muted">
                  Description
                </dt>
                <dd class="whitespace-pre-line">{{ a.description }}</dd>
              </div>
            }
          </dl>
        </section>

        <section class="th-card" aria-labelledby="visits-heading">
          <div class="flex flex-wrap items-center justify-between gap-2">
            <h2 id="visits-heading" class="font-semibold">Next visits</h2>
            <a
              [routerLink]="['/o', slug(), 'jobs']"
              [queryParams]="{ agreementId: a.id }"
              class="text-sm font-medium text-accent hover:underline"
              >All visits</a
            >
          </div>
          @if (a.generatedThrough) {
            <p class="mt-1 text-sm text-muted">
              Visits exist up to {{ a.generatedThrough | date: 'd MMM y' }}; more are added each
              day.
            </p>
          }
          @if (a.upcomingVisits.length === 0) {
            <p class="mt-3 text-sm text-muted">No visits ahead.</p>
          } @else {
            <ul class="mt-3 divide-y divide-border text-sm">
              @for (visit of a.upcomingVisits; track visit.id) {
                <li class="flex flex-wrap items-center justify-between gap-2 py-2">
                  <a
                    [routerLink]="['/o', slug(), 'jobs', visit.id]"
                    class="font-medium hover:underline"
                  >
                    {{ visit.occurrenceOn | date: 'EEE d MMM' }} · {{ visit.label }}
                  </a>
                  <span class="flex items-center gap-2 text-muted">
                    {{ visit.assigneeName ?? 'Unassigned' }}
                    <app-job-status-badge [status]="visit.status" />
                  </span>
                </li>
              }
            </ul>
          }
        </section>

        <section class="th-card" aria-labelledby="by-hand-heading">
          <h2 id="by-hand-heading" class="font-semibold">Visits changed by hand</h2>
          <p class="mt-1 text-sm text-muted">
            Someone edited these visits, so changes to the agreement leave them as they are.
          </p>
          @if (a.changedByHand.length === 0) {
            <p class="mt-3 text-sm text-muted">None.</p>
          } @else {
            <ul class="mt-3 divide-y divide-border text-sm">
              @for (visit of a.changedByHand; track visit.id) {
                <li class="py-2">
                  <a
                    [routerLink]="['/o', slug(), 'jobs', visit.id]"
                    class="font-medium hover:underline"
                  >
                    {{ visit.label }}, the visit of {{ visit.occurrenceOn | date: 'EEE d MMM' }}
                  </a>
                </li>
              }
            </ul>
          }
        </section>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgreementDetailPage {
  readonly slug = input.required<string>();
  readonly id = input.required<string>();

  private readonly api = inject(AgreementsApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));

  protected readonly holdMessages = holdMessages;
  protected readonly priorityLabels = priorityLabels;
  protected readonly agreement = signal<Agreement | null>(null);
  protected readonly loading = signal(true);
  protected readonly notFound = signal(false);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  constructor() {
    effect(() => {
      if (this.membership() && this.id()) {
        void this.load();
      }
    });
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.agreement.set(await this.api.get(this.membership()!.organizationId, this.id()));
    } catch (error) {
      const problem = toApiProblem(error);
      this.notFound.set(problem.code === 'agreement-not-found');
      this.loadError.set(problem.code === 'agreement-not-found' ? null : problem);
    } finally {
      this.loading.set(false);
    }
  }

  protected async act(a: Agreement, action: 'pause' | 'resume'): Promise<void> {
    await this.run(
      () => this.api.act(this.membership()!.organizationId, a.id, action),
      action === 'pause'
        ? 'Paused. No new visits are added; the existing ones stay.'
        : 'Resumed. Visits are added from today on.',
    );
  }

  protected async end(a: Agreement): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `End “${a.title}”?`,
      message:
        'Visits from today on that are New or Scheduled are cancelled; started and past visits stay. An ended agreement cannot be resumed.',
      confirmLabel: 'End agreement',
      danger: true,
    });
    if (confirmed) {
      await this.run(() => this.api.act(this.membership()!.organizationId, a.id, 'end'), 'Ended.');
    }
  }

  private async run(call: () => Promise<Agreement>, done: string): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    this.notice.set(null);
    try {
      this.agreement.set(await call());
      this.notice.set(done);
    } catch (error) {
      this.actionError.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
    }
  }
}
