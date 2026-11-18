import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CspSafeDialogContainer } from '../../shared/ui/dialog-container';
import { BoardCard, BoardPerson } from './board.api';

export interface MoveOptions {
  readonly card: BoardCard;
  readonly people: readonly BoardPerson[];
  /** The day the dialog starts on: the card's, or the first day of the week shown. */
  readonly day: string;
}

export interface MoveChoice {
  readonly assigneeUserId: string | null;
  readonly scheduledOn: string;
}

/**
 * The keyboard path of the board (SPEC-V2 9, WCAG 2.5.7): every move a drag can make, chosen from two fields. Any
 * date is allowed, so a card can also go to another week (dragging stays within the week shown).
 */
@Component({
  selector: 'app-move-dialog',
  host: {
    class:
      'block w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-surface p-6 shadow-xl',
  },
  template: `
    <h2 id="move-title" class="text-lg font-semibold">Move {{ data.card.label }}</h2>
    <p id="move-message" class="mt-2 text-sm text-muted">{{ data.card.title }}</p>
    <form class="mt-4 space-y-4" (submit)="$event.preventDefault(); submit()" novalidate>
      <div>
        <label class="th-label" for="move-person">Person</label>
        <select
          id="move-person"
          class="th-input"
          #person
          (change)="assignee.set(person.value || null)"
        >
          <option value="" [selected]="assignee() === null">Unassigned</option>
          @for (p of activePeople; track p.userId) {
            <option [value]="p.userId" [selected]="assignee() === p.userId">{{ p.name }}</option>
          }
        </select>
      </div>
      <div>
        <label class="th-label" for="move-day">Day</label>
        <input
          id="move-day"
          class="th-input"
          type="date"
          #dayInput
          [value]="day()"
          [attr.aria-invalid]="!!error()"
          [attr.aria-describedby]="error() ? 'move-day-error' : null"
          (input)="day.set(dayInput.value); error.set(null)"
        />
        @if (error(); as message) {
          <p id="move-day-error" class="th-field-error">{{ message }}</p>
        }
      </div>
      <div class="flex flex-wrap justify-end gap-2 pt-2">
        <button type="button" class="th-button-secondary" (click)="ref.close(null)">Cancel</button>
        <button type="submit" class="th-button">Move</button>
      </div>
    </form>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MoveDialog {
  protected readonly data = inject<MoveOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<MoveChoice | null>>(DialogRef);
  protected readonly activePeople = this.data.people.filter((p) => p.active);
  protected readonly assignee = signal<string | null>(this.data.card.assigneeUserId);
  protected readonly day = signal(this.data.day);
  protected readonly error = signal<string | null>(null);

  protected submit(): void {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(this.day())) {
      this.error.set('Choose a day.');
      return;
    }
    this.ref.close({ assigneeUserId: this.assignee(), scheduledOn: this.day() });
  }
}

@Injectable({ providedIn: 'root' })
export class AskMove {
  private readonly dialog = inject(Dialog);

  /** The chosen person and day, or null when cancelled. */
  async ask(options: MoveOptions): Promise<MoveChoice | null> {
    const ref = this.dialog.open<MoveChoice | null, MoveOptions>(MoveDialog, {
      data: options,
      backdropClass: 'th-dialog-backdrop',
      // Our container: the CDK one would inject its styles as an inline <style>, which the CSP blocks (D027).
      container: CspSafeDialogContainer,
      role: 'dialog',
      ariaModal: true,
      ariaLabelledBy: 'move-title',
      ariaDescribedBy: 'move-message',
      autoFocus: '#move-person',
    });
    return (await firstValueFrom(ref.closed)) ?? null;
  }
}
