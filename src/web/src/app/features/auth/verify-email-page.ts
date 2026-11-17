import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { landingPath } from '../../core/session/landing-path';
import { readLastOrganization } from '../../core/session/last-organization';
import { SessionUser } from '../../core/session/session.models';
import { SessionStore } from '../../core/session/session.store';

/** The API's code for an altered or expired verification link. */
export const verificationLinkInvalidCode = 'verification-link-invalid';

type State = 'verifying' | 'verified' | 'invalid' | 'error';

/**
 * US-06: `/verify-email#<token>`, opened from the email. It confirms at once: the token is in the fragment, which
 * neither the server nor a mail scanner that prefetches links ever receives (D028), so only a person's browser
 * can post it. The link works whoever is signed in here, or nobody.
 */
@Component({
  selector: 'app-verify-email-page',
  imports: [RouterLink],
  template: `
    <div class="mx-auto max-w-sm">
      @switch (state()) {
        @case ('verifying') {
          <h1 class="text-2xl font-semibold tracking-tight">Confirming your email address…</h1>
          <div class="mt-6 h-16 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
        }
        @case ('verified') {
          <h1 class="text-2xl font-semibold tracking-tight">Email address confirmed</h1>
          <div class="th-alert-success mt-6" role="status">
            Thank you. You can now invite people to your organizations.
          </div>
          @if (session.user(); as user) {
            <a [routerLink]="continuePath(user)" class="th-button mt-6 w-full">Continue</a>
          } @else {
            <a routerLink="/sign-in" class="th-button mt-6 w-full">Sign in</a>
          }
        }
        @case ('invalid') {
          <h1 class="text-2xl font-semibold tracking-tight">This link no longer works</h1>
          <p class="mt-3 text-muted">
            {{
              token
                ? 'It has expired or is not valid. Links work for 7 days, and changing your password voids them.'
                : 'It is incomplete. Copy the whole link from the email.'
            }}
            @if (session.user()) {
              You can ask for a new one from the banner at the top of your organization.
            } @else {
              Sign in to ask for a new one.
            }
          </p>
          @if (!session.user()) {
            <a routerLink="/sign-in" class="th-button mt-6 w-full">Sign in</a>
          }
        }
        @case ('error') {
          <h1 class="text-2xl font-semibold tracking-tight">Confirm your email address</h1>
          <div
            class="th-alert-error mt-6 flex flex-wrap items-center justify-between gap-3"
            role="alert"
          >
            <span>{{ problem()?.title }}</span>
            <button type="button" class="th-button-secondary" (click)="verify()">Try again</button>
          </div>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VerifyEmailPage implements OnInit {
  protected readonly session = inject(SessionStore);
  private readonly route = inject(ActivatedRoute);

  protected readonly state = signal<State>('verifying');
  protected readonly problem = signal<ApiProblem | null>(null);
  protected token = '';

  ngOnInit(): void {
    this.token = this.route.snapshot.fragment ?? '';
    if (this.token) {
      void this.verify();
    } else {
      this.state.set('invalid');
    }
  }

  protected continuePath(user: SessionUser): string {
    return landingPath(user, null, readLastOrganization());
  }

  protected async verify(): Promise<void> {
    this.state.set('verifying');
    try {
      await this.session.verifyEmail(this.token);
      this.state.set('verified');
    } catch (error) {
      const problem = toApiProblem(error);
      this.problem.set(problem);
      this.state.set(problem.code === verificationLinkInvalidCode ? 'invalid' : 'error');
    }
  }
}
