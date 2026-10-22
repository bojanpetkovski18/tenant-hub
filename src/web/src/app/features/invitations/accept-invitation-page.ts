import { DatePipe } from '@angular/common';
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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { organizationHome } from '../../core/session/landing-path';
import { roleLabels } from '../../core/session/session.models';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { AcceptedInvitation, InvitationPreview, MembersApi } from '../members/members.api';

/** Survives the detour through sign-in in this tab; the fragment does not (returnUrl carries no token). */
const pendingTokenKey = 'th.pending-invitation';

const messages = {
  displayName: { required: 'Enter your name.', maxlength: 'Use at most 80 characters.' },
  password: { required: 'Choose a password.', minlength: 'Use at least 10 characters.' },
} as const;

/**
 * US-11: `/invite#<token>`. The token is in the fragment, which browsers never send to a server (D028); the page
 * posts it to the API. One page for every state: unknown link, expired, revoked or used, already a member,
 * signed in with the invited email (join), with another email (explain, offer sign-out), no account (sign-up
 * form with the email fixed) and an existing account while signed out (sign in first).
 */
@Component({
  selector: 'app-accept-invitation-page',
  imports: [DatePipe, ReactiveFormsModule, RouterLink],
  template: `
    <div class="mx-auto max-w-md">
      @if (loading()) {
        <div class="space-y-3" aria-busy="true">
          <div class="h-8 w-64 animate-pulse rounded bg-surface-2"></div>
          <div class="h-40 animate-pulse rounded-lg bg-surface-2"></div>
        </div>
      } @else if (notFound()) {
        <h1 class="text-2xl font-semibold tracking-tight">Invitation not found</h1>
        <p class="mt-3 text-muted">
          This link is not valid. It may be incomplete, or a newer invitation replaced it. Ask the
          person who invited you to send it again.
        </p>
      } @else if (loadError(); as error) {
        <h1 class="text-2xl font-semibold tracking-tight">Accept invitation</h1>
        <div
          class="th-alert-error mt-4 flex flex-wrap items-center justify-between gap-3"
          role="alert"
        >
          <span>{{ error.title }}</span>
          <button type="button" class="th-button-secondary" (click)="load()">Try again</button>
        </div>
      } @else if (preview(); as p) {
        <h1 class="text-2xl font-semibold tracking-tight">Join {{ p.organizationName }}</h1>
        <p class="mt-2 text-muted">
          {{ p.invitedBy }} invited {{ p.maskedEmail }} to join as {{ article(p.role) }}
          {{ roleLabels[p.role] }}.
        </p>

        <div class="th-card mt-6 space-y-4">
          @if (p.memberOfOrganizationId) {
            <p>You are already a member of {{ p.organizationName }}.</p>
            <button type="button" class="th-button w-full" (click)="open(p.memberOfOrganizationId)">
              Open {{ p.organizationName }}
            </button>
          } @else if (p.state === 'expired') {
            <p class="font-medium">This invitation has expired.</p>
            <p class="text-sm text-muted">
              It was valid until {{ p.expiresAt | date: 'medium' }}. Ask {{ p.invitedBy }} to send a
              new one.
            </p>
          } @else if (p.state === 'revoked') {
            <p class="font-medium">This invitation was withdrawn.</p>
            <p class="text-sm text-muted">Ask {{ p.invitedBy }} if you should still join.</p>
          } @else if (p.state === 'accepted') {
            <p class="font-medium">This invitation has already been used.</p>
            <p class="text-sm text-muted">
              If you accepted it, <a routerLink="/sign-in" class="text-accent underline">sign in</a>
              to open the organization.
            </p>
          } @else if (p.signedInEmailMatches === true) {
            @if (actionError(); as message) {
              <div class="th-alert-error" role="alert">{{ message }}</div>
            }
            <p class="text-sm text-muted">You are signed in as {{ session.user()?.email }}.</p>
            <button
              type="button"
              class="th-button w-full"
              [disabled]="pending()"
              (click)="accept()"
            >
              {{ pending() ? 'Joining…' : 'Join ' + p.organizationName }}
            </button>
          } @else if (p.signedInEmailMatches === false) {
            <p class="font-medium">This invitation is for a different email address.</p>
            <p class="text-sm text-muted">
              It was sent to {{ p.maskedEmail }}, but you are signed in as
              {{ session.user()?.email }}. Sign out, then open the link again to sign in or create
              an account with the invited address.
            </p>
            <button
              type="button"
              class="th-button-secondary w-full"
              [disabled]="pending()"
              (click)="signOut()"
            >
              Sign out
            </button>
          } @else if (p.accountExists) {
            <p>You already have an account with this email. Sign in to accept the invitation.</p>
            <a
              routerLink="/sign-in"
              [queryParams]="{ returnUrl: '/invite' }"
              class="th-button w-full"
            >
              Sign in to accept
            </a>
          } @else {
            <form #formEl class="space-y-5" [formGroup]="form" (ngSubmit)="signUp()" novalidate>
              <p class="text-sm text-muted">
                Create your account to join. Your email is {{ p.maskedEmail }}.
              </p>
              @if (actionError(); as message) {
                <div class="th-alert-error" role="alert">{{ message }}</div>
              }
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
              <button type="submit" class="th-button w-full" [disabled]="pending()">
                {{ pending() ? 'Joining…' : 'Create account and join' }}
              </button>
            </form>
          }
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AcceptInvitationPage implements OnInit {
  private readonly api = inject(MembersApi);
  protected readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');

  protected readonly roleLabels = roleLabels;
  protected readonly preview = signal<InvitationPreview | null>(null);
  protected readonly loading = signal(true);
  protected readonly notFound = signal(false);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly pending = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly showPassword = signal(false);
  protected readonly form = inject(NonNullableFormBuilder).group({
    displayName: ['', [Validators.required, Validators.maxLength(80)]],
    password: ['', [Validators.required, Validators.minLength(10)]],
  });

  private token = '';

  ngOnInit(): void {
    const fragment = this.route.snapshot.fragment;
    if (fragment) {
      rememberToken(fragment);
    }
    this.token = fragment ?? readToken() ?? '';
    void this.load();
  }

  protected article(role: string): string {
    return role === 'owner' || role === 'admin' ? 'an' : 'a';
  }

  protected error(field: keyof typeof messages): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    this.notFound.set(false);
    if (!this.token) {
      this.notFound.set(true);
      this.loading.set(false);
      return;
    }
    try {
      this.preview.set(await this.api.preview(this.token));
    } catch (error) {
      const problem = toApiProblem(error);
      // A malformed token is a 400 from request validation; to the visitor it is the same unusable link.
      const unusable = problem.code === 'invitation-not-found' || problem.status === 400;
      this.notFound.set(unusable);
      this.loadError.set(unusable ? null : problem);
    } finally {
      this.loading.set(false);
    }
  }

  protected async accept(): Promise<void> {
    await this.join(() => this.api.accept(this.token));
  }

  protected async signUp(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusInvalid();
      return;
    }
    const { displayName, password } = this.form.getRawValue();
    await this.join(() => this.api.signUp(this.token, displayName, password));
  }

  protected async signOut(): Promise<void> {
    this.pending.set(true);
    try {
      await this.session.signOut();
    } finally {
      this.pending.set(false);
    }
    await this.load();
  }

  protected async open(organizationId: string): Promise<void> {
    forgetToken();
    const membership = this.session.memberships().find((m) => m.organizationId === organizationId);
    await this.router.navigateByUrl(membership ? organizationHome(membership.slug) : '/orgs');
  }

  private async join(action: () => Promise<AcceptedInvitation>): Promise<void> {
    this.actionError.set(null);
    this.pending.set(true);
    try {
      const joined = await action();
      this.session.apply(joined.session);
      await this.open(joined.organizationId);
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code?.startsWith('invitation-') && problem.code !== 'invitation-email-mismatch') {
        // The link changed state meanwhile (used, revoked, expired): show that state instead of the form.
        await this.load();
        return;
      }
      this.actionError.set(applyProblem(this.form, problem));
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

function rememberToken(token: string): void {
  try {
    sessionStorage.setItem(pendingTokenKey, token);
  } catch {
    // Storage can be unavailable (private mode, blocked); the link itself still works.
  }
}

function readToken(): string | null {
  try {
    return sessionStorage.getItem(pendingTokenKey);
  } catch {
    return null;
  }
}

function forgetToken(): void {
  try {
    sessionStorage.removeItem(pendingTokenKey);
  } catch {
    // Nothing to clean up when storage is unavailable.
  }
}
