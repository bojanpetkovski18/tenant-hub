import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { JobPriority, JobStatus, priorityLabels, statusLabels } from './jobs.api';

const statusIcons: Readonly<Record<JobStatus, string>> = {
  new: 'M12 8v8M8 12h8',
  scheduled:
    'M8 2v4M16 2v4M3 10h18M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z',
  inProgress: 'M5 12h14M13 6l6 6-6 6',
  completed: 'm5 12 5 5L20 7',
  cancelled: 'M18 6 6 18M6 6l12 12',
};

/** Status with an icon and text, never colour alone (SPEC 6). */
@Component({
  selector: 'app-job-status-badge',
  template: `
    <span [class]="'th-badge gap-1 ' + tone()">
      <svg
        viewBox="0 0 24 24"
        class="size-3"
        fill="none"
        stroke="currentColor"
        stroke-width="2.5"
        aria-hidden="true"
      >
        <path [attr.d]="icon()" />
      </svg>
      {{ label() }}
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobStatusBadge {
  readonly status = input.required<JobStatus>();

  protected readonly label = computed(() => statusLabels[this.status()]);
  protected readonly icon = computed(() => statusIcons[this.status()]);
  protected readonly tone = computed(() => {
    switch (this.status()) {
      case 'inProgress':
        return 'bg-accent-soft text-fg';
      case 'completed':
        return 'bg-success/15 text-fg';
      case 'cancelled':
        return 'line-through decoration-1';
      default:
        return '';
    }
  });
}

/** Priority with bars and text: one to four bars from Low to Urgent. */
@Component({
  selector: 'app-job-priority',
  template: `
    <span
      class="inline-flex items-center gap-1.5 text-sm"
      [class.font-semibold]="priority() === 'urgent'"
      [class.text-danger]="priority() === 'urgent'"
    >
      <svg viewBox="0 0 16 12" class="h-3 w-4" aria-hidden="true">
        @for (bar of bars; track bar) {
          <rect
            [attr.x]="bar * 4"
            [attr.y]="9 - bar * 3"
            width="3"
            [attr.height]="3 + bar * 3"
            [attr.fill]="bar < level() ? 'currentColor' : 'none'"
            stroke="currentColor"
            stroke-width="0.75"
          />
        }
      </svg>
      {{ label() }}
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobPriorityLabel {
  readonly priority = input.required<JobPriority>();

  protected readonly bars = [0, 1, 2, 3];
  protected readonly label = computed(() => priorityLabels[this.priority()]);
  protected readonly level = computed(
    () => ({ low: 1, normal: 2, high: 3, urgent: 4 })[this.priority()],
  );
}

/** "Overdue" with an icon, for the list and the detail page (judged by the API in the organization's zone). */
@Component({
  selector: 'app-overdue-marker',
  template: `
    <span class="inline-flex items-center gap-1 text-xs font-semibold text-danger">
      <svg
        viewBox="0 0 24 24"
        class="size-3.5"
        fill="none"
        stroke="currentColor"
        stroke-width="2.5"
        aria-hidden="true"
      >
        <path d="M12 8v4l2 2M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18Z" />
      </svg>
      Overdue
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OverdueMarker {}
