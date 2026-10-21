import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Stand-in for pages that later milestones build; inputs come from the route's data. */
@Component({
  selector: 'app-placeholder-page',
  template: `
    <h1 class="text-2xl font-semibold tracking-tight">{{ heading() }}</h1>
    <p class="mt-2 text-sm text-muted">{{ description() }}</p>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlaceholderPage {
  readonly heading = input.required<string>();
  readonly description = input('');
}
