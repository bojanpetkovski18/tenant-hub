import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { LimitName } from '../../core/http/api-problem';
import { PlanLimits, PlanUsage, limitLabels, limitNames, usageText } from '../plan/plan';

/** Literal class names, so Tailwind generates each width (classes, not an inline style: CSP, D023). */
const widths = [
  'w-0',
  'w-[5%]',
  'w-[10%]',
  'w-[15%]',
  'w-[20%]',
  'w-[25%]',
  'w-[30%]',
  'w-[35%]',
  'w-[40%]',
  'w-[45%]',
  'w-[50%]',
  'w-[55%]',
  'w-[60%]',
  'w-[65%]',
  'w-[70%]',
  'w-[75%]',
  'w-[80%]',
  'w-[85%]',
  'w-[90%]',
  'w-[95%]',
  'w-full',
];

/** A width class for a share from 0 to 100, in 5% steps; anything above a sliver shows at least 5%. */
export function widthClass(percent: number): string {
  const step = Math.round(Math.min(100, Math.max(0, percent)) / 5);
  return widths[percent > 0 && step === 0 ? 1 : step] ?? 'w-full';
}

/**
 * Usage against the plan's limits (US-50): one meter per limited resource, coloured as it nears the limit, and a
 * line of text for unlimited ones. The text says the same as the meter, for screen readers and at a glance.
 */
@Component({
  selector: 'app-usage-meters',
  template: `
    <div class="space-y-4">
      @for (limit of limitNames; track limit) {
        <div>
          <div class="flex flex-wrap items-baseline justify-between gap-2 text-sm">
            <span class="font-medium" [id]="idPrefix() + limit">{{
              limitLabels[limit].title
            }}</span>
            <span class="text-muted">{{ text(limit) }}</span>
          </div>
          @if (limits()[limit] !== null) {
            <div
              role="meter"
              class="mt-1.5 h-2 overflow-hidden rounded-full bg-surface-2"
              aria-valuemin="0"
              [attr.aria-valuemax]="limits()[limit]"
              [attr.aria-valuenow]="usage()[limit]"
              [attr.aria-labelledby]="idPrefix() + limit"
              [attr.aria-valuetext]="text(limit)"
            >
              <div [class]="bar(limit)"></div>
            </div>
          }
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsageMeters {
  readonly usage = input.required<PlanUsage>();
  readonly limits = input.required<PlanLimits>();
  /** Keeps the label ids unique if two sets of meters share a page. */
  readonly idPrefix = input('meter-');

  protected readonly limitLabels = limitLabels;
  protected readonly limitNames = limitNames;

  protected text(limit: LimitName): string {
    return usageText(limit, this.usage()[limit], this.limits()[limit]);
  }

  protected bar(limit: LimitName): string {
    const allowed = this.limits()[limit];
    const fill = allowed ? Math.min(100, Math.round((this.usage()[limit] / allowed) * 100)) : 0;
    const colour = fill >= 100 ? 'bg-danger' : fill >= 80 ? 'bg-warning' : 'bg-accent';
    return `h-full rounded-full ${colour} ${widthClass(fill)}`;
  }
}
