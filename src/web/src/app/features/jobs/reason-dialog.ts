import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CspSafeDialogContainer } from '../../shared/ui/dialog-container';

export interface ReasonOptions {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel: string;
  readonly danger?: boolean;
  /** The field's label ("Reason" unless given). */
  readonly label?: string;
  /** May be left empty (a comment on a quote, SPEC-V2 5.4); empty comes back as "". */
  readonly optional?: boolean;
  readonly maxLength?: number;
  /** Said when a required text is missing. */
  readonly requiredMessage?: string;
}

/**
 * Asks for the reason a move needs (cancel, reopen: SPEC 5.4; declining a request: SPEC-V2 5.5), or an optional
 * comment (deciding on a quote). Null when the user goes back.
 */
@Component({
  selector: 'app-reason-dialog',
  host: {
    class:
      'block w-[min(30rem,calc(100vw-2rem))] rounded-lg border border-border bg-surface p-6 shadow-xl',
  },
  template: `
    <h2 id="reason-title" class="text-lg font-semibold">{{ data.title }}</h2>
    <p id="reason-message" class="mt-2 text-sm text-muted">{{ data.message }}</p>
    <form class="mt-4" (submit)="$event.preventDefault(); submit()" novalidate>
      <label class="th-label" for="reason"
        >{{ data.label ?? 'Reason' }}
        @if (data.optional) {
          <span class="font-normal text-muted"> (optional)</span>
        }
      </label>
      <textarea
        #field
        id="reason"
        class="th-input min-h-24 py-2"
        [maxLength]="maxLength"
        [attr.aria-invalid]="!!error()"
        [attr.aria-describedby]="error() ? 'reason-hint reason-error' : 'reason-hint'"
        (input)="reason.set(field.value); error.set(null)"
      ></textarea>
      <p id="reason-hint" class="th-hint">
        {{ reason().trim().length }} / {{ maxLength }} characters
      </p>
      @if (error(); as message) {
        <p id="reason-error" class="th-field-error">{{ message }}</p>
      }
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="th-button-secondary" (click)="ref.close(null)">Back</button>
        <button type="submit" [class]="data.danger ? 'th-button-danger' : 'th-button'">
          {{ data.confirmLabel }}
        </button>
      </div>
    </form>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReasonDialog {
  protected readonly data = inject<ReasonOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<string | null>>(DialogRef);
  protected readonly maxLength = this.data.maxLength ?? 500;
  protected readonly reason = signal('');
  protected readonly error = signal<string | null>(null);

  protected submit(): void {
    const reason = this.reason().trim();
    if (!reason && !this.data.optional) {
      this.error.set(
        this.data.requiredMessage ?? 'Give a reason; it is kept in the job’s history.',
      );
      return;
    }
    this.ref.close(reason);
  }
}

@Injectable({ providedIn: 'root' })
export class AskReason {
  private readonly dialog = inject(Dialog);

  /** The trimmed text ("" for an optional one left empty), or null when the user goes back. */
  async ask(options: ReasonOptions): Promise<string | null> {
    const ref = this.dialog.open<string | null, ReasonOptions>(ReasonDialog, {
      data: options,
      backdropClass: 'th-dialog-backdrop',
      // Our container: the CDK one would inject its styles as an inline <style>, which the CSP blocks (D027).
      container: CspSafeDialogContainer,
      role: 'dialog',
      ariaModal: true,
      ariaLabelledBy: 'reason-title',
      ariaDescribedBy: 'reason-message',
      autoFocus: '#reason',
    });
    return (await firstValueFrom(ref.closed)) ?? null;
  }
}
