import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { PortalApi, PortalRequest, RequestStatus } from './portal.api';

const statusText: Readonly<Record<RequestStatus, string>> = {
  open: 'Waiting for an answer',
  converted: 'Planned',
  declined: 'Declined',
};

/** US-84: the company's work requests and what became of them (a job, or a reason for declining). */
@Component({
  selector: 'app-portal-requests-page',
  imports: [RouterLink, DatePipe, EmptyState, ErrorState],
  template: `
    <div class="flex flex-wrap items-center justify-between gap-3">
      <h1 class="text-2xl font-semibold tracking-tight">Requests</h1>
      <a [routerLink]="['/portal', slug(), 'requests', 'new']" class="th-button">Request work</a>
    </div>
    @if (notice() === 'sent') {
      <div class="th-alert-success mt-4" role="status">
        Your request was sent. The business will answer here.
      </div>
    }

    <div class="mt-4">
      @if (error(); as problem) {
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (!requests()) {
        <div class="h-40 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else if (requests()!.length === 0) {
        <app-empty-state
          title="No requests yet"
          message="Ask for work here: describe it and, if you like, a preferred day."
        >
          <a [routerLink]="['/portal', slug(), 'requests', 'new']" class="th-button"
            >Request work</a
          >
        </app-empty-state>
      } @else {
        <ul class="space-y-2" aria-label="Your requests">
          @for (request of requests(); track request.id) {
            <li class="th-card py-3">
              <div class="flex flex-wrap items-start justify-between gap-2">
                <div class="min-w-0">
                  <p class="font-medium">{{ request.title }}</p>
                  <p class="text-sm text-muted">
                    {{ request.requestedBy }} on {{ request.createdAt | date: 'd MMM y' }}
                    @if (request.preferredOn) {
                      · preferred {{ request.preferredOn | date: 'EEE d MMM' }}
                    }
                  </p>
                </div>
                <span
                  [class]="
                    'th-badge ' + (request.status === 'converted' ? 'bg-success/15 text-fg' : '')
                  "
                >
                  {{ statusText[request.status] }}
                  @if (request.jobLabel) {
                    · {{ request.jobLabel }}
                  }
                </span>
              </div>
              @if (request.declineReason) {
                <p class="mt-2 text-sm">
                  <span class="text-muted">Why:</span> {{ request.declineReason }}
                </p>
              }
            </li>
          }
        </ul>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalRequestsPage {
  readonly slug = input.required<string>();
  readonly notice = input<string>();

  private readonly api = inject(PortalApi);

  protected readonly statusText = statusText;
  protected readonly requests = signal<PortalRequest[] | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.error.set(null);
    try {
      this.requests.set(await this.api.requests());
    } catch (error) {
      this.error.set(toApiProblem(error));
    }
  }
}
