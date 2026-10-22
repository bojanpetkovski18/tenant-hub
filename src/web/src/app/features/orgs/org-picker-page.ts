import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { toApiProblem } from '../../core/http/api-problem';
import { organizationHome } from '../../core/session/landing-path';
import { roleLabels } from '../../core/session/session.models';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { browserTimeZone, timeZoneOptions } from '../../shared/forms/time-zones';

/** US-03: the user's organizations, and creating another one. */
@Component({
  selector: 'app-org-picker-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="mx-auto max-w-2xl">
      <h1 class="text-2xl font-semibold tracking-tight">Your organizations</h1>
      @if (notice() === 'left') {
        <div class="th-alert-success mt-4" role="status">You left the organization.</div>
      }
      @if (notice() === 'access-lost') {
        <div class="th-alert-error mt-4" role="status">
          You no longer have access to that organization. It may have been deleted, or an admin
          removed you.
        </div>
      }

      @if (session.memberships().length === 0) {
        <div class="th-card mt-6 text-center">
          <p class="font-medium">You are not a member of any organization yet.</p>
          <p class="mt-1 text-sm text-muted">
            Create one below, or ask an admin of an existing organization to invite you.
          </p>
        </div>
      } @else {
        <ul class="mt-6 space-y-3" aria-label="Organizations">
          @for (membership of session.memberships(); track membership.organizationId) {
            <li>
              <a
                [routerLink]="home(membership.slug)"
                class="th-card flex items-center gap-4 transition-colors hover:border-accent"
              >
                <span
                  class="flex size-10 shrink-0 items-center justify-center rounded-md bg-accent text-base font-semibold uppercase text-on-accent"
                  aria-hidden="true"
                  >{{ membership.name.charAt(0) }}</span
                >
                <span class="min-w-0 flex-1">
                  <span class="block truncate font-medium">{{ membership.name }}</span>
                  <span class="block truncate text-sm text-muted">{{ membership.slug }}</span>
                </span>
                <span class="th-badge">{{ roleLabels[membership.role] }}</span>
              </a>
            </li>
          }
        </ul>
      }

      <section class="th-card mt-8" aria-labelledby="create-heading">
        <h2 id="create-heading" class="text-lg font-semibold">Create an organization</h2>
        <p class="mt-1 text-sm text-muted">
          You become its Owner. You can own up to 5 organizations.
        </p>

        <form #formEl class="mt-5 space-y-5" [formGroup]="form" (ngSubmit)="create()" novalidate>
          @if (formError(); as message) {
            <div class="th-alert-error" role="alert">{{ message }}</div>
          }
          <div class="grid gap-5 sm:grid-cols-2">
            <div>
              <label class="th-label" for="name">Name</label>
              <input
                id="name"
                class="th-input"
                autocomplete="organization"
                formControlName="name"
                [attr.aria-invalid]="!!nameError()"
                [attr.aria-describedby]="nameError() ? 'name-error' : null"
              />
              @if (nameError(); as message) {
                <p id="name-error" class="th-field-error">{{ message }}</p>
              }
            </div>
            <div>
              <label class="th-label" for="timeZone">Time zone</label>
              <select
                id="timeZone"
                class="th-input"
                formControlName="timeZone"
                [attr.aria-invalid]="!!timeZoneError()"
                [attr.aria-describedby]="timeZoneError() ? 'timeZone-error' : null"
              >
                @for (zone of timeZones; track zone) {
                  <option [value]="zone">{{ zone }}</option>
                }
              </select>
              @if (timeZoneError(); as message) {
                <p id="timeZone-error" class="th-field-error">{{ message }}</p>
              }
            </div>
          </div>
          <button type="submit" class="th-button" [disabled]="pending()">
            {{ pending() ? 'Creating…' : 'Create organization' }}
          </button>
        </form>
      </section>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrgPickerPage {
  /** Query parameter set when the app sends the user here after losing access to an organization. */
  readonly notice = input<string | null>(null);

  protected readonly session = inject(SessionStore);
  protected readonly roleLabels = roleLabels;
  protected readonly timeZones = timeZoneOptions();
  protected readonly home = organizationHome;

  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild.required<ElementRef<HTMLElement>>('formEl');

  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(80)]],
    timeZone: [browserTimeZone(), Validators.required],
  });
  protected readonly pending = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected nameError(): string | null {
    return controlError(this.form.controls.name, {
      required: 'Enter a name.',
      minlength: 'Use at least 2 characters.',
      maxlength: 'Use at most 80 characters.',
    });
  }

  protected timeZoneError(): string | null {
    return controlError(this.form.controls.timeZone, { required: 'Choose a time zone.' });
  }

  protected async create(): Promise<void> {
    this.formError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      focusFirstInvalid(this.formEl(), this.injector);
      return;
    }

    this.pending.set(true);
    try {
      const { name, timeZone } = this.form.getRawValue();
      const membership = await this.session.createOrganization(name, timeZone);
      await this.router.navigateByUrl(organizationHome(membership.slug));
    } catch (error) {
      this.formError.set(
        applyProblem(this.form, toApiProblem(error), {
          'invalid-organization-name': 'name',
          'organization-name-taken': 'name',
          'invalid-time-zone': 'timeZone',
        }),
      );
      focusFirstInvalid(this.formEl(), this.injector);
    } finally {
      this.pending.set(false);
    }
  }
}
