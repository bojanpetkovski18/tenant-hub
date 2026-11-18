import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { DemoStore } from '../../core/demo/demo.store';
import { toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { controlError } from '../../shared/forms/server-errors';
import { PortalApi } from './portal.api';
import { PortalSessionStore } from './portal-session';

type Stage = 'loading' | 'form' | 'sent' | 'confirming' | 'expired' | 'unavailable';

/**
 * US-81: signing in to a business's portal without a password. The contact gives their email; we send a link that
 * works once within 15 minutes, and say the same whether or not the address is known. The link comes back here as
 * <c>/portal/:slug/sign-in#token</c> (the token in the fragment, which browsers never send to a server, D028). In the
 * public demo, which sends no email, "Try as a client" signs in a seeded contact instead (D040).
 */
@Component({
  selector: 'app-portal-sign-in-page',
  imports: [ReactiveFormsModule],
  template: `
    <div class="mx-auto max-w-md">
      @switch (stage()) {
        @case ('loading') {
          <div class="h-48 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
        }
        @case ('confirming') {
          <p class="th-card" role="status">Signing you in…</p>
        }
        @case ('unavailable') {
          <div class="th-card">
            <h1 class="text-xl font-semibold">This portal is not available</h1>
            <p class="mt-2 text-sm text-muted">
              Check the address in the email you received, or ask the business you work with.
            </p>
          </div>
        }
        @case ('sent') {
          <div class="th-card" role="status">
            <h1 class="text-xl font-semibold">Check your email</h1>
            <p class="mt-2 text-sm text-muted">
              If {{ sentTo() }} belongs to a contact of {{ organizationName() }}, a sign-in link is
              on its way. It works once, within 15 minutes.
            </p>
            <button type="button" class="th-button-secondary mt-4" (click)="stage.set('form')">
              Use another address
            </button>
          </div>
        }
        @default {
          <div class="th-card">
            <h1 class="text-xl font-semibold">Sign in to {{ organizationName() }}</h1>
            @if (stage() === 'expired') {
              <div class="th-alert-error mt-3" role="alert">
                This sign-in link has expired or has already been used. Ask for a new one below.
              </div>
            } @else if (noticeText(); as text) {
              <div class="th-alert-info mt-3" role="status">{{ text }}</div>
            }
            <p class="mt-2 text-sm text-muted">
              There is no password: enter the email address the business has for you, and we will
              send you a link that signs you in.
            </p>
            <form class="mt-4 space-y-4" [formGroup]="form" (ngSubmit)="request()" novalidate>
              @if (error(); as message) {
                <div class="th-alert-error" role="alert">{{ message }}</div>
              }
              <div>
                <label class="th-label" for="portal-email">Email</label>
                <input
                  id="portal-email"
                  class="th-input"
                  type="email"
                  autocomplete="email"
                  formControlName="email"
                  [attr.aria-invalid]="!!emailError()"
                  [attr.aria-describedby]="emailError() ? 'portal-email-error' : null"
                />
                @if (emailError(); as message) {
                  <p id="portal-email-error" class="th-field-error">{{ message }}</p>
                }
              </div>
              <button type="submit" class="th-button w-full" [disabled]="busy()">
                {{ busy() ? 'Sending…' : 'Email me a sign-in link' }}
              </button>
            </form>
          </div>

          @if (demoContacts().length > 0) {
            <section class="th-card mt-5" aria-labelledby="demo-clients-heading">
              <h2 id="demo-clients-heading" class="font-semibold">Try as a client</h2>
              <p class="mt-1 text-sm text-muted">
                The demo sends no email, so you can sign in as one of these client contacts
                directly.
              </p>
              <ul class="mt-3 space-y-2">
                @for (contact of demoContacts(); track contact.email) {
                  <li>
                    <button
                      type="button"
                      class="th-button-secondary w-full justify-between text-left"
                      [disabled]="busy()"
                      (click)="tryAs(contact.email)"
                    >
                      <span>{{ contact.name }}</span>
                      <span class="text-xs text-muted">{{ contact.clientName }}</span>
                    </button>
                  </li>
                }
              </ul>
            </section>
          }
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalSignInPage {
  readonly slug = input.required<string>();
  /** Why the contact is here again: <c>ended</c> (the session was refused) or <c>signed-out</c>. */
  readonly notice = input<string>();

  private readonly api = inject(PortalApi);
  private readonly store = inject(PortalSessionStore);
  private readonly staff = inject(SessionStore);
  private readonly demo = inject(DemoStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fragment = toSignal(this.route.fragment);

  protected readonly stage = signal<Stage>('loading');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly sentTo = signal('');
  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(254)]],
  });

  protected readonly organizationName = computed(
    () => this.store.session()?.organization?.name ?? 'the portal',
  );
  protected readonly noticeText = computed(() =>
    this.notice() === 'ended'
      ? 'Your portal session has ended. Sign in again to continue.'
      : this.notice() === 'signed-out'
        ? 'You are signed out.'
        : null,
  );
  protected readonly demoContacts = computed(() => {
    const info = this.demo.info();
    return info?.portalSlug === this.slug() ? (info.portalContacts ?? []) : [];
  });

  constructor() {
    // The fragment too: following a link over this page, already open, changes only the fragment (no new page).
    effect(() => void this.start(this.slug(), this.fragment() ?? ''));
  }

  protected emailError(): string | null {
    return controlError(this.form.controls.email, {
      required: 'Enter your email address.',
      email: 'Enter a valid email address.',
      maxlength: 'Enter a valid email address.',
    });
  }

  protected async request(): Promise<void> {
    this.error.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      document.getElementById('portal-email')?.focus();
      return;
    }
    this.busy.set(true);
    const email = this.form.getRawValue().email.trim();
    try {
      await this.api.requestLink(this.slug(), email);
      this.sentTo.set(email);
      this.stage.set('sent');
    } catch (error) {
      const problem = toApiProblem(error);
      this.error.set(
        problem.status === 429
          ? 'Too many requests from here. Wait a few minutes and try again.'
          : problem.title,
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected async tryAs(email: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.store.signedIn(this.slug(), await this.api.demoSignIn(email));
      await this.router.navigate(['/portal', this.slug(), 'visits']);
    } catch (error) {
      this.error.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
    }
  }

  /** A link with a token signs in at once; otherwise the form (or straight on, when already signed in). */
  private async start(slug: string, token: string): Promise<void> {
    if (token) {
      this.stage.set('confirming');
      // The token leaves the address bar (and the history) before anything else happens.
      history.replaceState(history.state, '', location.pathname + location.search);
      try {
        this.store.signedIn(slug, await this.api.confirm(slug, token));
        await this.router.navigate(['/portal', slug, 'visits'], { replaceUrl: true });
      } catch {
        await this.store.refresh(slug);
        this.stage.set(this.store.session()?.organization ? 'expired' : 'unavailable');
      }
      return;
    }

    await this.store.refresh(slug);
    const session = this.store.session();
    if (!session?.organization) {
      this.stage.set('unavailable');
    } else if (session.contact) {
      await this.router.navigate(['/portal', slug, 'visits'], { replaceUrl: true });
    } else {
      this.stage.set('form');
      void this.demo.load();
    }
  }
}
