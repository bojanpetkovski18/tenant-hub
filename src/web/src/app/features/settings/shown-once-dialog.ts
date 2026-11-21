import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CspSafeDialogContainer } from '../../shared/ui/dialog-container';

export interface ShownOnceOptions {
  readonly title: string;
  readonly message: string;
  /** The field's label: "API key", "Signing secret". */
  readonly label: string;
  readonly value: string;
  /**
   * Where focus goes when the dialog closes (a CSS selector). The button that opened it was disabled while the
   * request ran, so it lost focus, and the dialog would otherwise return it to the page's body.
   */
  readonly returnFocusTo: string;
}

/**
 * A key or a signing secret, shown this once (SPEC-V2 5.8): the server keeps only a hash or an encrypted copy, so
 * closing the dialog is the last chance to copy it. The value sits in a read-only field (selectable, readable by
 * screen readers) with a copy button that says when it worked.
 */
@Component({
  selector: 'app-shown-once-dialog',
  host: {
    class:
      'block w-[min(34rem,calc(100vw-2rem))] rounded-lg border border-border bg-surface p-6 shadow-xl',
  },
  template: `
    <h2 id="shown-once-title" class="text-lg font-semibold">{{ data.title }}</h2>
    <p id="shown-once-message" class="mt-2 text-sm text-muted">{{ data.message }}</p>
    <label class="th-label mt-4" for="shown-once-value">{{ data.label }}</label>
    <div class="flex flex-wrap gap-2">
      <input
        #field
        id="shown-once-value"
        class="th-input min-w-0 flex-1 font-mono text-sm"
        readonly
        [value]="data.value"
        (focus)="field.select()"
      />
      <button type="button" class="th-button-secondary" (click)="copy(field)">Copy</button>
    </div>
    <p class="th-hint" role="status">{{ copied() ? 'Copied.' : '' }}</p>
    <div class="mt-6 flex justify-end">
      <button type="button" class="th-button" (click)="ref.close()">I have copied it</button>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShownOnceDialog {
  protected readonly data = inject<ShownOnceOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<void>>(DialogRef);
  protected readonly copied = signal(false);

  protected async copy(field: HTMLInputElement): Promise<void> {
    field.select();
    try {
      await navigator.clipboard.writeText(this.data.value);
      this.copied.set(true);
    } catch {
      // No clipboard access (an insecure origin, a denied permission): the value is selected for Ctrl+C.
      this.copied.set(false);
    }
  }
}

@Injectable({ providedIn: 'root' })
export class ShowOnce {
  private readonly dialog = inject(Dialog);

  /** Resolves when the dialog is closed. Escape and the backdrop do not close it: the value would be lost unread. */
  async show(options: ShownOnceOptions): Promise<void> {
    const ref = this.dialog.open<void, ShownOnceOptions>(ShownOnceDialog, {
      data: options,
      backdropClass: 'th-dialog-backdrop',
      // Our container: the CDK one would inject its styles as an inline <style>, which the CSP blocks (D027).
      container: CspSafeDialogContainer,
      role: 'dialog',
      ariaModal: true,
      ariaLabelledBy: 'shown-once-title',
      ariaDescribedBy: 'shown-once-message',
      disableClose: true,
      autoFocus: '#shown-once-value',
      restoreFocus: false,
    });
    await firstValueFrom(ref.closed);
    // After the page has rendered what changed (the new key or endpoint in its list).
    setTimeout(() => document.querySelector<HTMLElement>(options.returnFocusTo)?.focus());
  }
}
