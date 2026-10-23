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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { Member, MembersApi } from '../members/members.api';
import {
  AuditQuery,
  activeAuditFilterCount,
  auditApiParams,
  auditUrlChanges,
  parseAuditQuery,
} from './audit-query';
import { AuditApi, AuditEntry, AuditPage, auditActions, entityTypeLabels } from './audit.api';

const pageSize = 50;

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
  name: 'Name',
  contactName: 'Contact person',
  email: 'Email',
  phone: 'Phone',
  address: 'Address',
  notes: 'Notes',
  role: 'Role',
};

const relativeFormat = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });

/** "just now", "5 minutes ago", "yesterday", "3 weeks ago": the browser's own wording, no library. */
export function relativeTime(iso: string, now: number = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000);
  const steps: readonly [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 31_536_000],
    ['month', 2_592_000],
    ['week', 604_800],
    ['day', 86_400],
    ['hour', 3_600],
    ['minute', 60],
  ];
  for (const [unit, size] of steps) {
    if (Math.abs(seconds) >= size) {
      return relativeFormat.format(Math.round(seconds / size), unit);
    }
  }
  return 'just now';
}

/**
 * US-40: the organization's activity, newest first, for Admins and Owners (`audit.read`). Filters and the page
 * cursor live in the URL. Entries are grouped by day; each shows who, when (relative and absolute), what, a link to
 * the thing if it still exists, and its changed fields on demand. Entries cannot be edited (D012).
 */
