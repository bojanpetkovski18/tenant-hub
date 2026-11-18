import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnInit,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';

/** The API's code for an unknown, expired or used reset link. */
export const resetLinkInvalidCode = 'reset-link-invalid';

/**
 * US-05: `/reset-password#<token>`. The token is in the fragment, which browsers never send to a server (D028).
 * A new password ends every session of the account, this browser's too, so the page ends by offering to sign in.
 */
@Component({
  selector: 'app-reset-password-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="mx-auto max-w-sm">
      @if (done()) {
        <h1 class="text-2xl font-semibold tracking-tight">Password changed</h1>
        <div class="th-alert-success mt-6" role="status">
          Your new password is set, and every device that was signed in has been signed out.
        </div>
        <a routerLink="/sign-in" class="th-button mt-6 w-full">Sign in</a>
      } @else if (linkInvalid()) {
        <h1 class="text-2xl font-semibold tracking-tight">This link no longer works</h1>
        <p class="mt-3 text-muted">
          {{
            token
              ? 'It has expired or has already been used. Reset links work once, for 30 minutes.'
              : 'It is incomplete. Copy the whole link from the email, or ask for a new one.'
          }}
        </p>
        <a routerLink="/forgot-password" class="th-button mt-6 w-full">Ask for a new link</a>
      } @else {
        <h1 class="text-2xl font-semibold tracking-tight">Choose a new password</h1>
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
            <label class="th-label" for="password">New password</label>
            <div class="flex gap-2">
              <input
                id="password"
                class="th-input"
                [type]="showPassword() ? 'text' : 'password'"
                autocomplete="new-password"
                formControlName="password"
                [attr.aria-invalid]="!!passwordError()"
                [attr.aria-describedby]="
                  passwordError() ? 'password-hint password-error' : 'password-hint'
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
            @if (passwordError(); as message) {
              <p id="password-error" class="th-field-error">{{ message }}</p>
            }
          </div>
          <button type="submit" class="th-button w-full" [disabled]="pending()">
            {{ pending() ? 'Saving…' : 'Set new password' }}
          </button>
        </form>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetPasswordPage implements OnInit {
  private readonly session = inject(SessionStore);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');

  protected readonly form = inject(NonNullableFormBuilder).group({
    password: ['', [Validators.required, Validators.minLength(10)]],
  });
  protected readonly pending = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly showPassword = signal(false);
  protected readonly done = signal(false);
  protected readonly linkInvalid = signal(false);
  protected token = '';

  ngOnInit(): void {
    this.token = this.route.snapshot.fragment ?? '';
    this.linkInvalid.set(!this.token);
  }

  protected passwordError(): string | null {
    return controlError(this.form.controls.password, {
      required: 'Choose a new password.',
      minlength: 'Use at least 10 characters.',
    });
  }

  protected async submit(): Promise<void> {
    this.formError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusInvalid();
      return;
    }

    this.pending.set(true);
    try {
      await this.session.resetPassword(this.token, this.form.getRawValue().password);
      this.done.set(true);
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === resetLinkInvalidCode) {
        this.linkInvalid.set(true);
        return;
      }
      this.formError.set(applyProblem(this.form, problem));
      this.focusInvalid();
    } finally {
      this.pending.set(false);
    }
  }

  private focusInvalid(): void {
    const form = this.formEl();
    if (form) {
      focusFirstInvalid(form, this.injector);
    }
  }
}
