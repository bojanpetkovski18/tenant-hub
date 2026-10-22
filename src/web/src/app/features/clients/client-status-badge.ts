import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { ClientStatus } from './clients.api';

/** Status as text, not colour alone (SPEC 6). */
@Component({
  selector: 'app-client-status-badge',
  template: `
    @if (status() === 'archived') {
      <span class="th-badge">Archived</span>
    } @else {
      <span class="th-badge bg-accent-soft text-fg">Active</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClientStatusBadge {
  readonly status = input.required<ClientStatus>();
}
