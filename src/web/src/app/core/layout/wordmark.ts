import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-wordmark',
  template: `
    <span class="flex items-center gap-2 text-base font-semibold tracking-tight">
      <svg viewBox="0 0 32 32" class="size-7" aria-hidden="true">
        <rect width="32" height="32" rx="7" class="fill-accent" />
        <path
          d="M9 10h14M16 10v13"
          class="stroke-on-accent"
          stroke-width="3.2"
          stroke-linecap="round"
        />
      </svg>
      <!-- Below 360 px the public header has no room for the name; the links around it carry it as their label. -->
      <span class="max-[359px]:sr-only">TenantHub</span>
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Wordmark {}
