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
  /** Null: back to "Not scheduled". */
  readonly scheduledOn: string | null;
}

/**
 * The keyboard path of the board (SPEC-V2 9, WCAG 2.5.7): every move a drag can make, chosen from two fields (an empty day is "Not scheduled"). Any
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
    <form class="mt-4 space-y-4" (submit)="$event.preventDefault(); submit(dayInput)" novalidate>
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
          [value]="initialDay"
          [attr.aria-invalid]="!!error()"
          [attr.aria-describedby]="error() ? 'move-day-error' : 'move-day-hint'"
          (input)="error.set(null)"
        />
        <p id="move-day-hint" class="th-hint">
          Leave it empty to put the job back in "Not scheduled".
        </p>
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
  /**
   * Written to the field once, never again: writing "" back while someone clears one part of the date (the field
   * reports "" for a half-typed date) would wipe the whole date. The day is read from the field on submit.
   */
  protected readonly initialDay = this.data.day;
  protected readonly error = signal<string | null>(null);

  protected submit(dayInput: HTMLInputElement): void {
    const day = dayInput.value;
    // A half-typed date also reads as "", so it must not be taken for "no day". Asked of the field now: going from
    // half-typed to empty fires no input event (the value stays ""), so a flag kept from events would be stale.
    if (dayInput.validity.badInput || (day !== '' && !/^\d{4}-\d{2}-\d{2}$/.test(day))) {
      this.error.set('Finish the date, or clear it to put the job back in "Not scheduled".');
      return;
    }
    this.ref.close({ assigneeUserId: this.assignee(), scheduledOn: day || null });
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
