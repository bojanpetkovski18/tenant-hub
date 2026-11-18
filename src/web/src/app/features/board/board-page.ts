import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { NgTemplateOutlet } from '@angular/common';
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
import { Confirm } from '../../shared/ui/confirm-dialog';
import { ErrorState } from '../../shared/ui/states';
import { ClientOption, ClientsApi } from '../clients/clients.api';
import { JobStatusBadge } from '../jobs/job-badges';
import { jobStatuses, statusLabels } from '../jobs/jobs.api';
import {
  BoardApi,
  BoardCard,
  BoardPerson,
  BoardView,
  addDays,
  dayText,
  hoursText,
} from './board.api';
import { AskMove } from './move-dialog';

/** Where a card can be dropped: a person (or nobody) on a day. */
interface Cell {
  readonly assigneeUserId: string | null;
  readonly day: string;
}

interface Row {
  readonly userId: string | null;
  readonly name: string;
  readonly active: boolean;
}

const unassigned: Row = { userId: null, name: 'Unassigned', active: true };

/**
 * US-75: the dispatch board. One week in the organization's zone (week, client and status in the URL), people by
 * days. Managers move cards by dragging (pointer, touch) or with "Move to…" (keyboard, WCAG 2.5.7); every move goes
 * through `POST /jobs/{id}/schedule` with the version shown, so a move made meanwhile is a conflict, not a lost
 * update. Cards are not moved until the API agrees: the board reloads after each move, and a refused one simply
 * stays where it was. Members and Viewers see the same board without the moves.
 */