@Component({
  selector: 'app-audit-page',
  imports: [DatePipe, RouterLink, EmptyState, ErrorState],
  template: `
    <div class="space-y-5">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight">Audit log</h1>
        <p class="mt-1 text-sm text-muted">
          Every change in this organization, newest first. Entries are never edited or removed.
        </p>
      </div>

      @if (!canRead()) {
        <app-empty-state
          title="The audit log is for Admins and Owners"
          message="Each job's own history is on the job's page."
        />
      } @else {
        <section aria-label="Filters" class="th-card grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
          <div>
            <label class="th-label" for="audit-actor">Person</label>
            <select
              #actorSelect
              id="audit-actor"
              class="th-input"
              (change)="change({ actor: actorSelect.value })"
            >
              <option value="" [selected]="!query().actor">Anyone</option>
              @for (member of members(); track member.userId) {
                <option [value]="member.userId" [selected]="member.userId === query().actor">
                  {{ member.displayName }}
                </option>
              }
            </select>
          </div>
          <div>
            <label class="th-label" for="audit-action">Action</label>
            <select
              #actionSelect
              id="audit-action"
              class="th-input"
              (change)="change({ action: actionSelect.value })"
            >
              <option value="" [selected]="!query().action">Any action</option>
              @for (group of actions; track group.type) {
                <optgroup [label]="entityTypeLabels[group.type] + 's'">
                  @for (action of group.actions; track action[0]) {
                    <option [value]="action[0]" [selected]="action[0] === query().action">
                      {{ action[1] }}
                    </option>
                  }
                </optgroup>
              }
            </select>
          </div>
          <div>
            <label class="th-label" for="audit-type">About</label>
            <select
              #typeSelect
              id="audit-type"
              class="th-input"
              (change)="setType(typeSelect.value)"
            >
              <option value="" [selected]="!query().entityType">Anything</option>
              @for (group of actions; track group.type) {
                <option [value]="group.type" [selected]="group.type === query().entityType">
                  {{ entityTypeLabels[group.type] }}s
                </option>
              }
            </select>
          </div>
          <div>
            <label class="th-label" for="audit-from">From</label>
            <input
              #fromInput
              id="audit-from"
              type="date"
              class="th-input"
              [value]="query().from"
              (change)="change({ from: fromInput.value })"
            />
          </div>
          <div>
            <label class="th-label" for="audit-to">To</label>
            <input
              #toInput
              id="audit-to"
              type="date"
              class="th-input"
              [value]="query().to"
              (change)="change({ to: toInput.value })"
            />
          </div>
          @if (query().entityId || filterCount() > 0) {
            <div class="flex flex-wrap items-center gap-3 sm:col-span-2 lg:col-span-5">
              @if (query().entityId) {
                <span class="th-badge bg-accent-soft text-fg">
                  The history of one {{ (query().entityType || 'thing').toLowerCase() }}
                </span>
              }
              <button type="button" class="th-button-secondary" (click)="clear()">
                Show everything
              </button>
            </div>
          }
        </section>

        @if (error(); as problem) {
          <app-error-state [message]="problem.title" (retry)="load()" />
        } @else if (page() === null) {
          <div class="space-y-3" aria-busy="true">
            @for (row of [1, 2, 3, 4]; track row) {
              <div class="h-14 animate-pulse rounded-lg bg-surface-2"></div>
            }
          </div>
        } @else if (page()!.items.length === 0) {
          @if (filterCount() > 0 || query().before) {
            <app-empty-state
              title="No activity matches"
              message="Try another person, action or date range."
            >
              <button type="button" class="th-button-secondary" (click)="clear()">
                Show everything
              </button>
            </app-empty-state>
          } @else {
            <app-empty-state
              title="No activity yet"
              message="Changes to clients, jobs and members will appear here."
            />
          }
        } @else {
          <div class="space-y-6" [attr.aria-busy]="loading()">
            @for (day of days(); track day.key) {
              <section [attr.aria-labelledby]="'day-' + day.key">
                <h2
                  [id]="'day-' + day.key"
                  class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted"
                >
                  {{ day.date | date: 'fullDate' }}
                </h2>
                <ol class="th-card divide-y divide-border p-0 sm:p-0">
                  @for (entry of day.entries; track entry.id) {
                    <li class="flex gap-3 p-4">
                      <span
                        class="flex size-8 shrink-0 items-center justify-center rounded-full bg-surface-2 text-xs font-semibold uppercase"
                        aria-hidden="true"
                        >{{ entry.actorName.charAt(0) }}</span
                      >
                      <div class="min-w-0 flex-1">
                        <p class="break-words font-medium">{{ entry.summary }}</p>
                        <p class="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs text-muted">
                          <span>{{ entry.actorName }}</span>
                          <span aria-hidden="true">·</span>
                          <time [attr.datetime]="entry.occurredAt">
                            {{ relative(entry.occurredAt) }},
                            {{ entry.occurredAt | date: 'shortTime' }}
                          </time>
                          <span aria-hidden="true">·</span>
                          <span>{{ entityTypeLabels[entry.entityType] }}</span>
                          @if (linkTo(entry); as link) {
                            <a
                              [routerLink]="link.path"
                              [queryParams]="link.query"
                              class="text-accent underline-offset-2 hover:underline"
                              >Open<span class="sr-only">
                                {{ entityTypeLabels[entry.entityType].toLowerCase() }}</span
                              ></a
                            >
                          }
                          @if (
                            !query().entityId &&
                            (entry.entityType === 'job' || entry.entityType === 'client')
                          ) {
                            <a
                              routerLink="."
                              [queryParams]="{
                                entityType: entry.entityType,
                                entityId: entry.entityId,
                                before: null,
                                action: null,
                              }"
                              queryParamsHandling="merge"
                              class="text-accent underline-offset-2 hover:underline"
                              >Its history</a
                            >
                          }
                        </p>
                        @if (entry.changes.length > 0) {
                          <button
                            type="button"
                            class="mt-1 text-xs font-medium text-accent underline-offset-2 hover:underline"
                            [attr.aria-expanded]="expanded().has(entry.id)"
                            [attr.aria-controls]="'changes-' + entry.id"
                            (click)="toggle(entry.id)"
                          >
                            {{ expanded().has(entry.id) ? 'Hide' : 'Show' }}
                            {{ entry.changes.length }}
                            {{ entry.changes.length === 1 ? 'field' : 'fields' }}
                          </button>
                          @if (expanded().has(entry.id)) {
                            <dl
                              [id]="'changes-' + entry.id"
                              class="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm"
                            >
                              @for (change of entry.changes; track change.field) {
                                <dt class="text-muted">{{ label(change.field) }}</dt>
                                <dd class="min-w-0 break-words">
                                  @if (change.from !== null) {
                                    <span class="text-muted line-through">{{ change.from }}</span>
                                    <span aria-hidden="true"> → </span>
                                    <span class="sr-only"> changed to </span>
                                  }
                                  {{ change.to ?? '(cleared)' }}
                                </dd>
                              }
                            </dl>
                          }
                        }
                      </div>
                    </li>
                  }
                </ol>
              </section>
            }
          </div>

          <nav class="flex flex-wrap items-center justify-between gap-3" aria-label="Pages">
            @if (query().before) {
              <button type="button" class="th-button-secondary" (click)="change({ before: '' })">
                Newest
              </button>
            } @else {
              <span></span>
            }
            @if (page()!.nextCursor; as next) {
              <button type="button" class="th-button-secondary" (click)="change({ before: next })">
                Older entries
              </button>
            } @else {
              <p class="text-sm text-muted">That is everything.</p>
            }
          </nav>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuditLogPage {
  readonly slug = input.required<string>();
  readonly actor = input<string>();
  readonly action = input<string>();
  readonly entityType = input<string>();
  readonly entityId = input<string>();
  readonly from = input<string>();
  readonly to = input<string>();
  readonly before = input<string>();

  private readonly api = inject(AuditApi);
  private readonly membersApi = inject(MembersApi);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly actions = auditActions;
  protected readonly entityTypeLabels = entityTypeLabels;
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canRead = computed(() => this.session.can(this.slug(), 'audit.read'));

  protected readonly query = computed(() =>
    parseAuditQuery({
      actor: this.actor(),
      action: this.action(),
      entityType: this.entityType(),
      entityId: this.entityId(),
      from: this.from(),
      to: this.to(),
      before: this.before(),
    }),
  );
  protected readonly filterCount = computed(() => activeAuditFilterCount(this.query()));

  protected readonly page = signal<AuditPage | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiProblem | null>(null);
  protected readonly members = signal<Member[]>([]);
  protected readonly expanded = signal<ReadonlySet<number>>(new Set());
  private latest = 0;

  /** Entries grouped by their local day, in order. */
  protected readonly days = computed(() => {
    const groups: { key: string; date: Date; entries: AuditEntry[] }[] = [];
    for (const entry of this.page()?.items ?? []) {
      const date = new Date(entry.occurredAt);
      const key = `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
      const last = groups.at(-1);
      if (last?.key === key) {
        last.entries.push(entry);
      } else {
        groups.push({ key, date, entries: [entry] });
      }
    }
    return groups;
  });

  constructor() {
    effect(() => {
      const membership = this.membership();
      this.query();
      if (membership && this.canRead()) {
        void this.load();
      }
    });

    effect(() => {
      const membership = this.membership();
      if (membership && this.canRead()) {
        void this.membersApi.members(membership.organizationId).then(
          (members) => this.members.set(members),
          () => this.members.set([]),
        );
      }
    });
  }

  protected relative(iso: string): string {
    return relativeTime(iso);
  }

  protected label(field: string): string {
    return fieldLabels[field] ?? field;
  }

  /** Where an entry's subject lives, if it still exists. */
  protected linkTo(
    entry: AuditEntry,
  ): { path: string[]; query: Record<string, string> | null } | null {
    if (!entry.entityExists) {
      return null;
    }
    const base = ['/o', this.slug()];
    switch (entry.entityType) {
      case 'job':
        return { path: [...base, 'jobs', entry.entityId], query: null };
      case 'client':
        return { path: [...base, 'clients', entry.entityId], query: null };
      case 'member':
        return { path: [...base, 'members'], query: null };
      case 'invitation':
        return { path: [...base, 'members'], query: { tab: 'pending' } };
    }
  }

  protected setType(value: string): void {
    this.change({ entityType: parseAuditQuery({ entityType: value }).entityType });
  }

  protected toggle(id: number): void {
    const next = new Set(this.expanded());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.expanded.set(next);
  }

  protected async load(): Promise<void> {
    const membership = this.membership();
    if (!membership) {
      return;
    }
    const request = ++this.latest;
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.list(
        membership.organizationId,
        auditApiParams(this.query(), pageSize),
      );
      if (request === this.latest) {
        this.page.set(page);
        this.expanded.set(new Set());
      }
    } catch (error) {
      if (request === this.latest) {
        this.error.set(toApiProblem(error));
      }
    } finally {
      if (request === this.latest) {
        this.loading.set(false);
      }
    }
  }

  protected clear(): void {
    this.change({ actor: '', action: '', entityType: '', entityId: '', from: '', to: '' });
  }

  protected change(changes: Partial<AuditQuery>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: auditUrlChanges(changes),
      queryParamsHandling: 'merge',
    });
  }
}
