import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { Client, ClientsApi } from './clients.api';

const messages = {
  name: {
    required: 'Enter a name.',
    minlength: 'Use at least 2 characters.',
    maxlength: 'Use at most 120 characters.',
  },
  contactName: { maxlength: 'Use at most 120 characters.' },
  email: { email: 'Enter a valid email address.', maxlength: 'Use at most 254 characters.' },
  phone: { maxlength: 'Use at most 40 characters.' },
  address: { maxlength: 'Use at most 300 characters.' },
  notes: { maxlength: 'Use at most 2,000 characters.' },
} as const;

type Field = keyof typeof messages;

const fields: readonly {
  name: Field;
  label: string;
  type?: string;
  autocomplete?: string;
  multiline?: boolean;
}[] = [
  { name: 'name', label: 'Name' },
  { name: 'contactName', label: 'Contact person', autocomplete: 'off' },
  { name: 'email', label: 'Email', type: 'email', autocomplete: 'off' },
  { name: 'phone', label: 'Phone', type: 'tel', autocomplete: 'off' },
  { name: 'address', label: 'Address', autocomplete: 'off' },
  { name: 'notes', label: 'Notes', multiline: true },
];

/** US-20: create a client, or edit one with optimistic concurrency (a conflict offers to reload). */
@Component({
  selector: 'app-client-form-page',
  imports: [ReactiveFormsModule, RouterLink, EmptyState, ErrorState],
  template: `
    <a [routerLink]="backLink()" class="text-sm text-muted hover:text-fg">← Back</a>
    <h1 class="mt-3 text-2xl font-semibold tracking-tight">
      {{ id() ? 'Edit client' : 'New client' }}
    </h1>

    @if (!canWrite()) {
      <div class="mt-5">
        <app-empty-state
          title="You can view clients but not change them"
          message="Ask an admin if you need to."
        />
      </div>
    } @else if (loading()) {
      <div class="mt-5 h-64 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
    } @else if (loadError(); as error) {
      <div class="mt-5"><app-error-state [message]="error.title" (retry)="load()" /></div>
    } @else {
      <form
        #formEl
        class="th-card mt-5 max-w-2xl space-y-5"
        [formGroup]="form"
        (ngSubmit)="save()"
        novalidate
      >
        @if (formError(); as message) {
          <div class="th-alert-error" role="alert">{{ message }}</div>
        }
        @for (field of fields; track field.name) {
          <div>
            <label class="th-label" [for]="field.name">
              {{ field.label }}
              @if (field.name !== 'name') {
                <span class="font-normal text-muted">(optional)</span>
              }
            </label>
            @if (field.multiline) {
              <textarea
                class="th-input min-h-24 py-2"
                [id]="field.name"
                [formControlName]="field.name"
                [attr.aria-invalid]="!!error(field.name)"
                [attr.aria-describedby]="error(field.name) ? field.name + '-error' : null"
              ></textarea>
            } @else {
              <input
                class="th-input"
                [id]="field.name"
                [type]="field.type ?? 'text'"
                [attr.autocomplete]="field.autocomplete ?? null"
                [formControlName]="field.name"
                [attr.aria-invalid]="!!error(field.name)"
                [attr.aria-describedby]="error(field.name) ? field.name + '-error' : null"
              />
            }
            @if (error(field.name); as message) {
              <p [id]="field.name + '-error'" class="th-field-error">{{ message }}</p>
            }
          </div>
        }
        <div class="flex flex-wrap gap-2">
          <button type="submit" class="th-button" [disabled]="saving()">
            {{ saving() ? 'Saving…' : id() ? 'Save changes' : 'Create client' }}
          </button>
          <a [routerLink]="backLink()" class="th-button-secondary">Cancel</a>
        </div>
      </form>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClientFormPage {
  readonly slug = input.required<string>();
  /** Present when editing (route clients/:id/edit). */
  readonly id = input<string>();

  private readonly api = inject(ClientsApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');

  protected readonly fields = fields;
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canWrite = computed(() => this.session.can(this.slug(), 'clients.write'));
  /** Back to the client when editing, else to the list (absolute; see ClientDetailPage). */
  protected readonly backLink = computed(() =>
    this.id() ? ['/o', this.slug(), 'clients', this.id()!] : ['/o', this.slug(), 'clients'],
  );

  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(120)]],
    contactName: ['', Validators.maxLength(120)],
    email: ['', [Validators.email, Validators.maxLength(254)]],
    phone: ['', Validators.maxLength(40)],
    address: ['', Validators.maxLength(300)],
    notes: ['', Validators.maxLength(2000)],
  });
  private version = 0;
  protected readonly loading = signal(false);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);

  constructor() {
    effect(() => {
      if (this.membership() && this.id()) {
        void this.load();
      }
    });
  }

  protected error(field: Field): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.fill(await this.api.get(this.membership()!.organizationId, this.id()!));
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  protected async save(): Promise<void> {
    this.formError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusInvalid();
      return;
    }

    const organizationId = this.membership()!.organizationId;
    const input = this.form.getRawValue();
    this.saving.set(true);
    try {
      const id = this.id();
      const saved = id
        ? await this.api.update(organizationId, id, input, this.version)
        : await this.api.create(organizationId, input);
      await this.router.navigate(['/o', this.slug(), 'clients', saved.id]);
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'concurrency-conflict') {
        await this.resolveConflict();
        return;
      }
      this.formError.set(applyProblem(this.form, problem, { 'client-name-taken': 'name' }));
      this.focusInvalid();
    } finally {
      this.saving.set(false);
    }
  }

  /** Someone else saved first: reload their version, or keep these edits (the next save will conflict again). */
  private async resolveConflict(): Promise<void> {
    const reload = await this.confirm.ask({
      title: 'This client was changed by someone else',
      message:
        'They saved after you opened this form. Reload to see their version (your edits here will be lost), or keep editing.',
      confirmLabel: 'Reload their version',
      cancelLabel: 'Keep editing',
    });
    if (reload) {
      await this.load();
    } else {
      this.formError.set(
        'Not saved: someone else changed this client. Reload before saving again.',
      );
    }
  }

  private fill(client: Client): void {
    this.version = client.version;
    this.form.reset({
      name: client.name,
      contactName: client.contactName ?? '',
      email: client.email ?? '',
      phone: client.phone ?? '',
      address: client.address ?? '',
      notes: client.notes ?? '',
    });
  }

  private focusInvalid(): void {
    const form = this.formEl();
    if (form) {
      focusFirstInvalid(form, this.injector);
    }
  }
}
