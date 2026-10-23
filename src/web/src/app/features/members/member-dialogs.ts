import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ComponentType } from '@angular/cdk/portal';
import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injectable,
  Injector,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { Role, roleLabels } from '../../core/session/session.models';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { CspSafeDialogContainer } from '../../shared/ui/dialog-container';
import { LimitReached } from '../../shared/ui/limit-reached';
import { InvitationSent, MembersApi } from './members.api';

export interface InviteDialogData {
  readonly organizationId: string;
  /** For the plan link when the seat limit is reached. */
  readonly slug: string;
  /** From the session (the API's rules): Admins never see Owner, nobody is invited as Owner. */
  readonly roles: readonly Role[];
}

const messages = {
  email: { required: 'Enter an email address.', email: 'Enter a valid email address.' },
  role: { required: 'Choose a role.' },
} as const;

/** US-10: invite by email with a role. Closes with the result; the page shows the link when it was not emailed. */
@Component({
  selector: 'app-invite-dialog',
  imports: [ReactiveFormsModule, LimitReached],
  host: {
    class:
      'block w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-surface p-6 shadow-xl',
  },
  template: `
    <h2 id="invite-title" class="text-lg font-semibold">Invite a teammate</h2>
    <p class="mt-1 text-sm text-muted">They get a link that works once, for 7 days.</p>
    <form #formEl class="mt-5 space-y-5" [formGroup]="form" (ngSubmit)="submit()" novalidate>
      @if (limit(); as problem) {
        <app-limit-reached [problem]="problem" [slug]="data.slug" />
      } @else if (formError(); as message) {
        <div class="th-alert-error" role="alert">{{ message }}</div>
      }
      <div>
        <label class="th-label" for="invite-email">Email</label>
        <input
          id="invite-email"
          class="th-input"
          type="email"
          autocomplete="off"
          formControlName="email"
          [attr.aria-invalid]="!!error('email')"
          [attr.aria-describedby]="error('email') ? 'invite-email-error' : null"
        />
        @if (error('email'); as message) {
          <p id="invite-email-error" class="th-field-error">{{ message }}</p>
        }
      </div>
      <div>
        <label class="th-label" for="invite-role">Role</label>
        <select
          id="invite-role"
          class="th-input"
          formControlName="role"
          [attr.aria-invalid]="!!error('role')"
          [attr.aria-describedby]="
            error('role') ? 'invite-role-hint invite-role-error' : 'invite-role-hint'
          "
        >
          @for (role of data.roles; track role) {
            <option [value]="role">{{ roleLabels[role] }}</option>
          }
        </select>
        <p id="invite-role-hint" class="th-hint">
          To make someone an Owner, invite them first, then change their role.
        </p>
        @if (error('role'); as message) {
          <p id="invite-role-error" class="th-field-error">{{ message }}</p>
        }
      </div>
      <div class="flex flex-wrap justify-end gap-2">
        <button type="button" class="th-button-secondary" (click)="ref.close(null)">Cancel</button>
        <button type="submit" class="th-button" [disabled]="pending()">
          {{ pending() ? 'Sending…' : 'Send invitation' }}
        </button>
      </div>
    </form>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InviteDialog {
  protected readonly data = inject<InviteDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<InvitationSent | null>>(DialogRef);
  protected readonly roleLabels = roleLabels;
  private readonly api = inject(MembersApi);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild.required<ElementRef<HTMLElement>>('formEl');

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email]],
    role: [
      this.data.roles.includes('member') ? 'member' : (this.data.roles[0] ?? ''),
      Validators.required,
    ],
  });
  protected readonly pending = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly limit = signal<ApiProblem | null>(null);

  protected error(field: keyof typeof messages): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  protected async submit(): Promise<void> {
    this.formError.set(null);
    this.limit.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      focusFirstInvalid(this.formEl(), this.injector);
      return;
    }

    this.pending.set(true);
    try {
      const { email, role } = this.form.getRawValue();
      this.ref.close(await this.api.invite(this.data.organizationId, email.trim(), role as Role));
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'plan-limit-reached') {
        this.limit.set(problem);
        return;
      }
      this.formError.set(
        applyProblem(this.form, problem, {
          'already-member': 'email',
          'owner-not-invitable': 'role',
          'role-not-grantable': 'role',
        }),
      );
      focusFirstInvalid(this.formEl(), this.injector);
    } finally {
      this.pending.set(false);
    }
  }
}

export interface CopyLinkData {
  readonly sent: InvitationSent;
}

/** The link to pass on, when email is off (public demo) or the mail server failed (D011). */
@Component({
  selector: 'app-copy-link-dialog',
  imports: [DatePipe],
  host: {
    class:
      'block w-[min(32rem,calc(100vw-2rem))] rounded-lg border border-border bg-surface p-6 shadow-xl',
  },
  template: `
    <h2 id="copy-link-title" class="text-lg font-semibold">Share the invitation link</h2>
    <p id="copy-link-message" class="mt-2 text-sm text-muted">
      @if (data.sent.delivery === 'failed') {
        The email could not be sent.
      } @else {
        Email is turned off here.
      }
      Send this link to {{ data.sent.invitation.email }} yourself. It works once and expires on
      {{ data.sent.invitation.expiresAt | date: 'mediumDate' }}.
    </p>
    <label class="th-label mt-4" for="invite-link">Invitation link</label>
    <div class="flex gap-2">
      <input
        #linkInput
        id="invite-link"
        class="th-input font-mono text-xs"
        readonly
        [value]="data.sent.link"
        (focus)="linkInput.select()"
      />
      <button type="button" class="th-button shrink-0" (click)="copy(linkInput)">Copy</button>
    </div>
    <p class="th-hint" role="status">{{ copyStatus() }}</p>
    <div class="mt-5 flex justify-end">
      <button type="button" class="th-button-secondary" (click)="ref.close()">Done</button>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CopyLinkDialog {
  protected readonly data = inject<CopyLinkData>(DIALOG_DATA);
  protected readonly ref = inject(DialogRef);
  protected readonly copyStatus = signal('');

  protected async copy(input: HTMLInputElement): Promise<void> {
    try {
      await navigator.clipboard.writeText(input.value);
      this.copyStatus.set('Link copied.');
    } catch {
      // Clipboard access can be refused (permissions, insecure context): select it for a manual copy.
      input.focus();
      input.select();
      this.copyStatus.set('Press Ctrl+C (or ⌘C) to copy the selected link.');
    }
  }
}

/** Opens the member dialogs with the CSP-safe container (D027), like Confirm. */
@Injectable({ providedIn: 'root' })
export class MemberDialogs {
  private readonly dialog = inject(Dialog);

  invite(data: InviteDialogData): Promise<InvitationSent | null> {
    return this.open<InviteDialog, InviteDialogData, InvitationSent | null>(
      InviteDialog,
      data,
      'invite-title',
    );
  }

  async copyLink(sent: InvitationSent): Promise<void> {
    await this.open<CopyLinkDialog, CopyLinkData, void>(
      CopyLinkDialog,
      { sent },
      'copy-link-title',
    );
  }

  private async open<C, D, R>(
    component: ComponentType<C>,
    data: D,
    labelledBy: string,
  ): Promise<R | null> {
    const ref = this.dialog.open<R, D, C>(component, {
      data,
      backdropClass: 'th-dialog-backdrop',
      container: CspSafeDialogContainer,
      role: 'dialog',
      ariaModal: true,
      ariaLabelledBy: labelledBy,
      autoFocus: 'first-tabbable',
    });
    return (await firstValueFrom(ref.closed)) ?? null;
  }
}
