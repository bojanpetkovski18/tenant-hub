import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { ErrorState } from '../../shared/ui/states';
import { JobStatusBadge } from '../jobs/job-badges';
import { PortalApi, PortalVisitDetail } from './portal.api';

/** US-82: one visit, as the client sees it. Another client's visit is "not found", like one that does not exist. */
@Component({
  selector: 'app-portal-visit-page',
  imports: [RouterLink, DatePipe, ErrorState, JobStatusBadge],
  template: `
    <a
      [routerLink]="['/portal', slug(), 'visits']"
      class="text-sm font-medium text-accent hover:underline"
      >← All visits</a
    >
    <div class="mt-4">
      @if (notFound()) {
        <div class="th-card">
          <h1 class="text-xl font-semibold">Visit not found</h1>
          <p class="mt-2 text-sm text-muted">
            It may have been removed, or the link is not for your company.
          </p>
        </div>
      } @else if (error(); as problem) {
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (visit(); as v) {
        <article class="th-card">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div>
              <p class="font-mono text-xs text-muted">{{ v.label }}</p>
              <h1 class="text-xl font-semibold">{{ v.title }}</h1>
            </div>
            <app-job-status-badge [status]="v.status" />
          </div>
          <dl class="mt-4 grid gap-3 text-sm sm:grid-cols-2">
            <div>
              <dt class="text-muted">Date</dt>
              <dd>
                {{ v.scheduledOn ? (v.scheduledOn | date: 'EEEE d MMMM y') : 'To be agreed' }}
              </dd>
            </div>
            <div>
              <dt class="text-muted">Who is coming</dt>
              <dd>{{ v.assigneeName ?? 'Not assigned yet' }}</dd>
            </div>
            @if (v.dueOn) {
              <div>
                <dt class="text-muted">Done by</dt>
                <dd>{{ v.dueOn | date: 'EEEE d MMMM y' }}</dd>
              </div>
            }
            @if (v.completedAt) {
              <div>
                <dt class="text-muted">Completed</dt>
                <dd>{{ v.completedAt | date: 'd MMMM y, HH:mm' }}</dd>
              </div>
            }
          </dl>
          @if (v.description) {
            <p class="mt-4 whitespace-pre-line text-sm">{{ v.description }}</p>
          }
        </article>
      } @else {
        <div class="h-40 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalVisitPage {
  readonly slug = input.required<string>();
  readonly id = input.required<string>();

  private readonly api = inject(PortalApi);

  protected readonly visit = signal<PortalVisitDetail | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);
  protected readonly notFound = signal(false);

  constructor() {
    effect(() => {
      this.id();
      void this.load();
    });
  }

  async load(): Promise<void> {
    this.error.set(null);
    this.notFound.set(false);
    this.visit.set(null);
    try {
      this.visit.set(await this.api.visit(this.id()));
    } catch (error) {
      const problem = toApiProblem(error);
      this.notFound.set(problem.code === 'visit-not-found');
      this.error.set(problem.code === 'visit-not-found' ? null : problem);
    }
  }
}
