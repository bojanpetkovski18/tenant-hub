import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { JobStatusBadge } from '../jobs/job-badges';
import { BoardCard, hoursText } from './board.api';

/** Whether a card can still be planned: closed jobs keep their day (the API refuses with `job-closed`). */
export function isMovable(card: BoardCard): boolean {
  return card.status !== 'completed' && card.status !== 'cancelled';
}

/**
 * One job on the board. A component rather than an `ng-template`: `cdkDrag` goes on this element inside the
 * `cdkDropList` that renders it, so the drag finds its list through DI (a template outlet would resolve DI from where
 * the template was declared, and the card would become a free drag that never drops into a list).
 */
@Component({
  selector: 'app-board-card',
  imports: [RouterLink, JobStatusBadge],
  host: { class: 'block rounded-md border border-border bg-surface p-2 text-sm shadow-sm' },
  template: `
    <div class="flex flex-wrap items-center justify-between gap-1">
      <span class="font-mono text-xs text-muted">{{ card().label }}</span>
      <app-job-status-badge [status]="card().status" />
    </div>
    <a
      [routerLink]="['/o', slug(), 'jobs', card().id]"
      class="mt-1 block font-medium break-words hover:underline"
      >{{ card().title }}</a
    >
    <p class="mt-1 text-xs text-muted">
      {{ card().clientName }}
      @if (card().estimatedMinutes; as minutes) {
        · {{ hoursText(minutes) }}
      }
      @if (card().isVisit) {
        · visit
      }
    </p>
    @if (canPlan() && movable()) {
      <button
        type="button"
        class="mt-1 text-xs font-medium text-accent hover:underline"
        [disabled]="busy()"
        [attr.data-move]="card().id"
        (click)="move.emit()"
      >
        <!-- The visible words come first in the name (WCAG 2.5.3); the job tells the buttons apart. -->
        Move to…<span class="sr-only"> {{ card().label }}</span>
      </button>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BoardCardView {
  readonly card = input.required<BoardCard>();
  readonly slug = input.required<string>();
  readonly canPlan = input(false);
  readonly busy = input(false);
  /** "Move to…" was chosen. */
  readonly move = output();

  protected readonly hoursText = hoursText;

  protected movable(): boolean {
    return isMovable(this.card());
  }
}
