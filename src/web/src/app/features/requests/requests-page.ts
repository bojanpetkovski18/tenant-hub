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
import { SessionStore } from '../../core/session/session.store';
import { PagedList } from '../../shared/list/paged-list.store';
import { Pager } from '../../shared/list/pager';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { AskReason } from '../jobs/reason-dialog';
import { RequestStatus, RequestsApi, RequestsCount, WorkRequest } from './requests.api';

const statusLabels: Readonly<Record<RequestStatus, string>> = {
  open: 'Open',
  converted: 'Converted',
  declined: 'Declined',
};
const filters = ['open', 'converted', 'declined', 'all'] as const;

/**
 * US-84, staff side: the Requests inbox. Clients' requests from the portal, open first; convert one into a job (the
 * job form, prefilled) or decline it with a reason the client reads. The status filter and page are in the URL (D027).
 */
@Component({
  selector: 'app-requests-page',
  imports: [RouterLink, DatePipe, Pager, EmptyState, ErrorState],
  template: `
    <div class="space-y-5">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight">Requests</h1>
        <p class="mt-1 text-sm text-muted">Work your clients asked for through the portal.</p>
      </div>
      <nav class="flex flex-wrap gap-2" aria-label="Request status">
        @for (f of filters; track f) {
          <a
            [routerLink]="[]"
            [queryParams]="{ status: f === 'open' ? null : f, page: null }"
            class="th-nav-link"
            [attr.aria-current]="filter() === f ? 'page' : null"
            >{{ f === 'all' ? 'All' : statusLabels[f] }}</a
          >
        }
      </nav>
      <p class="sr-only" aria-live="polite">{{ announcement() }}</p>
      @if (message(); as text) {
        <div class="th-alert-error" role="alert">{{ text }}</div>
      }

      @if (unavailable(); as problem) {
        <div class="th-card">
          <p>{{ problem.title }}</p>
          @if (canChangePlan()) {
            <a
              [routerLink]="['/o', slug(), 'settings']"
              fragment="plan"
              class="mt-2 inline-block font-medium text-accent hover:underline"
              >See the plans</a
            >
          }
        </div>
      } @else if (error(); as problem) {
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (!requests()) {
        <div class="h-40 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else if (requests()!.items.length === 0) {
        <app-empty-state
          [title]="filter() === 'open' ? 'No open requests' : 'No requests here'"
          message="When a client asks for work in the portal, it arrives here."
        />
      } @else {
        <ul class="space-y-3" aria-label="Requests">
          @for (request of requests()!.items; track request.id) {
            <li class="th-card">
              <div class="flex flex-wrap items-start justify-between gap-3">
                <div class="min-w-0">
                  <p class="font-medium">{{ request.title }}</p>
                  <p class="text-sm text-muted">
                    <a
                      [routerLink]="['/o', slug(), 'clients', request.clientId]"
                      class="hover:underline"
                      >{{ request.clientName }}</a
                    >
                    · {{ request.contactName }} · {{ request.createdAt | date: 'd MMM y' }}
                    @if (request.preferredOn) {
                      · preferred {{ request.preferredOn | date: 'EEE d MMM' }}
                    }
                  </p>
                </div>
                <span class="th-badge">
                  {{ statusLabels[request.status] }}
                  @if (request.jobLabel) {
                    · {{ request.jobLabel }}
                  }
                </span>
              </div>
              @if (request.description) {
                <p class="mt-2 whitespace-pre-line text-sm">{{ request.description }}</p>
              }
              @if (request.declineReason) {
                <p class="mt-2 text-sm">
                  <span class="text-muted">Declined:</span> {{ request.declineReason }}
                </p>
              }
              @if (request.status === 'converted' && request.jobId) {
                <a
                  [routerLink]="['/o', slug(), 'jobs', request.jobId]"
                  class="mt-2 inline-block text-sm font-medium text-accent hover:underline"
                  >Open {{ request.jobLabel }}</a
                >
              }
              @if (request.status === 'open') {
                <div class="mt-3 flex flex-wrap gap-2">
                  @if (canConvert()) {
                    <a
                      [routerLink]="['/o', slug(), 'jobs', 'new']"
                      [queryParams]="convertParams(request)"
                      class="th-button"
                      >Convert to job<span class="sr-only">: {{ request.title }}</span></a
                    >
                  }
                  <button
                    type="button"
                    class="th-button-secondary"
                    [disabled]="busy()"
                    (click)="decline(request)"
                  >
                    Decline<span class="sr-only">: {{ request.title }}</span>
                  </button>
                </div>
              }
            </li>
          }
        </ul>
        <app-pager
          [page]="requests()!.page"
          [pageSize]="requests()!.pageSize"
          [total]="requests()!.total"
          (pageChange)="goTo($event)"
        />
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RequestsPage {
  readonly slug = input.required<string>();
  readonly status = input<string>();
  readonly page = input(1, { transform: (value: unknown) => numberAttribute(value, 1) });

  private readonly api = inject(RequestsApi);
  private readonly count = inject(RequestsCount);
  private readonly session = inject(SessionStore);
  private readonly askReason = inject(AskReason);
  private readonly router = inject(Router);
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));

  protected readonly filters = filters;
  protected readonly statusLabels = statusLabels;
  protected readonly filter = computed(() => filters.find((f) => f === this.status()) ?? 'open');
  protected readonly canConvert = computed(() => this.session.can(this.slug(), 'jobs.create'));
  protected readonly requests = signal<PagedList<WorkRequest> | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);
  /** The plan has no portal (403 `plan-feature-unavailable`): say which plan has it, not "try again". */
  protected readonly unavailable = signal<ApiProblem | null>(null);
  protected readonly canChangePlan = computed(() =>
    this.session.can(this.slug(), 'billing.manage'),
  );
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly announcement = signal('');

  constructor() {
    effect(() => {
      this.filter();
      this.page();
      if (this.membership()) {
        void this.load();
      }
    });
  }

  async load(): Promise<void> {
    const organizationId = this.membership()?.organizationId;
    if (!organizationId) {
      return;
    }
    this.error.set(null);
    this.unavailable.set(null);
    this.requests.set(null);
    const params: Record<string, string> = { page: String(this.page()), pageSize: '20' };
    if (this.filter() !== 'all') {
      params['status'] = this.filter();
    }
    try {
      this.requests.set(await this.api.list(organizationId, params));
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'plan-feature-unavailable') {
        this.unavailable.set(problem);
      } else {
        this.error.set(problem);
      }
    }
  }

  /** The job form, prefilled from the request; saving it converts the request (SPEC-V2 5.5). */
  protected convertParams(request: WorkRequest): Record<string, string> {
    return {
      fromRequest: request.id,
      clientId: request.clientId,
      title: request.title,
      ...(request.description ? { description: request.description } : {}),
      ...(request.preferredOn ? { scheduledOn: request.preferredOn } : {}),
    };
  }

  protected async decline(request: WorkRequest): Promise<void> {
    const reason = await this.askReason.ask({
      title: `Decline “${request.title}”?`,
      message: `${request.contactName} at ${request.clientName} sees this reason in the portal.`,
      confirmLabel: 'Decline request',
      danger: true,
      requiredMessage: 'Say why; the client reads it.',
    });
    if (!reason) {
      return;
    }
    const organizationId = this.membership()!.organizationId;
    this.busy.set(true);
    this.message.set(null);
    try {
      await this.api.decline(organizationId, request.id, reason);
      this.announcement.set(`Declined “${request.title}”.`);
      void this.count.refresh(organizationId);
    } catch (error) {
      this.message.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
      await this.load();
    }
  }

  protected goTo(page: number): void {
    void this.router.navigate([], { queryParams: { page }, queryParamsHandling: 'merge' });
  }
}
