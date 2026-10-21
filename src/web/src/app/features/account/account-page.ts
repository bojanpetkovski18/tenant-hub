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
import { toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';

/** US-04: the signed-in user's name and password. */
@Component({
  selector: 'app-account-page',
  imports: [ReactiveFormsModule],
  template: `
    <div class="mx-auto max-w-xl space-y-8">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight">Account</h1>
        <p class="mt-1 text-sm text-muted">Signed in as {{ session.user()?.email }}</p>
      </div>

      <section class="th-card" aria-labelledby="profile-heading">
        <h2 id="profile-heading" class="text-lg font-semibold">Profile</h2>
        <form
          #profileEl
          class="mt-5 space-y-5"
          [formGroup]="profile"
          (ngSubmit)="saveProfile()"
          novalidate
        >
          @if (profileMessage(); as message) {
            <div
              [class]="message.ok ? 'th-alert-success' : 'th-alert-error'"
              [attr.role]="message.ok ? 'status' : 'alert'"
            >
              {{ message.text }}
            </div>
          }
          <div>
            <label class="th-label" for="displayName">Name</label>
            <input
              id="displayName"
              class="th-input"
              autocomplete="name"
              formControlName="displayName"
              [attr.aria-invalid]="!!displayNameError()"
              [attr.aria-describedby]="displayNameError() ? 'displayName-error' : null"
            />
            @if (displayNameError(); as message) {
              <p id="displayName-error" class="th-field-error">{{ message }}</p>
            }
          </div>
          <button type="submit" class="th-button" [disabled]="savingProfile()">
            {{ savingProfile() ? 'Saving…' : 'Save name' }}
          </button>
        </form>
      </section>

      <section class="th-card" aria-labelledby="password-heading">
        <h2 id="password-heading" class="text-lg font-semibold">Password</h2>
        <p class="mt-1 text-sm text-muted">Changing it signs you out on every other device.</p>
        <form
          #passwordEl
          class="mt-5 space-y-5"
          [formGroup]="password"
          (ngSubmit)="changePassword()"
          novalidate
        >
          @if (passwordMessage(); as message) {
            <div
              [class]="message.ok ? 'th-alert-success' : 'th-alert-error'"
              [attr.role]="message.ok ? 'status' : 'alert'"
            >
              {{ message.text }}
            </div>
          }
          <div>
            <label class="th-label" for="currentPassword">Current password</label>
            <input
              id="currentPassword"
              class="th-input"
              type="password"
              autocomplete="current-password"
              formControlName="currentPassword"
              [attr.aria-invalid]="!!currentPasswordError()"
              [attr.aria-describedby]="currentPasswordError() ? 'currentPassword-error' : null"
            />
            @if (currentPasswordError(); as message) {
              <p id="currentPassword-error" class="th-field-error">{{ message }}</p>
            }
          </div>
          <div>
            <label class="th-label" for="newPassword">New password</label>
            <input
              id="newPassword"
              class="th-input"
              type="password"
              autocomplete="new-password"
              formControlName="newPassword"
              [attr.aria-invalid]="!!newPasswordError()"
              [attr.aria-describedby]="
                newPasswordError() ? 'newPassword-hint newPassword-error' : 'newPassword-hint'
              "
            />
            <p id="newPassword-hint" class="th-hint">
              At least 10 characters, with upper and lower case letters, a digit and a symbol.
            </p>
            @if (newPasswordError(); as message) {
              <p id="newPassword-error" class="th-field-error">{{ message }}</p>
            }
          </div>
          <button type="submit" class="th-button" [disabled]="changingPassword()">
            {{ changingPassword() ? 'Changing…' : 'Change password' }}
          </button>
        </form>
      </section>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountPage {
  protected readonly session = inject(SessionStore);
  private readonly injector = inject(Injector);
  private readonly profileEl = viewChild.required<ElementRef<HTMLElement>>('profileEl');
  private readonly passwordEl = viewChild.required<ElementRef<HTMLElement>>('passwordEl');
  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly profile = this.fb.group({
    displayName: [
      this.session.user()?.displayName ?? '',
      [Validators.required, Validators.maxLength(80)],
    ],
  });
  protected readonly password = this.fb.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', [Validators.required, Validators.minLength(10)]],
  });
  protected readonly savingProfile = signal(false);
  protected readonly changingPassword = signal(false);
  protected readonly profileMessage = signal<{ ok: boolean; text: string } | null>(null);
  protected readonly passwordMessage = signal<{ ok: boolean; text: string } | null>(null);

  protected displayNameError(): string | null {
    return controlError(this.profile.controls.displayName, {
      required: 'Enter your name.',
      maxlength: 'Use at most 80 characters.',
    });
  }

  protected currentPasswordError(): string | null {
    return controlError(this.password.controls.currentPassword, {
      required: 'Enter your current password.',
    });
  }

  protected newPasswordError(): string | null {
    return controlError(this.password.controls.newPassword, {
      required: 'Choose a new password.',
      minlength: 'Use at least 10 characters.',
    });
  }

  protected async saveProfile(): Promise<void> {
    this.profileMessage.set(null);
    if (this.profile.invalid) {
      this.profile.markAllAsTouched();
      focusFirstInvalid(this.profileEl(), this.injector);
      return;
    }
    this.savingProfile.set(true);
    try {
      await this.session.updateProfile(this.profile.getRawValue().displayName);
      this.profile.markAsPristine();
      this.profileMessage.set({ ok: true, text: 'Your name was saved.' });
    } catch (error) {
      const message = applyProblem(this.profile, toApiProblem(error));
      this.profileMessage.set(message ? { ok: false, text: message } : null);
      focusFirstInvalid(this.profileEl(), this.injector);
    } finally {
      this.savingProfile.set(false);
    }
  }

  protected async changePassword(): Promise<void> {
    this.passwordMessage.set(null);
    if (this.password.invalid) {
      this.password.markAllAsTouched();
      focusFirstInvalid(this.passwordEl(), this.injector);
      return;
    }
    this.changingPassword.set(true);
    try {
      const { currentPassword, newPassword } = this.password.getRawValue();
      await this.session.changePassword(currentPassword, newPassword);
      this.password.reset();
      this.passwordMessage.set({
        ok: true,
        text: 'Your password was changed. Other devices have been signed out.',
      });
    } catch (error) {
      const message = applyProblem(this.password, toApiProblem(error));
      this.passwordMessage.set(message ? { ok: false, text: message } : null);
      focusFirstInvalid(this.passwordEl(), this.injector);
    } finally {
      this.changingPassword.set(false);
    }
  }
}
