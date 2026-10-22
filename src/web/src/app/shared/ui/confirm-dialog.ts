import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CspSafeDialogContainer } from './dialog-container';

export interface ConfirmOptions {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel: string;
  readonly cancelLabel?: string;
  /** Styles the confirm button as destructive. */
  readonly danger?: boolean;
}

/** A modal question with two answers (CDK Dialog: focus trap, Escape, focus returned on close). */
@Component({
  selector: 'app-confirm-dialog',
  host: {
    class:
      'block w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-surface p-6 shadow-xl',
  },
  template: `
    <h2 id="confirm-title" class="text-lg font-semibold">{{ data.title }}</h2>
    <p id="confirm-message" class="mt-2 text-sm text-muted">{{ data.message }}</p>
    <div class="mt-6 flex flex-wrap justify-end gap-2">
      <button type="button" class="th-button-secondary" (click)="ref.close(false)">
        {{ data.cancelLabel ?? 'Cancel' }}
      </button>
      <button
        type="button"
        [class]="data.danger ? 'th-button-danger' : 'th-button'"
        (click)="ref.close(true)"
      >
        {{ data.confirmLabel }}
      </button>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
}

@Injectable({ providedIn: 'root' })
export class Confirm {
  private readonly dialog = inject(Dialog);

  /** Resolves true only when the user picks the confirm button. */
  async ask(options: ConfirmOptions): Promise<boolean> {
    const ref = this.dialog.open<boolean, ConfirmOptions>(ConfirmDialog, {
      data: options,
      backdropClass: 'th-dialog-backdrop',
      // Our container: the CDK one would inject its styles as an inline <style>, which the CSP blocks (D027).
      container: CspSafeDialogContainer,
      // The CDK container carries the dialog semantics; these point it at our title and message.
      role: 'alertdialog',
      ariaModal: true,
      ariaLabelledBy: 'confirm-title',
      ariaDescribedBy: 'confirm-message',
      // Focus the safe choice first; Escape and the backdrop also cancel.
      autoFocus: 'first-tabbable',
    });
    return (await firstValueFrom(ref.closed)) === true;
  }
}
