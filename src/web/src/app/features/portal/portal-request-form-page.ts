import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { toApiProblem } from '../../core/http/api-problem';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { PortalApi } from './portal.api';

const messages = {
  title: {
    required: 'Describe the work in a few words.',
    minlength: 'Use at least 3 characters.',
    maxlength: 'Use at most 120 characters.',
  },
  description: { maxlength: 'Use at most 2,000 characters.' },
  preferredOn: {},
} as const;

/** US-84: asking for work. The API checks every rule (SPEC-V2 5.5); the form only helps. */
@Component({
  selector: 'app-portal-request-form-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <a
      [routerLink]="['/portal', slug(), 'requests']"
      class="text-sm font-medium text-accent hover:underline"
      >← Requests</a
    >
    <h1 class="mt-4 text-2xl font-semibold tracking-tight">Request work</h1>
    <form
      #formEl
      class="th-card mt-4 space-y-5"
      [formGroup]="form"
      (ngSubmit)="submit()"
      novalidate
    >
      @if (formError(); as message) {
        <div class="th-alert-error" role="alert">{{ message }}</div>
      }
      <div>
        <label class="th-label" for="request-title">What do you need?</label>
        <input
          id="request-title"
          class="th-input"
          formControlName="title"
          maxlength="120"
          [attr.aria-invalid]="!!error('title')"
          [attr.aria-describedby]="error('title') ? 'request-title-error' : null"
        />
        @if (error('title'); as message) {
          <p id="request-title-error" class="th-field-error">{{ message }}</p>
        }
      </div>
      <div>
        <label class="th-label" for="request-description"
          >Details <span class="font-normal text-muted">(optional)</span></label
        >
        <textarea
          id="request-description"
          class="th-input min-h-28 py-2"
          formControlName="description"
          maxlength="2000"
          [attr.aria-invalid]="!!error('description')"
          [attr.aria-describedby]="error('description') ? 'request-description-error' : null"
        ></textarea>
        @if (error('description'); as message) {
          <p id="request-description-error" class="th-field-error">{{ message }}</p>
        }
      </div>
      <div>
        <label class="th-label" for="request-day"
          >Preferred day <span class="font-normal text-muted">(optional)</span></label
        >
        <input
          id="request-day"
          class="th-input max-w-48"
          type="date"
          formControlName="preferredOn"
          [attr.aria-invalid]="!!error('preferredOn')"
          [attr.aria-describedby]="error('preferredOn') ? 'request-day-error' : null"
        />
        @if (error('preferredOn'); as message) {
          <p id="request-day-error" class="th-field-error">{{ message }}</p>
        }
      </div>
      <button type="submit" class="th-button" [disabled]="saving()">
        {{ saving() ? 'Sending…' : 'Send request' }}
      </button>
    </form>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalRequestFormPage {
  readonly slug = input.required<string>();

  private readonly api = inject(PortalApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');

  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly form = inject(NonNullableFormBuilder).group({
    title: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(120)]],
    description: ['', Validators.maxLength(2000)],
    preferredOn: [''],
  });

  protected error(field: keyof typeof messages): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  protected async submit(): Promise<void> {
    this.formError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusInvalid();
      return;
    }
    this.saving.set(true);
    const { title, description, preferredOn } = this.form.getRawValue();
    try {
      await this.api.submitRequest({
        title,
        description: description || null,
        preferredOn: preferredOn || null,
      });
      await this.router.navigate(['/portal', this.slug(), 'requests'], {
        queryParams: { notice: 'sent' },
      });
    } catch (error) {
      this.formError.set(applyProblem(this.form, toApiProblem(error)));
      this.focusInvalid();
    } finally {
      this.saving.set(false);
    }
  }

  private focusInvalid(): void {
    const form = this.formEl();
    if (form) {
      focusFirstInvalid(form, this.injector);
    }
  }
}
