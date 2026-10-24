import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DemoStore, resetIntervalText } from './demo.store';

/** "Demo: data resets every 6 hours" across the top of every page in demo mode (SPEC 4.1); nothing otherwise. */
@Component({
  selector: 'app-demo-banner',
  imports: [DatePipe],
  template: `
    @if (demo.info(); as info) {
      <p class="bg-accent-soft px-4 py-2 text-center text-sm text-fg">
        <span class="font-semibold">Demo:</span> data resets
        {{ intervalText(info.resetIntervalHours) }}.
        @if (info.nextResetAt) {
          Next reset at {{ info.nextResetAt | date: 'shortTime' }}.
        }
        All people and businesses here are fictional.
      </p>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DemoBanner {
  protected readonly demo = inject(DemoStore);
  protected readonly intervalText = resetIntervalText;

  constructor() {
    void this.demo.load();
  }
}
