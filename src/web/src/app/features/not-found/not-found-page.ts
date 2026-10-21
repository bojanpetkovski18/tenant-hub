import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-not-found-page',
  imports: [RouterLink],
  template: `
    <main
      id="main"
      tabindex="-1"
      class="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-16"
    >
      <p class="text-sm font-semibold text-accent">404</p>
      <h1 class="mt-2 text-3xl font-semibold tracking-tight">Page not found</h1>
      <p class="mt-3 text-muted">
        The page you asked for does not exist, or you do not have access to it.
      </p>
      <a
        routerLink="/"
        class="mt-8 inline-flex min-h-10 w-fit items-center rounded-md bg-accent px-4 text-sm font-semibold text-on-accent hover:bg-accent-strong"
        >Go to the start page</a
      >
    </main>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotFoundPage {}
