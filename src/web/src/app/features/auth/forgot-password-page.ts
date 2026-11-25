import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';

/**
 * US-05: asks for a reset link. The answer never says whether the address has an account (SPEC-V2 4.1), so the
 * page says the same thing either way. The public demo sends no email, so there the page explains that instead.
 */
@Component({
  selector: 'app-forgot-password-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="mx-auto max-w-sm">
      <h1 class="text-2xl font-semibold tracking-tight">Reset your password</h1>

      @if (session.demoMode()) {
        <p class="mt-3 text-muted">
          This demo sends no email, so passwords cannot be reset here. The demo accounts on the
          <a
            routerLink="/sign-in"
            class="font-medium text-accent underline-offset-2 hover:underline"
            >sign-in page</a
          >
          need no password.
        </p>
      } @else if (sentTo(); as email) {
        <div
          id="reset-sent"
          tabindex="-1"
          class="th-alert-success mt-6 focus:outline-none"
          role="status"
        >
          If an account exists for <strong class="break-all">{{ email }}</strong
          >, we have sent it a link to choose a new password. The link expires in 30 minutes.
        </div>
        <p class="mt-4 text-sm text-muted">
          No email after a few minutes? Check your spam folder, or
          <button
            type="button"
            class="font-medium text-accent underline-offset-2 hover:underline"
            (click)="startOver()"
          >
            try another address</button
          >.
        </p>
      } @else {
        <p class="mt-2 text-sm text-muted">
          Enter the email address of your account, and we will send you a link to choose a new
          password.
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
          <button type="submit" class="th-button w-full" [disabled]="pending()">
            {{ pending() ? 'Sending…' : 'Send me a link' }}
          </button>
        </form>
      }

      <p class="mt-6 text-sm">
        <a routerLink="/sign-in" class="font-medium text-accent underline-offset-2 hover:underline"
          >Back to sign in</a
        >
      </p>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForgotPasswordPage {
  protected readonly session = inject(SessionStore);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email]],
  });
  protected readonly pending = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly sentTo = signal<string | null>(null);

  protected emailError(): string | null {
    return controlError(this.form.controls.email, {
      required: 'Enter your email address.',
      email: 'Enter a valid email address.',
    });
  }

  protected startOver(): void {
    this.sentTo.set(null);
    this.form.reset();
    afterNextRender(() => document.getElementById('email')?.focus(), { injector: this.injector });
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
      const email = this.form.getRawValue().email.trim();
      await this.session.requestPasswordReset(email);
      this.sentTo.set(email);
      // The form, and the button that had focus, are gone: focus goes to the answer (M22 audit).
      afterNextRender(() => document.getElementById('reset-sent')?.focus(), {
        injector: this.injector,
      });
    } catch (error) {
      this.formError.set(applyProblem(this.form, toApiProblem(error)));
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
