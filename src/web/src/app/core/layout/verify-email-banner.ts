import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toApiProblem } from '../http/api-problem';
import { SessionStore } from '../session/session.store';

/**
 * SPEC-V2 4.1: while the signed-in account has not confirmed its email address (only asked when email is on, so
 * never in the demo), a strip at the top of the organization says so and can send the link again.
 */
@Component({
  selector: 'app-verify-email-banner',
  template: `
    @if (user(); as user) {
      <section
        class="border-b border-border bg-surface-2 px-4 py-3 text-sm sm:px-6"
        aria-labelledby="verify-email-heading"
      >
        <h2 id="verify-email-heading" class="font-semibold">Confirm your email address</h2>
        <p class="mt-1 text-muted">
          We sent a link to <strong class="break-all text-fg">{{ user.email }}</strong
          >. Until you follow it, you cannot invite people.
          <button
            type="button"
            class="font-medium text-accent underline-offset-2 hover:underline disabled:opacity-60"
            [disabled]="sending()"
            (click)="resend()"
          >
            {{ sending() ? 'Sending…' : 'Send it again' }}
          </button>
        </p>
        <p class="mt-1" role="status">{{ status() }}</p>
      </section>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VerifyEmailBanner {
  private readonly session = inject(SessionStore);

  /** The user while they still need to verify; null otherwise, which hides the banner. */
  protected readonly user = computed(() => {
    const user = this.session.user();
    return user && !user.emailVerified && !this.session.demoMode() ? user : null;
  });
  protected readonly sending = signal(false);
  protected readonly status = signal('');

  protected async resend(): Promise<void> {
    this.sending.set(true);
    this.status.set('');
    try {
      await this.session.resendVerification();
      this.status.set('Sent. Check your inbox, and your spam folder.');
    } catch (error) {
      const problem = toApiProblem(error);
      this.status.set(
        problem.status === 429
          ? 'You asked for several emails just now. Wait a few minutes, then try again.'
          : problem.title,
      );
    } finally {
      this.sending.set(false);
    }
  }
}
