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
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { AgreementStatusBadge } from './agreement-status-badge';
import { AgreementListItem, AgreementStatus, agreementsUrl } from './agreements.api';

const AgreementsListStore = createPagedListStore<AgreementListItem>();
const statuses: readonly (AgreementStatus | 'all')[] = ['all', 'active', 'paused', 'ended'];
const pageSize = 25;

/**
 * US-70: the agreements list. Search, status and page live in the URL (query parameters bound as inputs, D027);
 * the store only fetches. Active agreements come first, then by title.
 */
@Component({
  selector: 'app-agreements-list-page',
  imports: [RouterLink, DatePipe, Pager, EmptyState, ErrorState, AgreementStatusBadge],
  providers: [AgreementsListStore],
  template: `
    <div class="space-y-5">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 class="text-2xl font-semibold tracking-tight">Agreements</h1>
          <p class="mt-1 text-sm text-muted">
            Repeating work for a client. Visits are added as jobs up to 60 days ahead.
          </p>
        </div>
        @if (canManage()) {
          <a routerLink="new" class="th-button">New agreement</a>
        }
      </div>

      <div class="flex flex-wrap items-end gap-3" role="search">
        <div class="min-w-0 flex-1 basis-56">
          <label class="th-label" for="agreement-search">Search</label>
          <input
            #searchInput
            id="agreement-search"
            class="th-input"
            type="search"
            placeholder="Title or client"
            [value]="searchText()"
            (input)="onSearch(searchInput.value)"
          />
        </div>
        <div>
          <label class="th-label" for="agreement-status">Show</label>
          <select
            #statusSelect
            id="agreement-status"
            class="th-input"
            [value]="query().status"
            (change)="navigate({ status: statusSelect.value, page: null })"
          >
            <option value="all">All agreements</option>
            <option value="active">Active</option>
            <option value="paused">Paused</option>
            <option value="ended">Ended</option>
          </select>
        </div>
      </div>

      @if (store.error(); as error) {
        <app-error-state [message]="error.title" (retry)="store.retry()" />
      } @else if (store.isEmpty()) {
        @if (query().q || query().status !== 'all') {
          <app-empty-state title="No agreements match" message="Try another search or show all.">
            <button
              type="button"
              class="th-button-secondary"
              (click)="navigate({ q: null, status: null, page: null })"
            >
              Clear filters
            </button>
          </app-empty-state>
        } @else {
          <app-empty-state
            title="No agreements yet"
            message="Set up repeating work once, such as weekly office cleaning, and its visits appear by themselves."
          >
            @if (canManage()) {
              <a routerLink="new" class="th-button">Set up an agreement</a>
            }
          </app-empty-state>
        }
      } @else {
        <div class="th-card overflow-hidden p-0 sm:p-0" [attr.aria-busy]="store.loading()">
          <table class="hidden w-full text-sm md:table">
            <caption class="sr-only">
              Agreements,
              {{
                store.total()
              }}
              in total
            </caption>
            <thead class="border-b border-border bg-surface-2">
              <tr>
                @for (heading of headings; track heading) {
                  <th
                    scope="col"
                    class="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted"
                  >
                    {{ heading }}
                  </th>
                }
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
              @for (agreement of store.items(); track agreement.id) {
                <tr class="border-b border-border last:border-0 hover:bg-surface-2">
                  <td class="px-3 py-2.5 font-medium">
                    <a [routerLink]="agreement.id" class="hover:underline">{{ agreement.title }}</a>
                  </td>
                  <td class="px-3 py-2.5 text-muted">{{ agreement.clientName }}</td>
                  <td class="px-3 py-2.5 text-muted">{{ agreement.schedule }}</td>
                  <td class="px-3 py-2.5 text-muted">
                    {{ agreement.nextVisitOn ? (agreement.nextVisitOn | date: 'EEE d MMM') : '—' }}
                  </td>
                  <td class="px-3 py-2.5 text-muted">
                    {{ agreement.assigneeName ?? 'Unassigned' }}
                  </td>
                  <td class="px-3 py-2.5">
                    <app-agreement-status-badge
                      [status]="agreement.status"
                      [hold]="agreement.pausedReason"
                    />
                  </td>
                </tr>
              }
            </tbody>
          </table>

          <ul class="divide-y divide-border md:hidden" aria-label="Agreements">
            @for (agreement of store.items(); track agreement.id) {
              <li class="p-4">
                <div class="flex items-start justify-between gap-3">
                  <a [routerLink]="agreement.id" class="font-medium hover:underline">{{
                    agreement.title
                  }}</a>
                  <app-agreement-status-badge
                    [status]="agreement.status"
                    [hold]="agreement.pausedReason"
                  />
                </div>
                <p class="mt-1 text-sm text-muted">{{ agreement.clientName }}</p>
                <p class="text-sm text-muted">{{ agreement.schedule }}</p>
                @if (agreement.nextVisitOn) {
                  <p class="text-sm text-muted">
                    Next visit {{ agreement.nextVisitOn | date: 'EEE d MMM' }}
                  </p>
                }
              </li>
            }
          </ul>
        </div>

        <app-pager
          [page]="query().page"
          [pageSize]="pageSize"
          [total]="store.total()"
          (pageChange)="navigate({ page: $event === 1 ? null : $event })"
        />
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgreementsListPage {
  /** Route parameter (inherited from /o/:slug). */
  readonly slug = input.required<string>();
  /** Query parameters. */
  readonly q = input<string>();
  readonly status = input<string>();
  readonly page = input<string>();

  protected readonly store = inject(AgreementsListStore);
  protected readonly pageSize = pageSize;
  protected readonly skeletonRows = [1, 2, 3, 4];
  protected readonly headings = [
    'Agreement',
    'Client',
    'Schedule',
    'Next visit',
    'Assignee',
    'Status',
  ];
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canManage = computed(() => this.session.can(this.slug(), 'agreements.manage'));

  /** The URL's query, with unknown values replaced by the defaults (the server ignores unknown statuses too). */
  protected readonly query = computed(() => {
    const status = statuses.find((s) => s === this.status()) ?? 'all';
    const page = Math.max(1, Number.parseInt(this.page() ?? '1', 10) || 1);
    return { q: this.q()?.trim() ?? '', status, page };
  });

  protected readonly searchText = signal('');
  private searchTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect(() => this.searchText.set(this.query().q));

    effect(() => {
      const membership = this.membership();
      const query = this.query();
      if (!membership) {
        return;
      }
      void this.store.load(agreementsUrl(membership.organizationId), {
        ...(query.q ? { q: query.q } : {}),
        ...(query.status !== 'all' ? { status: query.status } : {}),
        page: String(query.page),
        pageSize: String(pageSize),
      });
    });

    inject(DestroyRef).onDestroy(() => clearTimeout(this.searchTimer));
  }

  protected onSearch(value: string): void {
    this.searchText.set(value);
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(
      () => this.navigate({ q: value.trim() || null, page: null }),
      300,
    );
  }

  /** Changes the query in the URL; default values are removed so the URL stays short. */
  protected navigate(changes: Record<string, string | number | null>): void {
    const cleaned = Object.fromEntries(
      Object.entries(changes).map(([key, value]) => [
        key,
        key === 'status' && value === 'all' ? null : value,
      ]),
    );
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: cleaned,
      queryParamsHandling: 'merge',
    });
  }
}
