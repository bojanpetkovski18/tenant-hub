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
import { ClientStatusBadge } from './client-status-badge';
import { ClientListItem, ClientStatusFilter, clientsUrl } from './clients.api';

const ClientsListStore = createPagedListStore<ClientListItem>();
const statuses: readonly ClientStatusFilter[] = ['active', 'archived', 'all'];
const sorts = ['name', '-name', 'updated', '-updated', 'created', '-created'];
const pageSize = 25;

/**
 * US-20: the clients list. Search, status, sort and page live in the URL (query parameters bound as inputs), so
 * reload, back/forward and shared links reproduce the view; the store only fetches. Wide screens get a table,
 * narrow screens the same rows as cards.
 */
@Component({
  selector: 'app-clients-list-page',
  imports: [RouterLink, DatePipe, SortHeader, Pager, EmptyState, ErrorState, ClientStatusBadge],
  providers: [ClientsListStore],
  template: `
    <div class="space-y-5">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-2xl font-semibold tracking-tight">Clients</h1>
        @if (canWrite()) {
          <a routerLink="new" class="th-button">New client</a>
        }
      </div>

      <div class="flex flex-wrap items-end gap-3" role="search">
        <div class="min-w-0 flex-1 basis-56">
          <label class="th-label" for="client-search">Search</label>
          <input
            #searchInput
            id="client-search"
            class="th-input"
            type="search"
            placeholder="Name, contact or email"
            [value]="searchText()"
            (input)="onSearch(searchInput.value)"
          />
        </div>
        <div>
          <label class="th-label" for="client-status">Show</label>
          <select
            #statusSelect
            id="client-status"
            class="th-input"
            [value]="query().status"
            (change)="navigate({ status: statusSelect.value, page: null })"
          >
            <option value="active">Active clients</option>
            <option value="archived">Archived clients</option>
            <option value="all">All clients</option>
          </select>
        </div>
      </div>

      @if (store.error(); as error) {
        <app-error-state [message]="error.title" (retry)="store.retry()" />
      } @else if (store.isEmpty()) {
        @if (query().q || query().status !== 'active') {
          <app-empty-state
            title="No clients match"
            message="Try another search or show all clients."
          >
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
            title="No clients yet"
            message="Add the businesses and people you work for."
          >
            @if (canWrite()) {
              <a routerLink="new" class="th-button">Add your first client</a>
            }
          </app-empty-state>
        }
      } @else {
        <div class="th-card overflow-hidden p-0 sm:p-0" [attr.aria-busy]="store.loading()">
          <table class="hidden w-full text-sm md:table">
            <caption class="sr-only">
              Clients,
              {{
                store.total()
              }}
              in total
            </caption>
            <thead class="border-b border-border bg-surface-2">
              <tr>
                <th
                  appSortHeader="name"
                  [sort]="query().sort"
                  (sortChange)="navigate({ sort: $event, page: null })"
                >
                  Name
                </th>
                <th
                  scope="col"
                  class="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted"
                >
                  Contact
                </th>
                <th
                  scope="col"
                  class="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted"
                >
                  Email
                </th>
                <th
                  scope="col"
                  class="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted"
                >
                  Phone
                </th>
                <th
                  scope="col"
                  class="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted"
                >
                  Status
                </th>
                <th
                  appSortHeader="updated"
                  [sort]="query().sort"
                  (sortChange)="navigate({ sort: $event, page: null })"
                >
                  Updated
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
              @for (client of store.items(); track client.id) {
                <tr class="border-b border-border last:border-0 hover:bg-surface-2">
                  <td class="px-3 py-2.5 font-medium">
                    <a [routerLink]="client.id" class="hover:underline">{{ client.name }}</a>
                  </td>
                  <td class="px-3 py-2.5 text-muted">{{ client.contactName ?? '—' }}</td>
                  <td class="px-3 py-2.5 text-muted">{{ client.email ?? '—' }}</td>
                  <td class="px-3 py-2.5 text-muted">{{ client.phone ?? '—' }}</td>
                  <td class="px-3 py-2.5"><app-client-status-badge [status]="client.status" /></td>
                  <td class="px-3 py-2.5 text-muted">
                    {{ client.updatedAt | date: 'mediumDate' }}
                  </td>
                </tr>
              }
            </tbody>
          </table>

          <ul class="divide-y divide-border md:hidden" aria-label="Clients">
            @for (client of store.items(); track client.id) {
              <li class="p-4">
                <div class="flex items-start justify-between gap-3">
                  <a [routerLink]="client.id" class="font-medium hover:underline">{{
                    client.name
                  }}</a>
                  <app-client-status-badge [status]="client.status" />
                </div>
                @if (client.contactName) {
                  <p class="mt-1 text-sm text-muted">{{ client.contactName }}</p>
                }
                @if (client.email) {
                  <p class="text-sm text-muted break-all">{{ client.email }}</p>
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
export class ClientsListPage {
  /** Route parameter (inherited from /o/:slug). */
  readonly slug = input.required<string>();
  /** Query parameters. */
  readonly q = input<string>();
  readonly status = input<string>();
  readonly sort = input<string>();
  readonly page = input<string>();

  protected readonly store = inject(ClientsListStore);
  protected readonly pageSize = pageSize;
  protected readonly skeletonRows = [1, 2, 3, 4, 5];
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canWrite = computed(() => this.session.can(this.slug(), 'clients.write'));

  /** The URL's query, with unknown values replaced by the defaults (the server applies the same rule). */
  protected readonly query = computed(() => {
    const status = statuses.find((s) => s === this.status()) ?? 'active';
    const sort = sorts.includes(this.sort() ?? '') ? this.sort()! : 'name';
    const page = Math.max(1, Number.parseInt(this.page() ?? '1', 10) || 1);
    return { q: this.q()?.trim() ?? '', status, sort, page };
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
      void this.store.load(clientsUrl(membership.organizationId), {
        ...(query.q ? { q: query.q } : {}),
        status: query.status,
        sort: query.sort,
        page: String(query.page),
        pageSize: String(pageSize),
      });
    });

    inject(DestroyRef).onDestroy(() => clearTimeout(this.searchTimer));
  }

  protected onSearch(value: string): void {
    this.searchText.set(value);
    clearTimeout(this.searchTimer);
    // Wait for a pause in typing before changing the URL (one history entry and one request per search).
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
        (key === 'status' && value === 'active') || (key === 'sort' && value === 'name')
          ? null
          : value,
      ]),
    );
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: cleaned,
      queryParamsHandling: 'merge',
    });
  }
}