@Component({
  selector: 'app-board-page',
  imports: [
    RouterLink,
    NgTemplateOutlet,
    CdkDropListGroup,
    CdkDropList,
    CdkDrag,
    ErrorState,
    JobStatusBadge,
  ],
  template: `
    <div class="space-y-5">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 class="text-2xl font-semibold tracking-tight">Board</h1>
          <p class="mt-1 text-sm text-muted">
            {{ weekLabel() }}
            @if (board(); as b) {
              · capacity {{ hoursText(b.dailyCapacityMinutes) }} a day per person
            }
          </p>
        </div>
        <nav class="flex flex-wrap gap-2" aria-label="Weeks">
          <button type="button" class="th-button-secondary" (click)="goToWeek(-7)">
            <span aria-hidden="true">←</span> Previous week
          </button>
          <button type="button" class="th-button-secondary" (click)="navigate({ week: null })">
            This week
          </button>
          <button type="button" class="th-button-secondary" (click)="goToWeek(7)">
            Next week <span aria-hidden="true">→</span>
          </button>
        </nav>
      </div>

      <div class="flex flex-wrap items-end gap-3" role="search">
        <div>
          <label class="th-label" for="board-client">Client</label>
          <select
            id="board-client"
            class="th-input"
            #clientSelect
            (change)="navigate({ clientId: clientSelect.value || null })"
          >
            <option value="" [selected]="!clientId()">All clients</option>
            @for (client of clients(); track client.id) {
              <option [value]="client.id" [selected]="client.id === clientId()">
                {{ client.name }}
              </option>
            }
          </select>
        </div>
        <div>
          <label class="th-label" for="board-status">Status</label>
          <select
            id="board-status"
            class="th-input"
            #statusSelect
            (change)="navigate({ status: statusSelect.value || null })"
          >
            <option value="" [selected]="!statusFilter()">All statuses</option>
            @for (s of statuses; track s) {
              <option [value]="s" [selected]="s === statusFilter()">{{ statusLabels[s] }}</option>
            }
          </select>
        </div>
      </div>

      <p class="sr-only" aria-live="polite" aria-atomic="true">{{ announcement() }}</p>
      @if (message(); as text) {
        <div class="th-alert-error" role="alert">{{ text }}</div>
      }

      @if (error(); as problem) {
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (!board()) {
        <div class="h-96 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else {
        @let b = board()!;
        @if (!b.canPlan) {
          <p class="text-sm text-muted">You can see the plan; Admins and Owners move the work.</p>
        }

        <div cdkDropListGroup class="space-y-5" [attr.aria-busy]="busy()">
          <!-- Wide screens: people by days. -->
          <div class="th-card hidden overflow-x-auto p-0 md:block">
            <table class="w-full min-w-[56rem] table-fixed border-collapse text-sm">
              <caption class="sr-only">
                Planned work, people by days,
                {{
                  weekLabel()
                }}
              </caption>
              <thead>
                <tr class="border-b border-border bg-surface-2">
                  <th
                    scope="col"
                    class="w-36 px-3 py-2 text-left text-xs font-semibold uppercase text-muted"
                  >
                    Person
                  </th>
                  @for (day of days(); track day) {
                    <th
                      scope="col"
                      class="px-2 py-2 text-left text-xs font-semibold"
                      [class.text-accent]="day === b.today"
                      [attr.aria-current]="day === b.today ? 'date' : null"
                    >
                      {{ shortDay(day) }}
                    </th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track row.userId) {
                  <tr class="border-b border-border align-top last:border-0">
                    <th scope="row" class="px-3 py-2 text-left font-medium">
                      {{ row.name }}
                      @if (!row.active) {
                        <span class="block text-xs font-normal text-muted">no longer a member</span>
                      }
                    </th>
                    @for (day of days(); track day) {
                      <td class="border-l border-border p-1.5">
                        <div
                          cdkDropList
                          [cdkDropListData]="cell(row, day)"
                          [cdkDropListDisabled]="!b.canPlan"
                          (cdkDropListDropped)="dropped($event)"
                          class="min-h-16 space-y-1.5 rounded"
                          [attr.aria-label]="row.name + ', ' + dayText(day)"
                        >
                          @for (card of cardsIn(row, day); track card.id) {
                            <ng-container
                              [ngTemplateOutlet]="cardTemplate"
                              [ngTemplateOutletContext]="{ $implicit: card, day }"
                            />
                          }
                        </div>
                        @if (row.userId) {
                          <ng-container
                            [ngTemplateOutlet]="loadTemplate"
                            [ngTemplateOutletContext]="{ $implicit: loadOf(row.userId, day) }"
                          />
                        }
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <!-- Narrow screens: day by day, person by person. -->
          <div class="space-y-4 md:hidden">
            @for (day of days(); track day) {
              <section class="th-card" [attr.aria-labelledby]="'day-' + day">
                <h2 [id]="'day-' + day" class="font-semibold" [class.text-accent]="day === b.today">
                  {{ dayText(day) }}
                </h2>
                @let people = rowsWithWork(day);
                @if (people.length === 0) {
                  <p class="mt-1 text-sm text-muted">Nothing planned.</p>
                }
                @for (row of people; track row.userId) {
                  <h3 class="mt-3 text-sm font-medium">{{ row.name }}</h3>
                  @if (row.userId) {
                    <ng-container
                      [ngTemplateOutlet]="loadTemplate"
                      [ngTemplateOutletContext]="{ $implicit: loadOf(row.userId, day) }"
                    />
                  }
                  <div class="mt-1 space-y-1.5">
                    @for (card of cardsIn(row, day); track card.id) {
                      <ng-container
                        [ngTemplateOutlet]="cardTemplate"
                        [ngTemplateOutletContext]="{ $implicit: card, day, phone: true }"
                      />
                    }
                  </div>
                }
              </section>
            }
          </div>

          <section class="th-card" aria-labelledby="queue-heading">
            <h2 id="queue-heading" class="font-semibold">
              Not scheduled ({{ b.unscheduledTotal }})
            </h2>
            <p class="mt-1 text-sm text-muted">
              Open jobs without a day, the nearest due first.
              @if (b.unscheduledTotal > b.unscheduled.length) {
                The {{ b.unscheduled.length }} nearest are here;
                <a
                  [routerLink]="['/o', slug(), 'jobs']"
                  class="font-medium text-accent hover:underline"
                  >the jobs list</a
                >
                has the rest.
              }
            </p>
            @if (b.unscheduled.length === 0) {
              <p class="mt-2 text-sm text-muted">Everything open has a day.</p>
            }
            <div
              cdkDropList
              [cdkDropListEnterPredicate]="noDrop"
              [cdkDropListDisabled]="!b.canPlan"
              class="mt-3 grid gap-1.5 sm:grid-cols-2 lg:grid-cols-4"
              aria-label="Not scheduled"
            >
              @for (card of b.unscheduled; track card.id) {
                <ng-container
                  [ngTemplateOutlet]="cardTemplate"
                  [ngTemplateOutletContext]="{ $implicit: card, day: null }"
                />
              }
            </div>
          </section>
        </div>

        <ng-template #cardTemplate let-card let-day="day" let-phone="phone">
          <div
            cdkDrag
            [cdkDragData]="card"
            [cdkDragDisabled]="!b.canPlan || phone"
            class="rounded-md border border-border bg-surface p-2 text-sm shadow-sm"
            [class.cursor-grab]="b.canPlan && !phone"
          >
            <div class="flex items-start justify-between gap-2">
              <a
                [routerLink]="['/o', slug(), 'jobs', card.id]"
                class="min-w-0 font-medium hover:underline"
              >
                <span class="font-mono text-xs text-muted">{{ card.label }}</span>
                {{ card.title }}
              </a>
              <app-job-status-badge [status]="card.status" />
            </div>
            <p class="mt-1 text-xs text-muted">
              {{ card.clientName }}
              @if (card.estimatedMinutes) {
                · {{ hoursText(card.estimatedMinutes) }}
              }
              @if (card.isVisit) {
                · visit
              }
            </p>
            @if (b.canPlan && card.status !== 'completed' && card.status !== 'cancelled') {
              <button
                type="button"
                class="mt-1 text-xs font-medium text-accent hover:underline"
                [disabled]="busy()"
                [attr.aria-label]="'Move ' + card.label + ' to another person or day'"
                (click)="openMove(card, day)"
              >
                Move to…
              </button>
            }
          </div>
        </ng-template>

        <ng-template #loadTemplate let-minutes>
          @let capacity = b.dailyCapacityMinutes;
          @if (minutes > 0) {
            <p
              class="mt-1 flex items-center gap-1 px-1 text-xs"
              [class.text-muted]="minutes <= capacity"
              [class.text-danger]="minutes > capacity"
              [class.font-semibold]="minutes > capacity"
            >
              @if (minutes > capacity) {
                <svg
                  viewBox="0 0 24 24"
                  class="size-3.5 shrink-0"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  aria-hidden="true"
                >
                  <path
                    d="M12 9v4M12 17h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"
                  />
                </svg>
                Over: {{ hoursText(minutes) }} of {{ hoursText(capacity) }}
              } @else {
                {{ hoursText(minutes) }} of {{ hoursText(capacity) }}
              }
            </p>
          }
        </ng-template>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BoardPage {
  /** Route parameter (inherited from /o/:slug). */
  readonly slug = input.required<string>();
  /** Query parameters: any day of the week to show, a client, a status. */
  readonly week = input<string>();
  readonly clientId = input<string>();
  readonly status = input<string>();

  private readonly api = inject(BoardApi);
  private readonly clientsApi = inject(ClientsApi);
  private readonly session = inject(SessionStore);
  private readonly askMove = inject(AskMove);
  private readonly confirm = inject(Confirm);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));

  protected readonly statuses = jobStatuses;
  protected readonly statusLabels = statusLabels;
  protected readonly hoursText = hoursText;
  protected readonly dayText = dayText;
  protected readonly board = signal<BoardView | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly announcement = signal('');
  protected readonly clients = signal<ClientOption[]>([]);
  /** The unscheduled area is a source only: a card leaves it by getting a day. */
  protected readonly noDrop = () => false;

  protected readonly statusFilter = computed(
    () => jobStatuses.find((s) => s === this.status()) ?? null,
  );
  protected readonly days = computed(() => {
    const start = this.board()?.weekStart;
    return start ? Array.from({ length: 7 }, (_, i) => addDays(start, i)) : [];
  });
  protected readonly weekLabel = computed(() => {
    const days = this.days();
    if (days.length === 0) {
      return 'Loading the week…';
    }
    const format = (d: string, options: Intl.DateTimeFormatOptions) =>
      new Intl.DateTimeFormat('en-GB', { ...options, timeZone: 'UTC' }).format(
        new Date(`${d}T00:00:00Z`),
      );
    return `Week of ${format(days[0]!, { day: 'numeric', month: 'long' })} to ${format(days[6]!, { day: 'numeric', month: 'long', year: 'numeric' })}`;
  });
  protected readonly rows = computed<readonly Row[]>(() => [
    ...(this.board()?.people ?? []).map((p: BoardPerson) => ({
      userId: p.userId,
      name: p.name,
      active: p.active,
    })),
    unassigned,
  ]);
  private readonly cardsByCell = computed(() => {
    const map = new Map<string, BoardCard[]>();
    for (const card of this.board()?.cards ?? []) {
      const key = `${card.assigneeUserId ?? ''}|${card.scheduledOn}`;
      map.set(key, [...(map.get(key) ?? []), card]);
    }
    return map;
  });
  private readonly loadByCell = computed(
    () =>
      new Map(
        (this.board()?.load ?? []).map((l) => [`${l.assigneeUserId ?? ''}|${l.day}`, l.minutes]),
      ),
  );

  constructor() {
    effect(() => {
      const membership = this.membership();
      this.week();
      this.clientId();
      this.statusFilter();
      if (membership) {
        void this.load();
      }
    });
    effect(() => {
      const membership = this.membership();
      if (membership) {
        void this.clientsApi
          .options(membership.organizationId)
          .then((clients) => this.clients.set(clients.filter((c) => c.status === 'active')))
          .catch(() => this.clients.set([]));
      }
    });
  }

  protected cell(row: Row, day: string): Cell {
    return { assigneeUserId: row.userId, day };
  }

  protected cardsIn(row: Row, day: string): readonly BoardCard[] {
    return this.cardsByCell().get(`${row.userId ?? ''}|${day}`) ?? [];
  }

  protected loadOf(userId: string, day: string): number {
    return this.loadByCell().get(`${userId}|${day}`) ?? 0;
  }

  protected rowsWithWork(day: string): readonly Row[] {
    return this.rows().filter((row) => this.cardsIn(row, day).length > 0);
  }

  protected shortDay(day: string): string {
    return new Intl.DateTimeFormat('en-GB', {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      timeZone: 'UTC',
    }).format(new Date(`${day}T00:00:00Z`));
  }

  async load(): Promise<void> {
    const membership = this.membership();
    if (!membership) {
      return;
    }
    this.error.set(null);
    const status = this.statusFilter();
    try {
      this.board.set(
        await this.api.get(membership.organizationId, {
          ...(this.week() ? { week: this.week()! } : {}),
          ...(this.clientId() ? { clientId: this.clientId()! } : {}),
          ...(status ? { status } : {}),
        }),
      );
    } catch (error) {
      this.error.set(toApiProblem(error));
    }
  }

  protected goToWeek(days: number): void {
    const start = this.board()?.weekStart;
    if (start) {
      this.navigate({ week: addDays(start, days) });
    }
  }

  protected navigate(changes: Record<string, string | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: changes,
      queryParamsHandling: 'merge',
    });
  }

  protected dropped(event: CdkDragDrop<Cell>): void {
    const card = event.item.data as BoardCard;
    const target = event.container.data;
    if (card.assigneeUserId === target.assigneeUserId && card.scheduledOn === target.day) {
      return;
    }
    void this.move(card, target.assigneeUserId, target.day);
  }

  protected async openMove(card: BoardCard, day: string | null): Promise<void> {
    const board = this.board();
    if (!board) {
      return;
    }
    const choice = await this.askMove.ask({
      card,
      people: board.people,
      day:
        card.scheduledOn ?? day ?? (board.weekStart > board.today ? board.weekStart : board.today),
    });
    if (choice) {
      await this.move(card, choice.assigneeUserId, choice.scheduledOn);
    }
  }

  /**
   * One move through the API. Nothing on the board changes until it answers; then the board is read again (new
   * versions, new load). A day past the due date asks whether to move the due date too; a conflict explains itself.
   */
  private async move(
    card: BoardCard,
    assigneeUserId: string | null,
    day: string,
    moveDueDate = false,
  ): Promise<void> {
    const organizationId = this.membership()!.organizationId;
    this.busy.set(true);
    this.message.set(null);
    try {
      await this.api.move(organizationId, card.id, {
        assigneeUserId,
        scheduledOn: day,
        version: card.version,
        moveDueDate,
      });
      const person = this.rows().find((r) => r.userId === assigneeUserId)?.name ?? 'Unassigned';
      this.announcement.set(
        `${card.label} moved to ${assigneeUserId ? person : 'nobody (unassigned)'}, ${dayText(day)}.`,
      );
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'after-due-date' && !moveDueDate) {
        this.busy.set(false);
        const confirmed = await this.confirm.ask({
          title: `Move the due date of ${card.label} too?`,
          message: `${problem.title.split('. ')[0]}. Moving it to ${dayText(day)} moves the due date to that day as well.`,
          confirmLabel: 'Move both',
        });
        if (confirmed) {
          await this.move(card, assigneeUserId, day, true);
          return;
        }
        this.announcement.set(`${card.label} was not moved.`);
      } else if (problem.code === 'concurrency-conflict') {
        this.message.set(
          `Someone else changed ${card.label} while you were planning, so it was not moved. The board now shows it as it is.`,
        );
        this.announcement.set(`${card.label} was not moved: someone else changed it.`);
      } else {
        this.message.set(problem.title);
        this.announcement.set(`${card.label} was not moved.`);
      }
    } finally {
      this.busy.set(false);
    }
    await this.load();
  }
}
