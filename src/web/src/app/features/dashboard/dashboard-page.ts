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
import { Permission } from '../../core/session/session.models';
import { SessionStore } from '../../core/session/session.store';
import { planLabels } from '../../shared/plan/plan';
import { ErrorState } from '../../shared/ui/states';
import { UsageMeters } from '../../shared/ui/usage-meters';
import { relativeTime } from '../audit/audit-page';
import { OverdueMarker } from '../jobs/job-badges';
import { Dashboard, DashboardApi } from './dashboard.api';
import { StatusChart } from './status-chart';

/** A calendar date from the API ("2026-10-24") in words, without the browser's zone shifting the day. */
export function formatDay(iso: string, options: Intl.DateTimeFormatOptions): string {
  return new Intl.DateTimeFormat('en-GB', { ...options, timeZone: 'UTC' }).format(
    new Date(`${iso}T00:00:00Z`),
  );
}

interface Tile {
  readonly label: string;
  readonly value: number;
  readonly path: string;
  readonly query: Readonly<Record<string, string>>;
  readonly tone?: 'danger';
}

/**
 * US-60: the organization at a glance. Each tile links to the list that counts the same records (the API sends the
 * organization's today and month start for the links), the chart has a table alternative, and recent activity
 * appears for `audit.read` only. One request; everything is counted in SQL.
 */
