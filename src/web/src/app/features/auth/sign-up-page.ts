import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { toApiProblem } from '../../core/http/api-problem';
import { organizationHome } from '../../core/session/landing-path';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { browserTimeZone, timeZoneOptions } from '../../shared/forms/time-zones';

const messages = {
  displayName: { required: 'Enter your name.', maxlength: 'Use at most 80 characters.' },
  email: { required: 'Enter your email address.', email: 'Enter a valid email address.' },
  password: { required: 'Choose a password.', minlength: 'Use at least 10 characters.' },
  organizationName: {
    required: 'Enter your organization’s name.',
    minlength: 'Use at least 2 characters.',
    maxlength: 'Use at most 80 characters.',
  },
  timeZone: { required: 'Choose a time zone.' },
} as const;

type Field = keyof typeof messages;

/** US-01: an account, an organization on the Free plan and the Owner membership, in one step. */
@Component({
  selector: 'app-sign-up-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="mx-auto max-w-md">
      <h1 class="text-2xl font-semibold tracking-tight">Create your organization</h1>
      <p class="mt-2 text-sm text-muted">
        Already have an account?
        <a routerLink="/sign-in" class="font-medium text-accent underline-offset-2 hover:underline"
          >Sign in</a
        >.
      </p>

      <form
        #formEl
        class="th-card mt-6 space-y-5"
        [formGroup]="form"
        (ngSubmit)="submit()"
        novalidate
      >
        @if (formError(); as message) {
          <div class="th-alert-error" role="alert">{{ message }}</div>
        }

        <fieldset class="space-y-5">
          <legend class="text-sm font-semibold">You</legend>
          <div>
            <label class="th-label" for="displayName">Your name</label>
            <input
              id="displayName"
              class="th-input"
              autocomplete="name"
              formControlName="displayName"
              [attr.aria-invalid]="!!error('displayName')"
              [attr.aria-describedby]="error('displayName') ? 'displayName-error' : null"
            />
            @if (error('displayName'); as message) {
              <p id="displayName-error" class="th-field-error">{{ message }}</p>
            }
          </div>

          <div>
            <label class="th-label" for="email">Work email</label>
            <input
              id="email"
              class="th-input"
              type="email"
              autocomplete="email"
              formControlName="email"
              [attr.aria-invalid]="!!error('email')"
              [attr.aria-describedby]="error('email') ? 'email-error' : null"
            />
            @if (error('email'); as message) {
              <p id="email-error" class="th-field-error">{{ message }}</p>
            }
          </div>

          <div>
            <label class="th-label" for="password">Password</label>
            <div class="flex gap-2">
              <input
                id="password"
                class="th-input"
                autocomplete="new-password"
                formControlName="password"
                [type]="showPassword() ? 'text' : 'password'"
                [attr.aria-invalid]="!!error('password')"
                [attr.aria-describedby]="
                  error('password') ? 'password-hint password-error' : 'password-hint'
                "
              />
              <button
                type="button"
                class="th-button-secondary shrink-0"
                [attr.aria-pressed]="showPassword()"
                (click)="showPassword.set(!showPassword())"
              >
                {{ showPassword() ? 'Hide' : 'Show' }}
              </button>
            </div>
            <p id="password-hint" class="th-hint">
              At least 10 characters, with upper and lower case letters, a digit and a symbol.
            </p>
            @if (error('password'); as message) {
              <p id="password-error" class="th-field-error">{{ message }}</p>
            }
          </div>
        </fieldset>

        <fieldset class="space-y-5 border-t border-border pt-5">
          <legend class="sr-only">Your organization</legend>
          <p class="text-sm font-semibold" aria-hidden="true">Your organization</p>
          <div>
            <label class="th-label" for="organizationName">Organization name</label>
            <input
              id="organizationName"
              class="th-input"
              autocomplete="organization"
              formControlName="organizationName"
              [attr.aria-invalid]="!!error('organizationName')"
              [attr.aria-describedby]="error('organizationName') ? 'organizationName-error' : null"
            />
            @if (error('organizationName'); as message) {
              <p id="organizationName-error" class="th-field-error">{{ message }}</p>
            }
          </div>

          <div>
            <label class="th-label" for="timeZone">Time zone</label>
            <select
              id="timeZone"
              class="th-input"
              formControlName="timeZone"
              [attr.aria-invalid]="!!error('timeZone')"
              [attr.aria-describedby]="
                error('timeZone') ? 'timeZone-hint timeZone-error' : 'timeZone-hint'
              "
            >
              @for (zone of timeZones; track zone) {
                <option [value]="zone">{{ zone }}</option>
              }
            </select>
            <p id="timeZone-hint" class="th-hint">
              Used for due dates and “overdue”. You can change it later.
            </p>
            @if (error('timeZone'); as message) {
              <p id="timeZone-error" class="th-field-error">{{ message }}</p>
            }
          </div>
        </fieldset>

        <button type="submit" class="th-button w-full" [disabled]="pending()">
          {{ pending() ? 'Creating…' : 'Create account and organization' }}
        </button>
        <p class="text-xs text-muted">
          Free plan: up to 3 seats, 25 active clients and 50 open jobs. All data in this demo is
          fictional.
        </p>
      </form>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignUpPage {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild.required<ElementRef<HTMLElement>>('formEl');

  protected readonly timeZones = timeZoneOptions();
  protected readonly form = inject(NonNullableFormBuilder).group({
    displayName: ['', [Validators.required, Validators.maxLength(80)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(10)]],
    organizationName: [
      '',
      [Validators.required, Validators.minLength(2), Validators.maxLength(80)],
    ],
    timeZone: [browserTimeZone(), Validators.required],
  });
  protected readonly pending = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly showPassword = signal(false);

  protected error(field: Field): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  protected async submit(): Promise<void> {
    this.formError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      focusFirstInvalid(this.formEl(), this.injector);
      return;
    }

    this.pending.set(true);
    try {
      const user = await this.session.signUp(this.form.getRawValue());
      const organization = user.memberships[0];
      await this.router.navigateByUrl(organization ? organizationHome(organization.slug) : '/orgs');
    } catch (error) {
      this.formError.set(
        applyProblem(this.form, toApiProblem(error), {
          'organization-name-taken': 'organizationName',
        }),
      );
      focusFirstInvalid(this.formEl(), this.injector);
    } finally {
      this.pending.set(false);
    }
  }
}
