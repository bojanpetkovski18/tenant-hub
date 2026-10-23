import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { LimitReached } from '../../shared/ui/limit-reached';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { JobPriorityLabel, JobStatusBadge, OverdueMarker } from './job-badges';
import {
  Job,
  JobHistoryEntry,
  JobTransitionOption,
  JobsApi,
  statusLabels,
  transitionLabels,
} from './jobs.api';
import { AskReason } from './reason-dialog';

const fieldLabels: Readonly<Record<string, string>> = {
  status: 'Status',
  reason: 'Reason',
  title: 'Title',
  description: 'Description',
  client: 'Client',
  assignee: 'Assignee',
  priority: 'Priority',
  scheduledOn: 'Scheduled',
  dueOn: 'Due',
  quotedAmount: 'Quoted amount',
};

/**
 * US-31 and US-32: one job, the moves the API allows this user right now (a move that still needs something is
 * shown disabled with what is missing), cancel and reopen with a reason, and the job's history from the audit log.
 */
@Component({
  selector: 'app-job-detail-page',
  imports: [
    RouterLink,
    DatePipe,
    EmptyState,
    ErrorState,
    JobStatusBadge,
    JobPriorityLabel,
    OverdueMarker,
    LimitReached,
  ],
  template: `
    <a [routerLink]="listLink()" class="text-sm text-muted hover:text-fg">← Jobs</a>

    @if (loading()) {
      <div class="mt-4 space-y-3" aria-busy="true">
        <div class="h-8 w-72 animate-pulse rounded bg-surface-2"></div>
        <div class="h-40 animate-pulse rounded bg-surface-2"></div>
      </div>
    } @else if (notFound()) {
      <div class="mt-4">
        <app-empty-state title="Job not found" message="It may have been deleted.">
          <a [routerLink]="listLink()" class="th-button-secondary">Back to jobs</a>
        </app-empty-state>
      </div>
    } @else if (loadError(); as error) {
      <div class="mt-4"><app-error-state [message]="error.title" (retry)="load()" /></div>
    } @else if (job(); as job) {
      <div class="mt-3 flex flex-wrap items-start justify-between gap-3">
        <div class="min-w-0">
          <p class="font-mono text-sm text-muted">{{ job.label }}</p>
          <h1 class="text-2xl font-semibold tracking-tight break-words">{{ job.title }}</h1>
          <div class="mt-2 flex flex-wrap items-center gap-3">
            <app-job-status-badge [status]="job.status" />
            <app-job-priority [priority]="job.priority" />
            @if (job.isOverdue) {
              <app-overdue-marker />
            }
          </div>
        </div>
        @if (job.canEdit || job.canDelete) {
          <div class="flex flex-wrap gap-2">
            @if (job.canEdit) {
              <a routerLink="edit" class="th-button-secondary">Edit</a>
            }
            @if (job.canDelete) {
              <button
                type="button"
                class="th-button-secondary text-danger"
                [disabled]="busy()"
                (click)="remove(job)"
              >
                Delete
              </button>
            }
          </div>
        }
      </div>

      @if (limit(); as problem) {
        <div class="mt-4"><app-limit-reached [problem]="problem" [slug]="slug()" /></div>
      } @else if (actionError(); as message) {
        <div class="th-alert-error mt-4" role="alert">{{ message }}</div>
      }
      @if (actionDone(); as message) {
        <div class="th-alert-success mt-4" role="status">{{ message }}</div>
      }

      @if (job.transitions.length > 0) {
        <section class="th-card mt-5" aria-labelledby="actions-heading">
          <h2 id="actions-heading" class="text-sm font-semibold">Status</h2>
          <div class="mt-3 flex flex-wrap gap-2">
            @for (option of job.transitions; track option.to) {
              <button
                type="button"
                [class]="
                  option.to === 'cancelled'
                    ? 'th-button-secondary text-danger'
                    : option.to === 'completed' ||
                        option.to === 'inProgress' ||
                        option.to === 'scheduled'
                      ? 'th-button'
                      : 'th-button-secondary'
                "
                [disabled]="busy() || !!option.blockedReason"
                [attr.aria-describedby]="option.blockedReason ? 'blocked-' + option.to : null"
                (click)="move(job, option)"
              >
                {{ label(job, option) }}
              </button>
            }
          </div>
          @for (option of job.transitions; track option.to) {
            @if (option.blockedReason) {
              <p [id]="'blocked-' + option.to" class="th-hint">
                {{ label(job, option) }}: {{ option.blockedReason }}
                @if (job.canEdit) {
                  <a routerLink="edit" class="text-accent underline">Edit the job</a>
                }
              </p>
            }
          }
        </section>
      }

      <section class="th-card mt-5" aria-labelledby="details-heading">
        <h2 id="details-heading" class="sr-only">Details</h2>
        <dl class="grid gap-x-6 gap-y-4 sm:grid-cols-2">
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Client</dt>
            <dd class="mt-1">
              <a [routerLink]="['/o', slug(), 'clients', job.clientId]" class="hover:underline">{{
                job.clientName
              }}</a>
            </dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Assignee</dt>
            <dd class="mt-1">{{ job.assigneeName ?? 'Unassigned' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Scheduled</dt>
            <dd class="mt-1">{{ job.scheduledOn ? (job.scheduledOn | date: 'fullDate') : '—' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Due</dt>
            <dd class="mt-1">{{ job.dueOn ? (job.dueOn | date: 'fullDate') : '—' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Quoted amount</dt>
            <dd class="mt-1">
              {{ job.quotedAmount === null ? '—' : money.format(job.quotedAmount) }}
            </dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Created</dt>
            <dd class="mt-1">{{ job.createdAt | date: 'medium' }} by {{ job.createdByName }}</dd>
          </div>
          @if (job.completedAt) {
            <div>
              <dt class="text-xs font-medium uppercase text-muted">Completed</dt>
              <dd class="mt-1">{{ job.completedAt | date: 'medium' }}</dd>
            </div>
          }
          <div class="sm:col-span-2">
            <dt class="text-xs font-medium uppercase text-muted">Description</dt>
            <dd class="mt-1 whitespace-pre-line">{{ job.description ?? '—' }}</dd>
          </div>
        </dl>
      </section>

      <section class="th-card mt-5" aria-labelledby="history-heading">
        <h2 id="history-heading" class="text-lg font-semibold">History</h2>
        @if (historyError(); as error) {
          <div class="mt-3">
            <app-error-state [message]="error.title" (retry)="loadHistory()" />
          </div>
        } @else if (history() === null) {
          <div class="mt-3 h-24 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
        } @else if (history()!.length === 0) {
          <p class="mt-2 text-sm text-muted">No history yet.</p>
        } @else {
          <ol class="mt-3 space-y-4 border-l border-border pl-4">
            @for (entry of history(); track entry.id) {
              <li>
                <p class="text-sm font-medium">{{ entry.summary }}</p>
                <p class="text-xs text-muted">
                  {{ entry.actorName }} ·
                  <time [attr.datetime]="entry.occurredAt">{{
                    entry.occurredAt | date: 'medium'
                  }}</time>
                </p>
                @if (entry.action !== 'job.created' && entry.changes.length > 0) {
                  <ul class="mt-1 space-y-0.5 text-xs text-muted">
                    @for (change of entry.changes; track change.field) {
                      <li>
                        {{ fieldLabel(change.field) }}:
                        @if (change.from !== null) {
                          <span class="line-through">{{ change.from }}</span> →
                        }
                        {{ change.to ?? 'none' }}
                      </li>
                    }
                  </ul>
                }
              </li>
            }
          </ol>
        }
      </section>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobDetailPage {
  readonly slug = input.required<string>();
  readonly id = input.required<string>();

  private readonly api = inject(JobsApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);
  private readonly askReason = inject(AskReason);
  private readonly router = inject(Router);

  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  // Absolute: ".." from a multi-segment route (jobs/:id) would leave the jobs section entirely.
  protected readonly listLink = computed(() => ['/o', this.slug(), 'jobs']);

  // The browser formats money; CurrencyPipe would add its currency table to the initial bundle (~9 kB).
  protected readonly money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'EUR' });
  protected readonly job = signal<Job | null>(null);
  protected readonly history = signal<JobHistoryEntry[] | null>(null);
  protected readonly loading = signal(true);
  protected readonly notFound = signal(false);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly historyError = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly limit = signal<ApiProblem | null>(null);
  protected readonly actionDone = signal<string | null>(null);

  constructor() {
    effect(() => {
      if (this.membership() && this.id()) {
        void this.load();
      }
    });
  }

  protected label(job: Job, option: JobTransitionOption): string {
    return job.status === 'completed' && option.to === 'inProgress'
      ? 'Reopen'
      : transitionLabels[option.to];
  }

  protected fieldLabel(field: string): string {
    return fieldLabels[field] ?? field;
  }

  protected async load(): Promise<void> {
    const membership = this.membership();
    if (!membership) {
      return;
    }
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.job.set(await this.api.get(membership.organizationId, this.id()));
      void this.loadHistory();
    } catch (error) {
      const problem = toApiProblem(error);
      this.notFound.set(problem.code === 'job-not-found');
      this.loadError.set(problem.code === 'job-not-found' ? null : problem);
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadHistory(): Promise<void> {
    this.historyError.set(null);
    try {
      this.history.set(await this.api.history(this.membership()!.organizationId, this.id()));
    } catch (error) {
      this.historyError.set(toApiProblem(error));
    }
  }

  protected async move(job: Job, option: JobTransitionOption): Promise<void> {
    let reason: string | undefined;
    if (option.needsReason) {
      const reopen = job.status === 'completed';
      const answer = await this.askReason.ask({
        title: reopen ? `Reopen ${job.label}?` : `Cancel ${job.label}?`,
        message: reopen
          ? 'It goes back to In progress. Say why, for the history.'
          : 'Cancelled jobs stay in the history and can be deleted later. Say why.',
        confirmLabel: reopen ? 'Reopen job' : 'Cancel job',
        danger: !reopen,
      });
      if (answer === null) {
        return;
      }
      reason = answer;
    }

    this.busy.set(true);
    this.actionError.set(null);
    this.limit.set(null);
    this.actionDone.set(null);
    try {
      const moved = await this.api.transition(
        this.membership()!.organizationId,
        job.id,
        option.to,
        reason,
      );
      this.job.set(moved);
      this.actionDone.set(`${moved.label} is now ${statusLabels[moved.status]}.`);
      void this.loadHistory();
    } catch (error) {
      const problem = toApiProblem(error);
      // Reopening counts toward the open-job limit (SPEC 5.5).
      if (problem.code === 'plan-limit-reached') {
        this.limit.set(problem);
        return;
      }
      // Someone else moved it first (409 invalid-transition): show what it is now and what is possible.
      this.actionError.set(problem.title);
      if (problem.code === 'invalid-transition') {
        await this.load();
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(job: Job): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Delete ${job.label}?`,
      message: 'The job is removed for good; its history stays in the audit log.',
      confirmLabel: 'Delete job',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await this.api.delete(this.membership()!.organizationId, job.id);
      await this.router.navigate(['/o', this.slug(), 'jobs']);
    } catch (error) {
      this.actionError.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
    }
  }
}
