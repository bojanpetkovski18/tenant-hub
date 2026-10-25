import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { widthClass } from '../../shared/ui/usage-meters';
import { statusLabels } from '../jobs/jobs.api';
import { StatusCount } from './dashboard.api';

/** Each status's colour; the label and the number are always written out, so colour is never the only cue. */
const barColours: Readonly<Record<StatusCount['status'], string>> = {
  new: 'bg-accent/60',
  scheduled: 'bg-accent',
  inProgress: 'bg-warning',
  completed: 'bg-success',
  cancelled: 'bg-muted',
};

/**
 * Jobs by status as a horizontal bar chart (US-60). Accessible-chart practice, since the repo has no dataviz guide:
 * every bar is labelled with its status and count in text, bars start at zero and share one scale, each row links
 * to the matching list, and the same numbers are one click away as a real table. The bars are decoration
 * (aria-hidden); the links carry the information.
 */
@Component({
  selector: 'app-status-chart',
  imports: [RouterLink],
  template: `
    <figure>
      <div class="flex flex-wrap items-baseline justify-between gap-2">
        <figcaption id="status-chart-title" class="font-semibold">Jobs by status</figcaption>
        <button
          type="button"
          class="text-sm font-medium text-accent underline-offset-2 hover:underline"
          [attr.aria-pressed]="asTable()"
          (click)="asTable.set(!asTable())"
        >
          {{ asTable() ? 'Show as chart' : 'Show as table' }}
        </button>
      </div>

      @if (total() === 0) {
        <p class="mt-4 text-sm text-muted">No jobs yet.</p>
      } @else if (asTable()) {
        <table class="mt-4 w-full text-sm" aria-labelledby="status-chart-title">
          <thead>
            <tr class="border-b border-border text-left text-muted">
              <th scope="col" class="py-2 font-medium">Status</th>
              <th scope="col" class="py-2 text-right font-medium">Jobs</th>
              <th scope="col" class="py-2 text-right font-medium">Share</th>
            </tr>
          </thead>
          <tbody>
            @for (row of counts(); track row.status) {
              <tr class="border-b border-border last:border-0">
                <th scope="row" class="py-2 text-left font-normal">
                  <a
                    [routerLink]="jobsPath()"
                    [queryParams]="{ status: row.status }"
                    class="font-medium text-accent underline-offset-2 hover:underline"
                  >
                    {{ labels[row.status] }}
                  </a>
                </th>
                <td class="py-2 text-right tabular-nums">{{ row.count }}</td>
                <td class="py-2 text-right tabular-nums">{{ share(row.count) }}%</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr class="font-medium">
              <th scope="row" class="py-2 text-left">Total</th>
              <td class="py-2 text-right tabular-nums">{{ total() }}</td>
              <td></td>
            </tr>
          </tfoot>
        </table>
      } @else {
        <ul class="mt-4 space-y-3" aria-labelledby="status-chart-title">
          @for (row of counts(); track row.status) {
            <li>
              <a
                [routerLink]="jobsPath()"
                [queryParams]="{ status: row.status }"
                class="group block rounded-md"
              >
                <span class="flex justify-between gap-2 text-sm">
                  <span class="group-hover:underline">{{ labels[row.status] }}</span>
                  <span class="tabular-nums text-muted">{{ ' ' + row.count }}</span>
                </span>
                <span class="mt-1 block h-3 rounded-sm bg-surface-2" aria-hidden="true">
                  <span [class]="bar(row)"></span>
                </span>
              </a>
            </li>
          }
        </ul>
      }
    </figure>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatusChart {
  readonly counts = input.required<readonly StatusCount[]>();
  /** The jobs list of this organization, e.g. /o/brightline/jobs. */
  readonly jobsPath = input.required<string>();

  protected readonly labels = statusLabels;
  protected readonly asTable = signal(false);
  protected readonly total = computed(() => this.counts().reduce((sum, c) => sum + c.count, 0));
  /** The longest bar fills the track; the others share its scale. */
  private readonly max = computed(() => Math.max(1, ...this.counts().map((c) => c.count)));

  protected share(count: number): number {
    return Math.round((count / Math.max(1, this.total())) * 100);
  }

  protected bar(row: StatusCount): string {
    return `block h-full rounded-sm ${barColours[row.status]} ${widthClass((row.count / this.max()) * 100)}`;
  }
}
