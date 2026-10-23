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
import { PagedList } from '../../shared/list/paged-list.store';
import { JobStatusBadge } from '../jobs/job-badges';
import { JobListItem, JobsApi } from '../jobs/jobs.api';
import { ClientStatusBadge } from './client-status-badge';
import { Client, ClientsApi } from './clients.api';

/** US-20: one client, with the actions the user's permissions allow. */
@Component({
  selector: 'app-client-detail-page',
  imports: [
    RouterLink,
    DatePipe,
    EmptyState,
    ErrorState,
    ClientStatusBadge,
    JobStatusBadge,
    LimitReached,
  ],
  template: `
    <a [routerLink]="listLink()" class="text-sm text-muted hover:text-fg">← Clients</a>

    @if (loading()) {
      <div class="mt-4 space-y-3" aria-busy="true">
        <div class="h-8 w-64 animate-pulse rounded bg-surface-2"></div>
        <div class="h-32 animate-pulse rounded bg-surface-2"></div>
      </div>
    } @else if (notFound()) {
      <div class="mt-4">
        <app-empty-state title="Client not found" message="It may have been deleted.">
          <a [routerLink]="listLink()" class="th-button-secondary">Back to clients</a>
        </app-empty-state>
      </div>
    } @else if (loadError(); as error) {
      <div class="mt-4"><app-error-state [message]="error.title" (retry)="load()" /></div>
    } @else if (client(); as client) {
      <div class="mt-3 flex flex-wrap items-start justify-between gap-3">
        <div class="flex flex-wrap items-center gap-3">
          <h1 class="text-2xl font-semibold tracking-tight">{{ client.name }}</h1>
          <app-client-status-badge [status]="client.status" />
        </div>
        @if (canWrite() || canDelete()) {
          <div class="flex flex-wrap gap-2">
            @if (canWrite()) {
              <a routerLink="edit" class="th-button-secondary">Edit</a>
              @if (client.status === 'active') {
                <button
                  type="button"
                  class="th-button-secondary"
                  [disabled]="busy()"
                  (click)="archive(client)"
                >
                  Archive
                </button>
              } @else {
                <button
                  type="button"
                  class="th-button-secondary"
                  [disabled]="busy()"
                  (click)="restore(client)"
                >
                  Restore
                </button>
              }
            }
            @if (canDelete()) {
              <button
                type="button"
                class="th-button-secondary text-danger"
                [disabled]="busy()"
                (click)="remove(client)"
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

      <section class="th-card mt-5" aria-labelledby="details-heading">
        <h2 id="details-heading" class="sr-only">Details</h2>
        <dl class="grid gap-x-6 gap-y-4 sm:grid-cols-2">
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Contact person</dt>
            <dd class="mt-1">{{ client.contactName ?? '—' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Email</dt>
            <dd class="mt-1 break-all">{{ client.email ?? '—' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Phone</dt>
            <dd class="mt-1">{{ client.phone ?? '—' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Address</dt>
            <dd class="mt-1">{{ client.address ?? '—' }}</dd>
          </div>
          <div class="sm:col-span-2">
            <dt class="text-xs font-medium uppercase text-muted">Notes</dt>
            <dd class="mt-1 whitespace-pre-line">{{ client.notes ?? '—' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Created</dt>
            <dd class="mt-1">{{ client.createdAt | date: 'medium' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium uppercase text-muted">Last updated</dt>
            <dd class="mt-1">{{ client.updatedAt | date: 'medium' }}</dd>
          </div>
        </dl>
      </section>

      <section class="th-card mt-5" aria-labelledby="jobs-heading">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <h2 id="jobs-heading" class="text-lg font-semibold">Jobs</h2>
          @if (canCreateJob() && client.status === 'active') {
            <a
              [routerLink]="['/o', slug(), 'jobs', 'new']"
              [queryParams]="{ clientId: client.id }"
              class="th-button-secondary"
              >New job</a
            >
          }
        </div>
        @if (jobsError(); as error) {
          <div class="mt-3"><app-error-state [message]="error.title" (retry)="loadJobs()" /></div>
        } @else if (jobs() === null) {
          <div class="mt-3 h-16 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
        } @else if (jobs()!.total === 0) {
          <p class="mt-2 text-sm text-muted">No jobs for this client yet.</p>
        } @else {
          <ul class="mt-3 divide-y divide-border" aria-label="This client's latest jobs">
            @for (job of jobs()!.items; track job.id) {
              <li class="flex flex-wrap items-center justify-between gap-2 py-2">
                <a [routerLink]="['/o', slug(), 'jobs', job.id]" class="min-w-0 hover:underline">
                  <span class="font-mono text-xs text-muted">{{ job.label }}</span>
                  {{ job.title }}
                </a>
                <app-job-status-badge [status]="job.status" />
              </li>
            }
          </ul>
          <a
            [routerLink]="['/o', slug(), 'jobs']"
            [queryParams]="{ clientId: client.id }"
            class="mt-3 inline-block text-sm text-accent underline-offset-2 hover:underline"
            >View all {{ jobs()!.total }} jobs, with filters</a
          >
        }
      </section>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClientDetailPage {
  readonly slug = input.required<string>();
  readonly id = input.required<string>();

  private readonly api = inject(ClientsApi);
  private readonly jobsApi = inject(JobsApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);
  private readonly router = inject(Router);

  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canWrite = computed(() => this.session.can(this.slug(), 'clients.write'));
  // Absolute: ".." from a multi-segment route (clients/:id) would leave the clients section entirely.
  protected readonly listLink = computed(() => ['/o', this.slug(), 'clients']);
  protected readonly canDelete = computed(() => this.session.can(this.slug(), 'clients.delete'));
  protected readonly canCreateJob = computed(() => this.session.can(this.slug(), 'jobs.create'));

  /** The client's latest jobs (SPEC 4.1) and how many are open, for the archive warning (SPEC 5.4). */
  protected readonly jobs = signal<PagedList<JobListItem> | null>(null);
  protected readonly jobsError = signal<ApiProblem | null>(null);
  private openJobs = 0;

  protected readonly client = signal<Client | null>(null);
  protected readonly loading = signal(true);
  protected readonly notFound = signal(false);
  protected readonly loadError = signal<ApiProblem | null>(null);
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

  protected async load(): Promise<void> {
    const membership = this.membership();
    if (!membership) {
      return;
    }
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.client.set(await this.api.get(membership.organizationId, this.id()));
      void this.loadJobs();
    } catch (error) {
      const problem = toApiProblem(error);
      this.notFound.set(problem.code === 'client-not-found');
      this.loadError.set(problem.code === 'client-not-found' ? null : problem);
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadJobs(): Promise<void> {
    const organizationId = this.membership()!.organizationId;
    this.jobsError.set(null);
    try {
      const [latest, open] = await Promise.all([
        this.jobsApi.list(organizationId, { clientId: this.id(), sort: '-number', pageSize: '5' }),
        this.jobsApi.list(organizationId, {
          clientId: this.id(),
          status: ['new', 'scheduled', 'inProgress'],
          pageSize: '1',
        }),
      ]);
      this.jobs.set(latest);
      this.openJobs = open.total;
    } catch (error) {
      this.jobsError.set(toApiProblem(error));
    }
  }

  protected async archive(client: Client): Promise<void> {
    const open =
      this.openJobs === 0
        ? ''
        : ` It has ${this.openJobs} open ${this.openJobs === 1 ? 'job' : 'jobs'}; they stay as they are.`;
    const confirmed = await this.confirm.ask({
      title: `Archive ${client.name}?`,
      message:
        'Archived clients are hidden from the list and cannot get new jobs. You can restore them at any time.' +
        open,
      confirmLabel: 'Archive',
    });
    if (confirmed) {
      await this.run(
        () => this.api.archive(this.membership()!.organizationId, client.id),
        'Client archived.',
      );
    }
  }

  protected async restore(client: Client): Promise<void> {
    await this.run(
      () => this.api.restore(this.membership()!.organizationId, client.id),
      'Client restored.',
    );
  }

  protected async remove(client: Client): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Delete ${client.name}?`,
      message: 'This cannot be undone. Clients with jobs cannot be deleted; archive them instead.',
      confirmLabel: 'Delete client',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await this.api.delete(this.membership()!.organizationId, client.id);
      await this.router.navigate(['/o', this.slug(), 'clients']);
    } catch (error) {
      // 409 explains why (for example, the client has jobs): show the server's reason.
      this.actionError.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
    }
  }

  private async run(action: () => Promise<Client>, done: string): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    this.limit.set(null);
    this.actionDone.set(null);
    try {
      this.client.set(await action());
      this.actionDone.set(done);
    } catch (error) {
      const problem = toApiProblem(error);
      // Restoring counts toward the active-client limit (SPEC 5.5).
      if (problem.code === 'plan-limit-reached') {
        this.limit.set(problem);
        return;
      }
      this.actionError.set(problem.title);
    } finally {
      this.busy.set(false);
    }
  }
}