@Component({
  selector: 'app-dashboard-page',
  imports: [RouterLink, ErrorState, UsageMeters, StatusChart, OverdueMarker],
  template: `
    <div class="space-y-6">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight">Dashboard</h1>
        @if (data(); as d) {
          <p class="mt-1 text-sm text-muted">
            {{ day(d.today, { weekday: 'long', day: 'numeric', month: 'long' }) }} in
            {{ d.timeZone }}
          </p>
        }
      </div>

      @if (loadError(); as error) {
        <app-error-state [message]="error.title" (retry)="load()" />
      } @else if (!data()) {
        <div class="grid grid-cols-2 gap-3 lg:grid-cols-4" aria-busy="true">
          @for (i of [1, 2, 3, 4]; track i) {
            <div class="h-24 animate-pulse rounded-lg bg-surface-2"></div>
          }
        </div>
        <div class="h-64 animate-pulse rounded-lg bg-surface-2"></div>
      } @else {
        @let d = data()!;
        @if (isEmpty()) {
          <section class="th-card" aria-labelledby="welcome-heading">
            <h2 id="welcome-heading" class="font-semibold">Nothing here yet</h2>
            <p class="mt-1 text-sm text-muted">
              Add a client, then plan a job for them. The figures below fill in as you work.
            </p>
            <div class="mt-3 flex flex-wrap gap-2">
              @if (can('clients.write')) {
                <a [routerLink]="base() + '/clients/new'" class="th-button">Add a client</a>
              }
              @if (can('jobs.create')) {
                <a [routerLink]="base() + '/jobs/new'" class="th-button-secondary">Create a job</a>
              }
            </div>
          </section>
        }

        <ul class="grid grid-cols-2 gap-3 lg:grid-cols-4" aria-label="Key figures">
          @for (tile of tiles(); track tile.label) {
            <li>
              <a
                [routerLink]="tile.path"
                [queryParams]="tile.query"
                class="th-card group flex h-full flex-col gap-1 hover:bg-surface-2"
              >
                <span
                  class="text-3xl font-semibold tabular-nums"
                  [class.text-danger]="tile.tone === 'danger' && tile.value > 0"
                  >{{ tile.value }}</span
                >
                <span class="text-sm text-muted group-hover:underline">{{ ' ' + tile.label }}</span>
              </a>
            </li>
          }
        </ul>

        <div class="grid gap-6 lg:grid-cols-2">
          <section class="th-card">
            <app-status-chart [counts]="d.jobsByStatus" [jobsPath]="base() + '/jobs'" />
          </section>

          <section class="th-card" aria-labelledby="week-heading">
            <div class="flex flex-wrap items-baseline justify-between gap-2">
              <h2 id="week-heading" class="font-semibold">My next 7 days</h2>
              <a
                [routerLink]="base() + '/jobs'"
                [queryParams]="{ assignee: 'me', sort: 'due' }"
                class="text-sm font-medium text-accent underline-offset-2 hover:underline"
                >All my jobs</a
              >
            </div>
            @if (d.myNextSevenDays.length === 0) {
              <p class="mt-4 text-sm text-muted">Nothing scheduled for you in the next 7 days.</p>
            } @else {
              <ul class="mt-3 divide-y divide-border">
                @for (job of d.myNextSevenDays; track job.id) {
                  <li class="py-2.5">
                    <a
                      [routerLink]="base() + '/jobs/' + job.id"
                      class="group flex items-start justify-between gap-3"
                    >
                      <span class="min-w-0">
                        <span class="block truncate font-medium group-hover:underline">{{
                          job.title
                        }}</span>
                        <span class="block truncate text-sm text-muted"
                          >{{ job.label }} · {{ job.clientName }}</span
                        >
                      </span>
                      <span class="shrink-0 text-right text-sm">
                        <time [attr.datetime]="job.scheduledOn">{{
                          day(job.scheduledOn, { weekday: 'short', day: 'numeric', month: 'short' })
                        }}</time>
                        @if (job.isOverdue) {
                          <span class="block"><app-overdue-marker /></span>
                        }
                      </span>
                    </a>
                  </li>
                }
              </ul>
            }
          </section>

          <section class="th-card" aria-labelledby="usage-heading">
            <div class="flex flex-wrap items-baseline justify-between gap-2">
              <h2 id="usage-heading" class="font-semibold">Plan usage</h2>
              <p class="text-sm text-muted">
                {{ planLabels[d.plan] }} plan
                @if (canSeeSettings()) {
                  ·
                  <a
                    [routerLink]="base() + '/settings'"
                    fragment="plan"
                    class="font-medium text-accent underline-offset-2 hover:underline"
                    >Plans</a
                  >
                }
              </p>
            </div>
            <app-usage-meters
              class="mt-4 block"
              idPrefix="dashboard-meter-"
              [usage]="d.usage"
              [limits]="d.limits"
            />
          </section>

          @if (d.recentActivity; as activity) {
            <section class="th-card" aria-labelledby="activity-heading">
              <div class="flex flex-wrap items-baseline justify-between gap-2">
                <h2 id="activity-heading" class="font-semibold">Recent activity</h2>
                <a
                  [routerLink]="base() + '/audit'"
                  class="text-sm font-medium text-accent underline-offset-2 hover:underline"
                  >Audit log</a
                >
              </div>
              @if (activity.length === 0) {
                <p class="mt-4 text-sm text-muted">No activity yet.</p>
              } @else {
                <ul class="mt-3 divide-y divide-border">
                  @for (entry of activity; track entry.id) {
                    <li class="py-2.5 text-sm">
                      <p>{{ entry.summary }}</p>
                      <p class="text-muted">
                        {{ entry.actorName }} ·
                        <time [attr.datetime]="entry.occurredAt" [title]="entry.occurredAt">{{
                          ago(entry.occurredAt)
                        }}</time>
                      </p>
                    </li>
                  }
                </ul>
              }
            </section>
          }
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPage {
  readonly slug = input.required<string>();

  private readonly api = inject(DashboardApi);
  private readonly session = inject(SessionStore);

  protected readonly planLabels = planLabels;
  protected readonly data = signal<Dashboard | null>(null);
  protected readonly loadError = signal<ApiProblem | null>(null);

  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly base = computed(() => `/o/${this.slug()}`);
  protected readonly canSeeSettings = computed(
    () => this.can('org.manage') || this.can('billing.manage'),
  );
  protected readonly isEmpty = computed(() => {
    const d = this.data();
    return !!d && d.tiles.activeClients === 0 && d.jobsByStatus.every((s) => s.count === 0);
  });

  protected readonly tiles = computed<readonly Tile[]>(() => {
    const d = this.data();
    if (!d) {
      return [];
    }
    const jobs = `${this.base()}/jobs`;
    const tiles: Tile[] = [
      {
        label: 'Open jobs',
        value: d.tiles.openJobs,
        path: jobs,
        query: { status: 'new,scheduled,inProgress' },
      },
      {
        label: 'Overdue jobs',
        value: d.tiles.overdueJobs,
        path: jobs,
        query: { overdue: 'true' },
        tone: 'danger',
      },
      {
        label: 'Completed this month',
        value: d.tiles.completedThisMonth,
        path: jobs,
        query: { status: 'completed', completedFrom: d.monthStart },
      },
      {
        label: 'Active clients',
        value: d.tiles.activeClients,
        path: `${this.base()}/clients`,
        query: {},
      },
    ];
    return tiles;
  });

  constructor() {
    effect(() => {
      if (this.membership()) {
        void this.load();
      }
    });
  }

  protected can(permission: Permission): boolean {
    return this.session.can(this.slug(), permission);
  }

  protected day(iso: string, options: Intl.DateTimeFormatOptions): string {
    return formatDay(iso, options);
  }

  protected ago(iso: string): string {
    return relativeTime(iso);
  }

  protected async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.data.set(await this.api.get(this.membership()!.organizationId));
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    }
  }
}
