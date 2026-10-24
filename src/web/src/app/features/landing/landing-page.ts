import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DemoStore, resetIntervalText } from '../../core/demo/demo.store';
import { SessionStore } from '../../core/session/session.store';

interface Feature {
  readonly title: string;
  readonly text: string;
}

const features: readonly Feature[] = [
  {
    title: 'Clients and jobs',
    text: 'Keep every client in one place and move each job from new to scheduled, in progress and done.',
  },
  {
    title: 'Roles that mean something',
    text: 'Owners, Admins, Members and Viewers each see and do exactly what their role allows.',
  },
  {
    title: 'Your team, invited',
    text: 'Invite people by email; they join with the role you choose and can belong to several businesses.',
  },
  {
    title: 'A history you can trust',
    text: 'Every change is recorded with who made it and when, and the log cannot be edited.',
  },
  {
    title: 'Your data stays yours',
    text: 'Each organization is kept apart from every other, and the tests prove it on every change.',
  },
  {
    title: 'Plans that grow with you',
    text: 'Start free; move up when you need more people, clients or open jobs.',
  },
];

/** The public landing page (SPEC 6): a short pitch, "Try the demo" in demo mode, and the way in. */
@Component({
  selector: 'app-landing-page',
  imports: [RouterLink],
  template: `
    <div class="space-y-14">
      <section class="max-w-3xl pt-4 sm:pt-10">
        <h1 class="text-4xl font-semibold tracking-tight text-balance sm:text-5xl">
          The back office for small service businesses
        </h1>
        <p class="mt-4 text-lg text-muted">
          TenantHub keeps clients, jobs and your team in one place, for cleaning companies,
          landscapers and every business that does the work at the customer's door.
        </p>
        <div class="mt-8 flex flex-wrap gap-3">
          @if (session.isSignedIn()) {
            <a routerLink="/orgs" class="th-button">Go to your organizations</a>
          } @else {
            @if (demo.info(); as info) {
              <a routerLink="/sign-in" class="th-button">Try the demo</a>
              <a routerLink="/sign-up" class="th-button-secondary">Create your organization</a>
            } @else {
              <a routerLink="/sign-up" class="th-button">Create your organization</a>
              <a routerLink="/sign-in" class="th-button-secondary">Sign in</a>
            }
          }
        </div>
        @if (demo.info(); as info) {
          <p class="mt-3 text-sm text-muted">
            The demo opens a fictional cleaning company as Owner, Admin, Member or Viewer. No
            password needed; the data resets {{ intervalText(info.resetIntervalHours) }}.
          </p>
        }
      </section>

      <section aria-labelledby="features-heading">
        <h2 id="features-heading" class="text-xl font-semibold">What it does</h2>
        <ul class="mt-4 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          @for (feature of features; track feature.title) {
            <li class="th-card">
              <h3 class="font-semibold">{{ feature.title }}</h3>
              <p class="mt-1 text-sm text-muted">{{ feature.text }}</p>
            </li>
          }
        </ul>
      </section>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingPage {
  protected readonly session = inject(SessionStore);
  protected readonly demo = inject(DemoStore);
  protected readonly features = features;
  protected readonly intervalText = resetIntervalText;

  constructor() {
    void this.demo.load();
  }
}
