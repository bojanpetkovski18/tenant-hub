import { DatePipe } from '@angular/common';
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
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { ErrorState } from '../../shared/ui/states';
import { ClientContact, ClientsApi } from './clients.api';

const messages = {
  name: { required: 'Enter a name.', maxlength: 'Use at most 100 characters.' },
  email: { required: 'Enter an email address.', email: 'Enter a valid email address.' },
} as const;

/**
 * US-80: the client's portal contacts on the client page. Everyone who reads clients sees them; Owners and Admins
 * (<c>portal.manage</c>) add and remove them. On a plan without the portal the API says so (403
 * <c>plan-feature-unavailable</c>), and the section says which plan has it, with a link for whoever can change the plan.
 */
@Component({
  selector: 'app-client-contacts',
  imports: [ReactiveFormsModule, RouterLink, DatePipe, ErrorState],
  template: `
    <section class="th-card mt-5" aria-labelledby="contacts-heading">
      <h2 id="contacts-heading" class="text-lg font-semibold">Portal contacts</h2>
      <p class="sr-only" aria-live="polite">{{ announcement() }}</p>
      @if (unavailable(); as problem) {
        <p class="mt-2 text-sm text-muted">
          {{ problem.title }}
          @if (canChangePlan()) {
            <a
              [routerLink]="['/o', slug(), 'settings']"
              fragment="plan"
              class="font-medium text-accent hover:underline"
              >See the plans</a
            >
          }
        </p>
      } @else if (error(); as problem) {
        <div class="mt-3"><app-error-state [message]="problem.title" (retry)="load()" /></div>
      } @else if (contacts() === null) {
        <div class="mt-3 h-16 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
      } @else {
        <p class="mt-1 text-sm text-muted">
          They sign in to the client portal with an emailed link: no password. They see this
          client's visits, quotes and requests only.
        </p>
        @if (contacts()!.length === 0) {
          <p class="mt-3 text-sm text-muted">No contacts yet.</p>
        } @else {
          <ul class="mt-3 divide-y divide-border" aria-label="Portal contacts">
            @for (contact of contacts(); track contact.id) {
              <li class="flex flex-wrap items-center justify-between gap-2 py-2">
                <div class="min-w-0">
                  <p class="font-medium">{{ contact.name }}</p>
                  <p class="text-sm text-muted">
                    {{ contact.email }} ·
                    {{
                      contact.lastSignInAt
                        ? 'last signed in ' + (contact.lastSignInAt | date: 'd MMM y')
                        : 'not signed in yet'
                    }}
                  </p>
                </div>
                @if (canManage()) {
                  <button
                    type="button"
                    class="th-button-secondary"
                    [disabled]="busy()"
                    (click)="remove(contact)"
                  >
                    Remove<span class="sr-only"> {{ contact.name }}</span>
                  </button>
                }
              </li>
            }
          </ul>
        }

        @if (canManage() && clientActive()) {
          <form
            class="mt-4 grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-end"
            [formGroup]="form"
            (ngSubmit)="add()"
            novalidate
          >
            @if (formError(); as message) {
              <div class="th-alert-error sm:col-span-3" role="alert">{{ message }}</div>
            }
            <div>
              <label class="th-label" for="contact-name">Name</label>
              <input
                id="contact-name"
                class="th-input"
                formControlName="name"
                autocomplete="off"
                [attr.aria-invalid]="!!fieldError('name')"
                [attr.aria-describedby]="fieldError('name') ? 'contact-name-error' : null"
              />
              @if (fieldError('name'); as message) {
                <p id="contact-name-error" class="th-field-error">{{ message }}</p>
              }
            </div>
            <div>
              <label class="th-label" for="contact-email">Email</label>
              <input
                id="contact-email"
                class="th-input"
                type="email"
                formControlName="email"
                autocomplete="off"
                [attr.aria-invalid]="!!fieldError('email')"
                [attr.aria-describedby]="fieldError('email') ? 'contact-email-error' : null"
              />
              @if (fieldError('email'); as message) {
                <p id="contact-email-error" class="th-field-error">{{ message }}</p>
              }
            </div>
            <button type="submit" class="th-button" [disabled]="busy()">Add contact</button>
          </form>
        }
      }
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClientContacts {
  readonly slug = input.required<string>();
  readonly organizationId = input.required<string>();
  readonly clientId = input.required<string>();
  /** Archived clients get no new contacts (the API refuses too). */
  readonly clientActive = input(true);

  private readonly api = inject(ClientsApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);

  protected readonly canManage = computed(() => this.session.can(this.slug(), 'portal.manage'));
  protected readonly canChangePlan = computed(() =>
    this.session.can(this.slug(), 'billing.manage'),
  );
  protected readonly contacts = signal<ClientContact[] | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);
  protected readonly unavailable = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly announcement = signal('');
  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    email: ['', [Validators.required, Validators.email]],
  });

  constructor() {
    effect(() => {
      this.organizationId();
      this.clientId();
      void this.load();
    });
  }

  protected fieldError(field: keyof typeof messages): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  async load(): Promise<void> {
    this.error.set(null);
    this.unavailable.set(null);
    try {
      this.contacts.set(await this.api.contacts(this.organizationId(), this.clientId()));
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'plan-feature-unavailable') {
        this.unavailable.set(problem);
      } else {
        this.error.set(problem);
      }
    }
  }

  protected async add(): Promise<void> {
    this.formError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    const { name, email } = this.form.getRawValue();
    try {
      const contact = await this.api.addContact(this.organizationId(), this.clientId(), {
        name,
        email,
      });
      this.form.reset();
      this.announcement.set(`Added ${contact.name}. They get an email explaining how to sign in.`);
      await this.load();
    } catch (error) {
      this.formError.set(applyProblem(this.form, toApiProblem(error)));
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(contact: ClientContact): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Remove ${contact.name}?`,
      message: 'They lose access to the portal on their next click. Their requests stay.',
      confirmLabel: 'Remove contact',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    this.busy.set(true);
    try {
      await this.api.removeContact(this.organizationId(), contact.id);
      this.announcement.set(`Removed ${contact.name}.`);
      await this.load();
    } catch (error) {
      this.error.set(toApiProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
