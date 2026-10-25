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
import {
  DemoAccount,
  DemoStore,
  demoRoleHints,
  resetIntervalText,
} from '../../core/demo/demo.store';
import { landingPath } from '../../core/session/landing-path';
import { readLastOrganization } from '../../core/session/last-organization';
import { Role, roleLabels } from '../../core/session/session.models';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { controlError } from '../../shared/forms/server-errors';

@Component({
  selector: 'app-sign-in-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="mx-auto max-w-sm">
      <h1 class="text-2xl font-semibold tracking-tight">Sign in</h1>
      <p class="mt-2 text-sm text-muted">
        New here?
        <a routerLink="/sign-up" class="font-medium text-accent underline-offset-2 hover:underline"
          >Create an account and an organization</a
        >.
      </p>

      @if (demo.info(); as info) {
        <section class="th-card mt-6" aria-labelledby="demo-heading">
          <h2 id="demo-heading" class="font-semibold">Try the demo</h2>
          <p class="mt-1 text-sm text-muted">
            One click, no password: you join Brightline Cleaning Co., a fictional company. Data
            resets {{ intervalText(info.resetIntervalHours) }}.
          </p>
          @if (demoError(); as message) {
            <div class="th-alert-error mt-3" role="alert">{{ message }}</div>
          }
          <ul class="mt-4 space-y-3">
            @for (account of info.accounts; track account.role) {
              <li>
                <button
                  type="button"
                  class="th-button-secondary w-full"
                  [disabled]="demoPending() !== null"
                  [attr.aria-describedby]="'demo-hint-' + account.role"
                  (click)="tryAs(account)"
                >
                  {{
                    demoPending() === account.role
                      ? 'Signing in…'
                      : 'Try as ' + roleLabels[account.role]
                  }}
                </button>
                <p [id]="'demo-hint-' + account.role" class="mt-1 text-xs text-muted">
                  {{ account.displayName }}. {{ hints[account.role] }}
                </p>
              </li>
            }
          </ul>
        </section>
        <p class="mt-6 text-sm text-muted">Or sign in with an account:</p>
      }

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

        <div>
          <label class="th-label" for="email">Email</label>
          <input
            id="email"
            class="th-input"
            type="email"
            autocomplete="email"
            formControlName="email"
            [attr.aria-invalid]="!!emailError()"
            [attr.aria-describedby]="emailError() ? 'email-error' : null"
          />
          @if (emailError(); as message) {
            <p id="email-error" class="th-field-error">{{ message }}</p>
          }
        </div>

        <div>
          <label class="th-label" for="password">Password</label>
          <div class="flex gap-2">
            <input
              id="password"
              class="th-input"
              [type]="showPassword() ? 'text' : 'password'"
              autocomplete="current-password"
              formControlName="password"
              [attr.aria-invalid]="!!passwordError()"
              [attr.aria-describedby]="passwordError() ? 'password-error' : null"
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
          @if (passwordError(); as message) {
            <p id="password-error" class="th-field-error">{{ message }}</p>
          }
        </div>

        <button type="submit" class="th-button w-full" [disabled]="pending()">
          {{ pending() ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInPage {
  /** Query parameter: where to go after signing in. */
  readonly returnUrl = input<string | null>(null);

  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild.required<ElementRef<HTMLElement>>('formEl');

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
  protected readonly pending = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly showPassword = signal(false);

  protected readonly demo = inject(DemoStore);
  protected readonly demoPending = signal<Role | null>(null);
  protected readonly demoError = signal<string | null>(null);
  protected readonly hints = demoRoleHints;
  protected readonly roleLabels = roleLabels;
  protected readonly intervalText = resetIntervalText;

  constructor() {
    void this.demo.load();
  }

  /** Demo mode (US-61): signs in as the seeded account for this role. */
  protected async tryAs(account: DemoAccount): Promise<void> {
    this.demoError.set(null);
    this.demoPending.set(account.role);
    try {
      const user = await this.session.signInAsDemo(account.role);
      await this.router.navigateByUrl(landingPath(user, this.returnUrl(), readLastOrganization()));
    } catch (error) {
      this.demoError.set(toApiProblem(error).title);
    } finally {
      this.demoPending.set(null);
    }
  }

  protected emailError(): string | null {
    return controlError(this.form.controls.email, {
      required: 'Enter your email address.',
      email: 'Enter a valid email address.',
    });
  }

  protected passwordError(): string | null {
    return controlError(this.form.controls.password, { required: 'Enter your password.' });
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
      const { email, password } = this.form.getRawValue();
      const user = await this.session.signIn(email, password);
      await this.router.navigateByUrl(landingPath(user, this.returnUrl(), readLastOrganization()));
    } catch (error) {
      const problem = toApiProblem(error);
      // The API answers wrong password, unknown email and lockout the same way, on purpose (no enumeration).
      this.formError.set(problem.status === 401 ? 'Invalid email or password.' : problem.title);
    } finally {
      this.pending.set(false);
    }
  }
}
