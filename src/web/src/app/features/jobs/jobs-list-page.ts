import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { createPagedListStore } from '../../shared/list/paged-list.store';
import { Pager } from '../../shared/list/pager';
import { SortHeader } from '../../shared/list/sort-header';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { ClientsApi, ClientOption } from '../clients/clients.api';
import { Member, MembersApi } from '../members/members.api';
import { JobPriorityLabel, JobStatusBadge, OverdueMarker } from './job-badges';
import {
  JobQuery,
  activeFilterCount,
  jobApiParams,
  jobUrlChanges,
  parseJobQuery,
} from './job-query';
import {
  JobListItem,
  JobPriority,
  JobStatus,
  jobPriorities,
  jobStatuses,
  jobsUrl,
  priorityLabels,
  statusLabels,
} from './jobs.api';

const JobsListStore = createPagedListStore<JobListItem>();
const pageSize = 25;

/**
 * US-31 and US-33: the jobs list. Every filter, the search, the sort and the page live in the URL (query parameters
 * bound as inputs, parsed by {@link parseJobQuery}), so reload, back and shared links reproduce the view; the store
 * only fetches. "My jobs" is one click. Wide screens get a table and the filters beside it; narrow screens get
 * cards and a "Filters" button.
 */
@Component({
  selector: 'app-jobs-list-page',
  imports: [
    RouterLink,
    DatePipe,
    SortHeader,
    Pager,
    EmptyState,
    ErrorState,
    JobStatusBadge,
    JobPriorityLabel,
    OverdueMarker,
  ],
  providers: [JobsListStore],
  template: `
    <div class="space-y-5">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-2xl font-semibold tracking-tight">Jobs</h1>
        <div class="flex flex-wrap gap-2">
          <button
            type="button"
            class="th-button-secondary"
            [class.border-accent]="query().assignee === 'me'"
            [attr.aria-pressed]="query().assignee === 'me'"
            (click)="change({ assignee: query().assignee === 'me' ? '' : 'me', page: 1 })"
          >
            My jobs
          </button>
          @if (canCreate()) {
            <a routerLink="new" class="th-button">New job</a>
          }
        </div>
      </div>

      <div class="flex flex-wrap items-end gap-3" role="search">
        <div class="min-w-0 flex-1 basis-56">
          <label class="th-label" for="job-search">Search</label>
          <input
            #searchInput
            id="job-search"
            class="th-input"
            type="search"
            placeholder="Number or title, e.g. JOB-0042"
            [value]="searchText()"
            (input)="onSearch(searchInput.value)"
          />
        </div>
        <div class="md:hidden">
          <label class="th-label" for="job-sort">Sort by</label>
          <select
            #sortSelect
            id="job-sort"
            class="th-input"
            [value]="query().sort"
            (change)="change({ sort: sortSelect.value, page: 1 })"
          >
            <option value="-number">Newest first</option>
            <option value="number">Oldest first</option>
            <option value="due">Due date, soonest</option>
            <option value="-due">Due date, latest</option>
            <option value="-priority">Priority, highest</option>
            <option value="-updated">Recently updated</option>
            <option value="title">Title, A–Z</option>
          </select>
        </div>
        <button
          type="button"
          class="th-button-secondary lg:hidden"
          aria-controls="job-filters"
          [attr.aria-expanded]="filtersOpen()"
          (click)="filtersOpen.set(!filtersOpen())"
        >
          Filters
          @if (filterCount() > 0) {
            <span class="th-badge bg-accent-soft text-fg">{{ filterCount() }}</span>
          }
        </button>
      </div>

      <div class="flex flex-col gap-5 lg:flex-row lg:items-start">
        <section
          id="job-filters"
          aria-label="Filters"
          class="th-card space-y-5 lg:block lg:w-64 lg:shrink-0"
          [class.hidden]="!filtersOpen()"
        >
          <fieldset>
            <legend class="th-label">Status</legend>
            <div class="grid grid-cols-2 gap-x-3 gap-y-1 lg:grid-cols-1">
              @for (status of statuses; track status) {
                <label class="flex min-h-8 items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    class="size-4 accent-accent"
                    [checked]="query().statuses.includes(status)"
                    (change)="toggleStatus(status)"
                  />
                  {{ statusLabels[status] }}
                </label>
              }
            </div>
          </fieldset>

          <fieldset>
            <legend class="th-label">Priority</legend>
            <div class="grid grid-cols-2 gap-x-3 gap-y-1 lg:grid-cols-1">
              @for (priority of priorities; track priority) {
                <label class="flex min-h-8 items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    class="size-4 accent-accent"
                    [checked]="query().priorities.includes(priority)"
                    (change)="togglePriority(priority)"
                  />
                  {{ priorityLabels[priority] }}
                </label>
              }
            </div>
          </fieldset>

          <div>
            <label class="th-label" for="job-assignee">Assignee</label>
            <select
              #assigneeSelect
              id="job-assignee"
              class="th-input"
              [value]="query().assignee"
              (change)="change({ assignee: assigneeSelect.value, page: 1 })"
            >
              <option value="">Anyone</option>
              <option value="me">Me</option>
              <option value="none">Unassigned</option>
              @for (member of members(); track member.userId) {
                @if (!member.isYou) {
                  <option [value]="member.userId">{{ member.displayName }}</option>
                }
              }
            </select>
          </div>

          <div>
            <label class="th-label" for="job-client">Client</label>
            <select
              #clientSelect
              id="job-client"
              class="th-input"
              [value]="query().clientId"
              (change)="change({ clientId: clientSelect.value, page: 1 })"
            >
              <option value="">All clients</option>
              @for (client of clients(); track client.id) {
                <option [value]="client.id">{{ client.name }}</option>
              }
            </select>
          </div>

          <label class="flex min-h-8 items-center gap-2 text-sm">
            <input
              type="checkbox"
              class="size-4 accent-accent"
              [checked]="query().overdue"
              (change)="change({ overdue: !query().overdue, page: 1 })"
            />
            Overdue only
          </label>

          <fieldset>
            <legend class="th-label">Due date</legend>
            <div class="grid grid-cols-2 gap-2 lg:grid-cols-1">
              <div>
                <label class="mb-1 block text-xs text-muted" for="job-due-from">From</label>
                <input
                  #dueFrom
                  id="job-due-from"
                  type="date"
                  class="th-input"
                  [value]="query().dueFrom"
                  (change)="change({ dueFrom: dueFrom.value, page: 1 })"
                />
              </div>
              <div>
                <label class="mb-1 block text-xs text-muted" for="job-due-to">To</label>
                <input
                  #dueTo
                  id="job-due-to"
                  type="date"
                  class="th-input"
                  [value]="query().dueTo"
                  (change)="change({ dueTo: dueTo.value, page: 1 })"
                />
              </div>
            </div>
          </fieldset>

          @if (filterCount() > 0 || query().q) {
            <button type="button" class="th-button-secondary w-full" (click)="clear()">
              Clear filters
            </button>
          }
        </section>

        <div class="min-w-0 flex-1 space-y-4">
          @if (store.error(); as error) {
            <app-error-state [message]="error.title" (retry)="store.retry()" />
          } @else if (store.isEmpty()) {
            @if (filterCount() > 0 || query().q) {
              <app-empty-state title="No jobs match" message="Try fewer filters or another search.">
                <button type="button" class="th-button-secondary" (click)="clear()">
                  Clear filters
                </button>
              </app-empty-state>
            } @else {
              <app-empty-state title="No jobs yet" message="Plan the work you do for your clients.">
                @if (canCreate()) {
                  <a routerLink="new" class="th-button">Create the first job</a>
                }
              </app-empty-state>
            }
          } @else {
            <div class="th-card overflow-hidden p-0 sm:p-0" [attr.aria-busy]="store.loading()">
              <table class="hidden w-full text-sm md:table">
                <caption class="sr-only">
                  Jobs,
                  {{
                    store.total()
                  }}
                  in total
                </caption>
                <thead class="border-b border-border bg-surface-2">
                  <tr>
                    <th appSortHeader="number" [sort]="query().sort" (sortChange)="sortBy($event)">
                      Job
                    </th>
                    <th appSortHeader="title" [sort]="query().sort" (sortChange)="sortBy($event)">
                      Title
                    </th>
                    <th
                      scope="col"
                      class="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted"
                    >
                      Assignee
                    </th>
                    <th
                      scope="col"
                      class="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted"
                    >
                      Status
                    </th>
                    <th
                      appSortHeader="priority"
                      [sort]="query().sort"
                      (sortChange)="sortBy($event)"
                    >
                      Priority
                    </th>
                    <th appSortHeader="due" [sort]="query().sort" (sortChange)="sortBy($event)">
                      Due
                    </th>
                  </tr>
                </thead>
                <tbody>
                  @if (store.loading() && store.items().length === 0) {
                    @for (row of skeletonRows; track row) {
                      <tr class="border-b border-border last:border-0" aria-hidden="true">
                        <td class="px-3 py-3" colspan="6">
                          <div class="h-4 w-2/3 animate-pulse rounded bg-surface-2"></div>
                        </td>
                      </tr>
                    }
                  }
                  @for (job of store.items(); track job.id) {
                    <tr class="border-b border-border last:border-0 hover:bg-surface-2">
                      <td class="whitespace-nowrap px-3 py-2.5 font-mono text-xs text-muted">
                        {{ job.label }}
                      </td>
                      <td class="px-3 py-2.5">
                        <a [routerLink]="job.id" class="font-medium hover:underline">{{
                          job.title
                        }}</a>
                        <span class="block text-xs text-muted">{{ job.clientName }}</span>
                      </td>
                      <td class="px-3 py-2.5 text-muted">{{ job.assigneeName ?? 'Unassigned' }}</td>
                      <td class="px-3 py-2.5"><app-job-status-badge [status]="job.status" /></td>
                      <td class="px-3 py-2.5"><app-job-priority [priority]="job.priority" /></td>
                      <td class="whitespace-nowrap px-3 py-2.5">
                        <span class="text-muted">{{
                          job.dueOn ? (job.dueOn | date: 'mediumDate') : '—'
                        }}</span>
                        @if (job.isOverdue) {
                          <span class="block"><app-overdue-marker /></span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>

              <ul class="divide-y divide-border md:hidden" aria-label="Jobs">
                @for (job of store.items(); track job.id) {
                  <li class="space-y-1.5 p-4">
                    <div class="flex items-start justify-between gap-3">
                      <a [routerLink]="job.id" class="min-w-0 font-medium hover:underline">
                        <span class="font-mono text-xs text-muted">{{ job.label }}</span>
                        <span class="block">{{ job.title }}</span>
                      </a>
                      <app-job-status-badge [status]="job.status" />
                    </div>
                    <p class="text-sm text-muted">
                      {{ job.clientName }} · {{ job.assigneeName ?? 'Unassigned' }}
                    </p>
                    <div class="flex flex-wrap items-center gap-3 text-sm">
                      <app-job-priority [priority]="job.priority" />
                      @if (job.dueOn) {
                        <span class="text-muted">Due {{ job.dueOn | date: 'mediumDate' }}</span>
                      }
                      @if (job.isOverdue) {
                        <app-overdue-marker />
                      }
                    </div>
                  </li>
                }
              </ul>
            </div>

            <app-pager
              [page]="query().page"
              [pageSize]="pageSize"
              [total]="store.total()"
              (pageChange)="change({ page: $event })"
            />
          }
        </div>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobsListPage {
  /** Route parameter (inherited from /o/:slug). */
  readonly slug = input.required<string>();
  /** Query parameters. */
  readonly q = input<string>();
  readonly status = input<string>();
  readonly priority = input<string>();
  readonly assignee = input<string>();
  readonly clientId = input<string>();
  readonly overdue = input<string>();
  readonly dueFrom = input<string>();
  readonly dueTo = input<string>();
  readonly sort = input<string>();
  readonly page = input<string>();

  protected readonly store = inject(JobsListStore);
  protected readonly pageSize = pageSize;
  protected readonly skeletonRows = [1, 2, 3, 4, 5];
  protected readonly statuses = jobStatuses;
  protected readonly priorities = jobPriorities;
  protected readonly statusLabels = statusLabels;
  protected readonly priorityLabels = priorityLabels;
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly membersApi = inject(MembersApi);
  private readonly clientsApi = inject(ClientsApi);

  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canCreate = computed(() => this.session.can(this.slug(), 'jobs.create'));

  protected readonly query = computed(() =>
    parseJobQuery({
      q: this.q(),
      status: this.status(),
      priority: this.priority(),
      assignee: this.assignee(),
      clientId: this.clientId(),
      overdue: this.overdue(),
      dueFrom: this.dueFrom(),
      dueTo: this.dueTo(),
      sort: this.sort(),
      page: this.page(),
    }),
  );
  protected readonly filterCount = computed(() => activeFilterCount(this.query()));

  protected readonly searchText = signal('');
  protected readonly filtersOpen = signal(false);
  protected readonly members = signal<Member[]>([]);
  protected readonly clients = signal<ClientOption[]>([]);
  private searchTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect(() => this.searchText.set(this.query().q));

    effect(() => {
      const membership = this.membership();
      const query = this.query();
      if (membership) {
        void this.store.load(jobsUrl(membership.organizationId), jobApiParams(query, pageSize));
      }
    });

    // The pickers' options; the list works without them (a failure leaves "Anyone" and "All clients").
    effect(() => {
      const membership = this.membership();
      if (membership) {
        void this.membersApi.members(membership.organizationId).then(
          (members) => this.members.set(members),
          () => this.members.set([]),
        );
        void this.clientsApi.options(membership.organizationId).then(
          (clients) => this.clients.set(clients),
          () => this.clients.set([]),
        );
      }
    });

    inject(DestroyRef).onDestroy(() => clearTimeout(this.searchTimer));
  }

  protected onSearch(value: string): void {
    this.searchText.set(value);
    clearTimeout(this.searchTimer);
    // Wait for a pause in typing before changing the URL (one history entry and one request per search).
    this.searchTimer = setTimeout(() => this.change({ q: value.trim(), page: 1 }), 300);
  }

  protected toggleStatus(status: JobStatus): void {
    const current = this.query().statuses;
    const next = current.includes(status)
      ? current.filter((s) => s !== status)
      : [...current, status];
    this.change({ statuses: jobStatuses.filter((s) => next.includes(s)), page: 1 });
  }

  protected togglePriority(priority: JobPriority): void {
    const current = this.query().priorities;
    const next = current.includes(priority)
      ? current.filter((p) => p !== priority)
      : [...current, priority];
    this.change({ priorities: jobPriorities.filter((p) => next.includes(p)), page: 1 });
  }

  protected sortBy(sort: string): void {
    this.change({ sort, page: 1 });
  }

  protected clear(): void {
    this.change({
      q: '',
      statuses: [],
      priorities: [],
      assignee: '',
      clientId: '',
      overdue: false,
      dueFrom: '',
      dueTo: '',
      page: 1,
    });
  }

  /** Changes the query in the URL; defaults leave it, so the URL stays short. */
  protected change(changes: Partial<JobQuery>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: jobUrlChanges(changes),
      queryParamsHandling: 'merge',
    });
  }
}
