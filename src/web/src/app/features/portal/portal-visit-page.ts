import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { ViewPhotos } from '../../shared/ui/photo-viewer';
import { ErrorState } from '../../shared/ui/states';
import { JobStatusBadge } from '../jobs/job-badges';
import { PortalApi, PortalVisitDetail } from './portal.api';

/**
 * US-82 and US-91: one visit, as the client sees it. Once completed, it shows what was done (the checklist and photos)
 * and asks for a sign-off: "Looks good", or a problem in the client's own words, once. Another client's visit is "not
 * found", like one that does not exist.
 */
@Component({
  selector: 'app-portal-visit-page',
  imports: [RouterLink, DatePipe, ErrorState, JobStatusBadge],
  template: `
    <a
      [routerLink]="['/portal', slug(), 'visits']"
      class="text-sm font-medium text-accent hover:underline"
      >← All visits</a
    >
    <div class="mt-4">
      @if (notFound()) {
        <div class="th-card">
          <h1 class="text-xl font-semibold">Visit not found</h1>
          <p class="mt-2 text-sm text-muted">
            It may have been removed, or the link is not for your company.
          </p>
        </div>
      } @else if (error(); as problem) {
        <!-- One h1 on every state of the page, the failed one included (M22 audit). -->
        <h1 class="mb-4 text-xl font-semibold">Visit</h1>
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (visit(); as v) {
        <article class="th-card">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div>
              <p class="font-mono text-xs text-muted">{{ v.label }}</p>
              <h1 class="text-xl font-semibold">{{ v.title }}</h1>
            </div>
            <app-job-status-badge [status]="v.status" />
          </div>
          <dl class="mt-4 grid gap-3 text-sm sm:grid-cols-2">
            <div>
              <dt class="text-muted">Date</dt>
              <dd>
                {{ v.scheduledOn ? (v.scheduledOn | date: 'EEEE d MMMM y') : 'To be agreed' }}
              </dd>
            </div>
            <div>
              <dt class="text-muted">Who is coming</dt>
              <dd>{{ v.assigneeName ?? 'Not assigned yet' }}</dd>
            </div>
            @if (v.dueOn) {
              <div>
                <dt class="text-muted">Done by</dt>
                <dd>{{ v.dueOn | date: 'EEEE d MMMM y' }}</dd>
              </div>
            }
            @if (v.completedAt) {
              <div>
                <dt class="text-muted">Completed</dt>
                <dd>{{ v.completedAt | date: 'd MMMM y, HH:mm' }}</dd>
              </div>
            }
          </dl>
          @if (v.description) {
            <p class="mt-4 whitespace-pre-line text-sm">{{ v.description }}</p>
          }
        </article>

        @if (v.status === 'completed') {
          @if (v.checklist.length > 0) {
            <section class="th-card mt-4" aria-labelledby="done-heading">
              <h2 id="done-heading" class="text-lg font-semibold">What was done</h2>
              <ul class="mt-3 space-y-1.5 text-sm">
                @for (item of v.checklist; track $index) {
                  <li class="flex items-start gap-2">
                    <span aria-hidden="true" [class]="item.done ? 'text-success' : 'text-muted'">{{
                      item.done ? '✓' : '○'
                    }}</span>
                    <span>
                      {{ item.text }}
                      <span class="sr-only">{{ item.done ? '(done)' : '(not done)' }}</span>
                    </span>
                  </li>
                }
              </ul>
            </section>
          }
          @if (v.photos.length > 0) {
            <section class="th-card mt-4" aria-labelledby="photos-heading">
              <h2 id="photos-heading" class="text-lg font-semibold">Photos</h2>
              <ul class="mt-3 grid grid-cols-3 gap-2 sm:grid-cols-4">
                @for (photo of v.photos; track photo.id; let i = $index) {
                  <li>
                    <button
                      type="button"
                      class="block aspect-square w-full overflow-hidden rounded-md border border-border bg-surface-2"
                      [attr.aria-label]="'Open photo ' + (i + 1) + ' of ' + v.photos.length"
                      (click)="view(v, i)"
                    >
                      <img
                        class="size-full object-cover"
                        [src]="api.photoUrl(v.id, photo.id, true)"
                        alt=""
                        loading="lazy"
                      />
                    </button>
                  </li>
                }
              </ul>
            </section>
          }

          <section class="th-card mt-4" aria-labelledby="signoff-heading">
            <h2 id="signoff-heading" class="text-lg font-semibold">Your sign-off</h2>
            @if (message(); as text) {
              <div class="th-alert-error mt-3" role="alert">{{ text }}</div>
            }
            @if (v.feedback; as f) {
              @if (f.kind === 'confirmed') {
                <p class="mt-2 text-sm" role="status">
                  <span class="th-badge bg-success/15 text-fg">Confirmed</span>
                  {{ f.byName ?? 'You' }} confirmed this visit on {{ f.at | date: 'd MMMM y' }}.
                  Thank you.
                </p>
              } @else {
                <div class="mt-2 text-sm" role="status">
                  <p>
                    {{ f.byName ?? 'You' }} reported a problem on {{ f.at | date: 'd MMMM y' }}. The
                    team has been told.
                  </p>
                  <p class="mt-2 whitespace-pre-line rounded-md bg-surface-2 p-3">{{ f.text }}</p>
                </div>
              }
            } @else if (v.canGiveFeedback) {
              <p class="mt-2 text-sm text-muted">
                Is everything as it should be? You can answer once.
              </p>
              @if (reporting()) {
                <form class="mt-3" (submit)="$event.preventDefault(); report(v)" novalidate>
                  <label class="th-label" for="problem">What is wrong?</label>
                  <textarea
                    #problem
                    id="problem"
                    class="th-input min-h-28 py-2"
                    maxlength="2000"
                    [attr.aria-invalid]="!!problemError()"
                    [attr.aria-describedby]="
                      problemError() ? 'problem-hint problem-error' : 'problem-hint'
                    "
                    (input)="problemText.set(problem.value); problemError.set(null)"
                  ></textarea>
                  <p id="problem-hint" class="th-hint">
                    {{ problemText().trim().length }} / 2,000 characters
                  </p>
                  @if (problemError(); as error) {
                    <p id="problem-error" class="th-field-error">{{ error }}</p>
                  }
                  <div class="mt-3 flex flex-wrap gap-2">
                    <button type="submit" class="th-button" [disabled]="busy()">
                      Send the problem
                    </button>
                    <button
                      type="button"
                      class="th-button-secondary"
                      (click)="reporting.set(false)"
                    >
                      Back
                    </button>
                  </div>
                </form>
              } @else {
                <div class="mt-3 flex flex-wrap gap-2">
                  <button
                    type="button"
                    class="th-button"
                    [disabled]="busy()"
                    (click)="confirmVisit(v)"
                  >
                    Looks good
                  </button>
                  <button
                    type="button"
                    class="th-button-secondary"
                    [disabled]="busy()"
                    (click)="startReport()"
                  >
                    Report a problem
                  </button>
                </div>
              }
            }
          </section>
        }
      } @else {
        <div class="h-40 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalVisitPage {
  readonly slug = input.required<string>();
  readonly id = input.required<string>();

  protected readonly api = inject(PortalApi);
  private readonly confirm = inject(Confirm);
  private readonly viewer = inject(ViewPhotos);
  private readonly injector = inject(Injector);

  protected readonly visit = signal<PortalVisitDetail | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);
  protected readonly notFound = signal(false);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly reporting = signal(false);
  protected readonly problemText = signal('');
  protected readonly problemError = signal<string | null>(null);

  protected view(v: PortalVisitDetail, index: number): void {
    this.viewer.open({
      title: `Photos of ${v.label}`,
      index,
      photos: v.photos.map((photo, i) => ({
        url: this.api.photoUrl(v.id, photo.id),
        alt: `Photo ${i + 1} of ${v.photos.length} of ${v.label}`,
        width: photo.width,
        height: photo.height,
      })),
    });
  }

  protected async confirmVisit(v: PortalVisitDetail): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Confirm ${v.label}?`,
      message: 'You are telling the team the work was done well. You can answer once.',
      confirmLabel: 'Looks good',
    });
    if (confirmed) {
      await this.answer(() => this.api.confirmVisit(v.id));
    }
  }

  protected startReport(): void {
    this.reporting.set(true);
    afterNextRender(() => document.getElementById('problem')?.focus(), { injector: this.injector });
  }

  protected async report(v: PortalVisitDetail): Promise<void> {
    const text = this.problemText().trim();
    if (!text) {
      this.problemError.set('Describe the problem, so the team knows what to put right.');
      return;
    }
    await this.answer(() => this.api.reportProblem(v.id, text));
  }

  private async answer(send: () => Promise<PortalVisitDetail>): Promise<void> {
    this.busy.set(true);
    this.message.set(null);
    try {
      this.visit.set(await send());
      this.reporting.set(false);
    } catch (error) {
      const problem = toApiProblem(error);
      const textError = problem.fieldErrors['text']?.[0];
      if (textError) {
        this.problemError.set(textError);
      } else {
        this.message.set(problem.title);
        // Already answered by another contact, or reopened meanwhile: show where it stands.
        void this.load();
      }
    } finally {
      this.busy.set(false);
    }
  }

  constructor() {
    effect(() => {
      this.id();
      void this.load();
    });
  }

  async load(): Promise<void> {
    this.error.set(null);
    this.notFound.set(false);
    this.visit.set(null);
    try {
      this.visit.set(await this.api.visit(this.id()));
    } catch (error) {
      const problem = toApiProblem(error);
      this.notFound.set(problem.code === 'visit-not-found');
      this.error.set(problem.code === 'visit-not-found' ? null : problem);
    }
  }
}
