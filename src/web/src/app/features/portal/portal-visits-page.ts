import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  numberAttribute,
  signal,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { PagedList } from '../../shared/list/paged-list.store';
import { Pager } from '../../shared/list/pager';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { JobStatusBadge } from '../jobs/job-badges';
import { PortalApi, PortalVisit } from './portal.api';

/**
 * US-82: the client's visits, upcoming (still open) or past (closed in the last 12 months), with date, status and who
 * is coming. Which list and which page are in the URL, as for every list (D027).
 */
@Component({
  selector: 'app-portal-visits-page',
  imports: [RouterLink, DatePipe, Pager, EmptyState, ErrorState, JobStatusBadge],
  template: `
    <h1 class="text-2xl font-semibold tracking-tight">Visits</h1>
    <nav class="mt-4 flex gap-2" aria-label="Which visits">
      <a
        [routerLink]="[]"
        [queryParams]="{ when: null, page: null }"
        class="th-nav-link"
        [attr.aria-current]="past() ? null : 'page'"
        >Upcoming</a
      >
      <a
        [routerLink]="[]"
        [queryParams]="{ when: 'past', page: null }"
        class="th-nav-link"
        [attr.aria-current]="past() ? 'page' : null"
        >Past</a
      >
    </nav>

    <div class="mt-4">
      @if (error(); as problem) {
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (!visits()) {
        <!-- As tall as the screen: the footer starts below the fold, so a full list does not push it (K6, CLS). -->
        <div class="h-dvh animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else if (visits()!.items.length === 0) {
        <app-empty-state
          [title]="past() ? 'No past visits' : 'No upcoming visits'"
          [message]="
            past()
              ? 'Visits from the last 12 months show here once they are done.'
              : 'When a visit is planned for you, it shows here.'
          "
        />
      } @else {
        <ul class="space-y-2" [attr.aria-label]="past() ? 'Past visits' : 'Upcoming visits'">
          @for (visit of visits()!.items; track visit.id) {
            <li class="th-card flex flex-wrap items-center justify-between gap-3 py-3">
              <div class="min-w-0">
                <a
                  [routerLink]="['/portal', slug(), 'visits', visit.id]"
                  class="font-medium hover:underline"
                >
                  {{ visit.title }}
                </a>
                <p class="text-sm text-muted">
                  <span class="font-mono text-xs">{{ visit.label }}</span> ·
                  {{
                    visit.scheduledOn
                      ? (visit.scheduledOn | date: 'EEE d MMM y')
                      : 'Date to be agreed'
                  }}
                  @if (visit.assigneeName) {
                    · {{ visit.assigneeName }}
                  }
                </p>
              </div>
              <app-job-status-badge [status]="visit.status" />
            </li>
          }
        </ul>
        <div class="mt-4">
          <app-pager
            [page]="visits()!.page"
            [pageSize]="visits()!.pageSize"
            [total]="visits()!.total"
            (pageChange)="goTo($event)"
          />
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalVisitsPage {
  readonly slug = input.required<string>();
  readonly when = input<string>();
  readonly page = input(1, { transform: (value: unknown) => numberAttribute(value, 1) });

  private readonly api = inject(PortalApi);
  private readonly router = inject(Router);

  protected readonly past = computed(() => this.when() === 'past');
  protected readonly visits = signal<PagedList<PortalVisit> | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);

  constructor() {
    effect(() => {
      this.past();
      this.page();
      void this.load();
    });
  }

  async load(): Promise<void> {
    this.error.set(null);
    this.visits.set(null);
    try {
      this.visits.set(await this.api.visits(this.past() ? 'past' : 'upcoming', this.page()));
    } catch (error) {
      this.error.set(toApiProblem(error));
    }
  }

  protected goTo(page: number): void {
    void this.router.navigate([], { queryParams: { page }, queryParamsHandling: 'merge' });
  }
}
