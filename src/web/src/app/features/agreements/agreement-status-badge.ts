import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AgreementStatus, GenerationHold, agreementStatusLabels } from './agreements.api';

/** Active, Paused or Ended, and "limit reached" while generation is held by the plan; text, never colour alone. */
@Component({
  selector: 'app-agreement-status-badge',
  template: `<span [class]="tone()">{{ label() }}</span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgreementStatusBadge {
  readonly status = input.required<AgreementStatus>();
  readonly hold = input<GenerationHold | null>(null);

  protected readonly label = computed(() =>
    this.status() === 'active' && this.hold() === 'planLimit'
      ? 'Active, limit reached'
      : agreementStatusLabels[this.status()],
  );

  protected readonly tone = computed(() => {
    if (this.status() === 'active') {
      return this.hold() === 'planLimit'
        ? 'th-badge bg-warning/15 text-fg'
        : 'th-badge bg-success/15 text-fg';
    }
    return this.status() === 'paused'
      ? 'th-badge bg-warning/15 text-fg'
      : 'th-badge bg-surface-2 text-muted';
  });
}
