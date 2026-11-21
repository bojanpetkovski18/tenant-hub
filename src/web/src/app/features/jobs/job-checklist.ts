import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList } from '@angular/cdk/drag-drop';
import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { ErrorState } from '../../shared/ui/states';
import { Checklist, ChecklistChange, ChecklistItem, JobsApi } from './jobs.api';

/**
 * US-90: the job's checklist. Whoever works on the job ticks, adds, edits, removes and reorders items while it is
 * open (the API's `canEdit`); a completed job's checklist is read-only and says why. Reordering works by pointer (drag
 * the handle) and by keyboard (Move up, Move down), and every move is announced. Each change goes to the API at once;
 * the page reloads the job afterwards, since the Complete button depends on the required items.
 */
@Component({
  selector: 'app-job-checklist',
  imports: [CdkDropList, CdkDrag, CdkDragHandle, DatePipe, ErrorState],
  template: `
    <section class="th-card mt-5" aria-labelledby="checklist-heading">
      <div class="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="checklist-heading" class="text-lg font-semibold">Checklist</h2>
        @if (checklist(); as c) {
          @if (c.items.length > 0) {
            <p class="text-sm text-muted">{{ doneCount(c) }} of {{ c.items.length }} done</p>
          }
        }
      </div>
      <p class="sr-only" aria-live="polite" aria-atomic="true">{{ announcement() }}</p>

      @if (loadError(); as error) {
        <div class="mt-3"><app-error-state [message]="error.title" (retry)="load()" /></div>
      } @else if (checklist(); as c) {
        @if (message(); as text) {
          <div class="th-alert-error mt-3" role="alert">{{ text }}</div>
        }
        @if (c.readOnlyReason) {
          <p class="mt-2 text-sm text-muted">{{ c.readOnlyReason }}</p>
        }
        @if (c.items.length === 0) {
          <p class="mt-2 text-sm text-muted">
            {{
              c.canEdit
                ? 'No checklist yet. Add what has to be done; mark the items that must be ticked before the job is completed.'
                : 'This job has no checklist.'
            }}
          </p>
        } @else {
          <ul
            class="mt-3 divide-y divide-border rounded-md border border-border"
            cdkDropList
            [cdkDropListDisabled]="!c.canEdit || busy()"
            (cdkDropListDropped)="dropped($event, c)"
          >
            @for (item of c.items; track item.id; let first = $first; let last = $last) {
              <li
                class="flex flex-wrap items-start gap-2 bg-surface px-3 py-2"
                cdkDrag
                [cdkDragData]="item"
              >
                @if (c.canEdit) {
                  <span
                    cdkDragHandle
                    class="mt-1 hidden cursor-grab select-none text-muted sm:inline"
                    aria-hidden="true"
                    title="Drag to reorder"
                    >⠿</span
                  >
                }
                <input
                  type="checkbox"
                  class="mt-1 size-4 accent-accent"
                  [id]="'item-' + item.id"
                  [checked]="item.done"
                  [disabled]="!c.canEdit || busy()"
                  (change)="tick(item, $any($event.target).checked)"
                />
                <div class="min-w-0 flex-1 basis-48">
                  @if (editing() === item.id) {
                    <form
                      class="flex flex-wrap items-center gap-2"
                      (submit)="$event.preventDefault(); saveEdit(item)"
                    >
                      <label class="sr-only" [for]="'edit-' + item.id">Item text</label>
                      <input
                        #text
                        class="th-input min-w-0 flex-1"
                        [id]="'edit-' + item.id"
                        maxlength="200"
                        [value]="item.text"
                        (input)="draft.set(text.value)"
                        (keydown.escape)="editing.set(null)"
                      />
                      <label class="flex items-center gap-1 text-sm">
                        <input
                          type="checkbox"
                          class="size-4 accent-accent"
                          [checked]="draftRequired()"
                          (change)="draftRequired.set($any($event.target).checked)"
                        />
                        Required
                      </label>
                      <button type="submit" class="th-button" [disabled]="busy()">Save</button>
                      <button type="button" class="th-button-secondary" (click)="editing.set(null)">
                        Cancel
                      </button>
                    </form>
                  } @else {
                    <label
                      [for]="'item-' + item.id"
                      class="break-words"
                      [class.line-through]="item.done"
                      [class.text-muted]="item.done"
                    >
                      {{ item.text }}
                    </label>
                    @if (item.required) {
                      <span class="th-badge ml-1">Required</span>
                    }
                    @if (item.done && item.doneAt) {
                      <p class="text-xs text-muted">
                        Ticked by {{ item.doneByName ?? 'someone' }} ·
                        <time [attr.datetime]="item.doneAt">{{
                          item.doneAt | date: 'd MMM, HH:mm'
                        }}</time>
                      </p>
                    }
                  }
                </div>
                @if (c.canEdit && editing() !== item.id) {
                  <div class="ml-auto flex flex-wrap gap-1">
                    <button
                      type="button"
                      class="th-icon-button"
                      [disabled]="busy() || first"
                      [attr.aria-label]="'Move “' + item.text + '” up'"
                      (click)="move(item, item.position - 1, c)"
                    >
                      ↑
                    </button>
                    <button
                      type="button"
                      class="th-icon-button"
                      [disabled]="busy() || last"
                      [attr.aria-label]="'Move “' + item.text + '” down'"
                      (click)="move(item, item.position + 1, c)"
                    >
                      ↓
                    </button>
                    <button
                      type="button"
                      class="th-icon-button"
                      [disabled]="busy()"
                      [attr.aria-label]="'Edit “' + item.text + '”'"
                      (click)="startEdit(item)"
                    >
                      ✎
                    </button>
                    <button
                      type="button"
                      class="th-icon-button text-danger"
                      [disabled]="busy()"
                      [attr.aria-label]="'Remove “' + item.text + '”'"
                      (click)="remove(item)"
                    >
                      ✕
                    </button>
                  </div>
                }
              </li>
            }
          </ul>
        }
        @if (c.canEdit && c.items.length < c.maxItems) {
          <form
            class="mt-3 flex flex-wrap items-end gap-2"
            (submit)="$event.preventDefault(); add()"
            novalidate
          >
            <div class="min-w-0 flex-1">
              <label class="th-label" for="new-item">New item</label>
              <input
                #newItem
                id="new-item"
                class="th-input"
                maxlength="200"
                [value]="newText()"
                [attr.aria-invalid]="!!addError()"
                [attr.aria-describedby]="addError() ? 'new-item-error' : null"
                (input)="newText.set(newItem.value); addError.set(null)"
              />
            </div>
            <label class="flex h-10 items-center gap-1 text-sm">
              <input
                type="checkbox"
                class="size-4 accent-accent"
                [checked]="newRequired()"
                (change)="newRequired.set($any($event.target).checked)"
              />
              Required
            </label>
            <button type="submit" class="th-button" [disabled]="busy()">Add</button>
            @if (addError(); as error) {
              <p id="new-item-error" class="th-field-error w-full">{{ error }}</p>
            }
          </form>
        }
      } @else {
        <div class="mt-3 h-16 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
      }
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobChecklist {
  readonly organizationId = input.required<string>();
  readonly jobId = input.required<string>();
  /** Changes when the job does (its status decides whether the checklist can change). */
  readonly jobVersion = input<number>(0);
  /** The checklist changed: the job's Complete button may now be enabled or disabled. */
  readonly changed = output<void>();

  private readonly api = inject(JobsApi);
  private readonly confirm = inject(Confirm);

  protected readonly checklist = signal<Checklist | null>(null);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly announcement = signal('');
  protected readonly editing = signal<string | null>(null);
  protected readonly draft = signal('');
  protected readonly draftRequired = signal(false);
  protected readonly newText = signal('');
  protected readonly newRequired = signal(true);
  protected readonly addError = signal<string | null>(null);

  constructor() {
    effect(() => {
      if (this.organizationId() && this.jobId()) {
        this.jobVersion();
        void this.load();
      }
    });
  }

  protected doneCount(c: Checklist): number {
    return c.items.filter((i) => i.done).length;
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.checklist.set(await this.api.checklist(this.organizationId(), this.jobId()));
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    }
  }

  protected async tick(item: ChecklistItem, done: boolean): Promise<void> {
    if (await this.change(item, { done })) {
      this.announcement.set(`${done ? 'Ticked' : 'Unticked'} ${item.text}.`);
    }
  }

  protected async move(item: ChecklistItem, position: number, c: Checklist): Promise<void> {
    if (position < 0 || position >= c.items.length) {
      return;
    }
    if (await this.change(item, { position })) {
      this.announcement.set(`Moved ${item.text} to place ${position + 1} of ${c.items.length}.`);
    }
  }

  protected async dropped(
    event: CdkDragDrop<unknown, unknown, ChecklistItem>,
    c: Checklist,
  ): Promise<void> {
    if (event.previousIndex !== event.currentIndex) {
      await this.move(event.item.data, event.currentIndex, c);
    }
  }

  protected startEdit(item: ChecklistItem): void {
    this.draft.set(item.text);
    this.draftRequired.set(item.required);
    this.editing.set(item.id);
  }

  protected async saveEdit(item: ChecklistItem): Promise<void> {
    if (await this.change(item, { text: this.draft(), required: this.draftRequired() })) {
      this.editing.set(null);
      this.announcement.set(`Saved ${this.draft().trim()}.`);
    }
  }

  protected async add(): Promise<void> {
    const text = this.newText().trim();
    if (!text) {
      this.addError.set('Write what has to be done.');
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    try {
      this.checklist.set(
        await this.api.addChecklistItem(
          this.organizationId(),
          this.jobId(),
          text,
          this.newRequired(),
        ),
      );
      this.newText.set('');
      this.announcement.set(`Added ${text}.`);
      this.changed.emit();
    } catch (error) {
      const problem = toApiProblem(error);
      this.addError.set(problem.fieldErrors['text']?.[0] ?? problem.title);
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(item: ChecklistItem): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Remove “${item.text}”?`,
      message: 'It comes off this job’s checklist. The history keeps a record.',
      confirmLabel: 'Remove item',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    try {
      this.checklist.set(
        await this.api.removeChecklistItem(this.organizationId(), this.jobId(), item.id),
      );
      this.announcement.set(`Removed ${item.text}.`);
      this.changed.emit();
    } catch (error) {
      this.failed(error);
    } finally {
      this.busy.set(false);
    }
  }

  private async change(item: ChecklistItem, change: ChecklistChange): Promise<boolean> {
    this.busy.set(true);
    this.message.set(null);
    try {
      this.checklist.set(
        await this.api.changeChecklistItem(this.organizationId(), this.jobId(), item.id, change),
      );
      this.changed.emit();
      return true;
    } catch (error) {
      this.failed(error);
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  /** Shows why, and reloads: the job may have been completed or the item removed meanwhile. */
  private failed(error: unknown): void {
    const problem = toApiProblem(error);
    this.message.set(problem.fieldErrors['text']?.[0] ?? problem.title);
    void this.load();
  }
}
