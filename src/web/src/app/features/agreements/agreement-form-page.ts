import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  afterNextRender,
  ElementRef,
  Injector,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { CurrentOrganizationStore } from '../../core/session/current-organization.store';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { LimitReached } from '../../shared/ui/limit-reached';
import { ErrorState } from '../../shared/ui/states';
import { ClientOption, ClientsApi } from '../clients/clients.api';
import { JobPriority, jobPriorities, priorityLabels } from '../jobs/jobs.api';
import { Member, MembersApi } from '../members/members.api';
import {
  Agreement,
  AgreementInput,
  AgreementsApi,
  RecurrenceInput,
  RecurrenceKind,
  WeekOfMonth,
  Weekday,
  weekLabels,
  weekdayLabels,
  weekdays,
} from './agreements.api';

type EndKind = 'none' | 'date' | 'count';

/** A row of the visit checklist being edited. */
interface ChecklistLine {
  readonly key: number;
  readonly text: string;
  readonly required: boolean;
}

const messages = {
  clientId: { required: 'Choose a client.' },
  assigneeUserId: {},
  title: {
    required: 'Enter a title.',
    minlength: 'Use at least 2 characters.',
    maxlength: 'Use at most 120 characters.',
  },
  quotedAmount: { min: 'Enter an amount of 0 or more.', max: 'Use at most 1,000,000.00.' },
  estimatedMinutes: { min: 'Enter at least 1 minute.', max: 'Use at most 1,440 minutes (a day).' },
  description: { maxlength: 'Use at most 5,000 characters.' },
  startsOn: { required: 'Choose the date of the first visit.' },
  endsOn: { required: 'Choose the end date.' },
  maxVisits: {
    required: 'Enter how many visits.',
    min: 'At least 1 visit.',
    max: 'At most 500 visits.',
  },
} as const;

/**
 * Today as a date (YYYY-MM-DD) in the organization's time zone, which is what the API checks the start against; the
 * browser's own zone until the organization has loaded. A manager far from the organization would otherwise get a
 * default the API refuses (found in the M16 review).
 */
function todayIn(timeZone?: string): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(new Date());
}

/**
 * US-70, US-71: create or edit an agreement. The recurrence editor shows only the fields of the chosen pattern, and
 * a live preview asks the API for the next eight dates as the schedule changes (POST /agreements/preview), so the
 * user sees what will be generated before saving. The client is chosen once, on creation.
 */
@Component({
  selector: 'app-agreement-form-page',
  imports: [ReactiveFormsModule, RouterLink, DatePipe, ErrorState, LimitReached],
  template: `
    <div class="mx-auto max-w-3xl space-y-5">
      <a [routerLink]="id() ? '..' : '../'" class="text-sm text-muted hover:underline">
        ← {{ id() ? 'Agreement' : 'Agreements' }}
      </a>
      <h1 class="text-2xl font-semibold tracking-tight">
        {{ id() ? 'Edit agreement' : 'New agreement' }}
      </h1>

      @if (!canManage()) {
        <p class="text-muted">Only Admins and Owners set up and change agreements.</p>
      } @else if (loadError(); as error) {
        <app-error-state [message]="error.title" (retry)="reload()" />
      } @else if (loading()) {
        <div class="h-96 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else {
        <form #formEl class="space-y-5" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (limit(); as problem) {
            <app-limit-reached [problem]="problem" [slug]="slug()" />
          } @else if (formError(); as message) {
            <div class="th-alert-error" role="alert">{{ message }}</div>
          }

          <section class="th-card space-y-5" aria-labelledby="visit-heading">
            <h2 id="visit-heading" class="font-semibold">Each visit</h2>

            @if (agreement(); as a) {
              <p class="text-sm"><span class="text-muted">Client:</span> {{ a.clientName }}</p>
            } @else {
              <div>
                <label class="th-label" for="clientId">Client</label>
                <select
                  id="clientId"
                  class="th-input"
                  formControlName="clientId"
                  [attr.aria-invalid]="!!error('clientId')"
                  [attr.aria-describedby]="error('clientId') ? 'clientId-error' : null"
                >
                  <option value="">
                    {{ options() === 'loading' ? 'Loading clients…' : 'Choose a client' }}
                  </option>
                  @for (client of activeClients(); track client.id) {
                    <option [value]="client.id">{{ client.name }}</option>
                  }
                </select>
                @if (error('clientId'); as message) {
                  <p id="clientId-error" class="th-field-error">{{ message }}</p>
                }
                <!-- As on the job form (M10): a failure is announced, and an empty list says why. -->
                @if (options() === 'error') {
                  <div
                    class="th-alert-error mt-2 flex flex-wrap items-center justify-between gap-3"
                    role="alert"
                  >
                    <span>The clients and people could not be loaded.</span>
                    <button type="button" class="th-button-secondary" (click)="loadOptions()">
                      Try again
                    </button>
                  </div>
                } @else if (options() === 'ready' && activeClients().length === 0) {
                  <p class="th-hint">
                    No active clients yet.
                    <a
                      [routerLink]="['/o', slug(), 'clients', 'new']"
                      class="font-medium text-accent underline-offset-2 hover:underline"
                      >Add a client</a
                    >
                    first, then set up the agreement.
                  </p>
                }
              </div>
            }

            <div>
              <label class="th-label" for="title">Title</label>
              <input
                id="title"
                class="th-input"
                formControlName="title"
                placeholder="Weekly office cleaning"
                [attr.aria-invalid]="!!error('title')"
                [attr.aria-describedby]="error('title') ? 'title-error' : null"
              />
              @if (error('title'); as message) {
                <p id="title-error" class="th-field-error">{{ message }}</p>
              }
            </div>

            <div class="grid gap-5 sm:grid-cols-2">
              <div>
                <label class="th-label" for="assigneeUserId">Assignee</label>
                <select
                  id="assigneeUserId"
                  class="th-input"
                  formControlName="assigneeUserId"
                  aria-describedby="assignee-hint"
                >
                  <option value="">Unassigned</option>
                  @for (member of members(); track member.userId) {
                    <option [value]="member.userId">{{ member.displayName }}</option>
                  }
                </select>
                <p id="assignee-hint" class="th-hint">
                  With an assignee, visits are Scheduled; without, they are New.
                </p>
                @if (error('assigneeUserId'); as message) {
                  <p class="th-field-error">{{ message }}</p>
                }
              </div>
              <div>
                <label class="th-label" for="priority">Priority</label>
                <select id="priority" class="th-input" formControlName="priority">
                  @for (priority of priorities; track priority) {
                    <option [value]="priority">{{ priorityLabels[priority] }}</option>
                  }
                </select>
              </div>
              <div>
                <label class="th-label" for="quotedAmount">
                  Quoted per visit, EUR <span class="font-normal text-muted">(optional)</span>
                </label>
                <input
                  id="quotedAmount"
                  type="number"
                  inputmode="decimal"
                  min="0"
                  step="0.01"
                  class="th-input"
                  formControlName="quotedAmount"
                  [attr.aria-invalid]="!!error('quotedAmount')"
                  [attr.aria-describedby]="error('quotedAmount') ? 'quotedAmount-error' : null"
                />
                @if (error('quotedAmount'); as message) {
                  <p id="quotedAmount-error" class="th-field-error">{{ message }}</p>
                }
              </div>
              <div>
                <label class="th-label" for="estimatedMinutes">
                  Estimated time, minutes <span class="font-normal text-muted">(optional)</span>
                </label>
                <input
                  id="estimatedMinutes"
                  type="number"
                  inputmode="numeric"
                  min="1"
                  max="1440"
                  class="th-input"
                  formControlName="estimatedMinutes"
                  [attr.aria-invalid]="!!error('estimatedMinutes')"
                  [attr.aria-describedby]="
                    error('estimatedMinutes') ? 'estimatedMinutes-error' : null
                  "
                />
                @if (error('estimatedMinutes'); as message) {
                  <p id="estimatedMinutes-error" class="th-field-error">{{ message }}</p>
                }
              </div>
            </div>

            <div>
              <label class="th-label" for="description">
                Description <span class="font-normal text-muted">(optional)</span>
              </label>
              <textarea
                id="description"
                rows="3"
                class="th-input"
                formControlName="description"
                [attr.aria-invalid]="!!error('description')"
              ></textarea>
              @if (error('description'); as message) {
                <p class="th-field-error">{{ message }}</p>
              }
            </div>
          </section>

          <section class="th-card space-y-3" aria-labelledby="checklist-heading">
            <h2 id="checklist-heading" class="font-semibold">Visit checklist</h2>
            <p class="th-hint">
              Each visit starts with these items. Required ones must be ticked before a visit is
              completed. Upcoming visits nobody has started follow changes here.
            </p>
            <p class="sr-only" aria-live="polite" aria-atomic="true">
              {{ checklistAnnouncement() }}
            </p>
            @if (checklistError('checklist'); as message) {
              <p class="th-field-error" role="alert">{{ message }}</p>
            }
            @if (checklist().length === 0) {
              <p class="text-sm text-muted">No checklist: visits start without one.</p>
            } @else {
              <ol class="space-y-2">
                @for (
                  line of checklist();
                  track line.key;
                  let i = $index;
                  let first = $first;
                  let last = $last
                ) {
                  <li class="flex flex-wrap items-start gap-2">
                    <div class="min-w-0 flex-1">
                      <label class="sr-only" [for]="'line-' + line.key">Item {{ i + 1 }}</label>
                      <input
                        class="th-input"
                        [id]="'line-' + line.key"
                        maxlength="200"
                        [value]="line.text"
                        [attr.aria-invalid]="!!checklistError('checklist[' + i + '].text')"
                        (input)="setLine(i, { text: $any($event.target).value })"
                      />
                      @if (checklistError('checklist[' + i + '].text'); as message) {
                        <p class="th-field-error">{{ message }}</p>
                      }
                    </div>
                    <label class="flex h-10 items-center gap-1 text-sm">
                      <input
                        type="checkbox"
                        class="size-4 accent-accent"
                        [checked]="line.required"
                        (change)="setLine(i, { required: $any($event.target).checked })"
                      />
                      Required
                    </label>
                    <div class="flex gap-1">
                      <button
                        type="button"
                        class="th-icon-button"
                        [disabled]="first"
                        [attr.aria-label]="'Move item ' + (i + 1) + ' up'"
                        (click)="moveLine(i, -1)"
                      >
                        ↑
                      </button>
                      <button
                        type="button"
                        class="th-icon-button"
                        [disabled]="last"
                        [attr.aria-label]="'Move item ' + (i + 1) + ' down'"
                        (click)="moveLine(i, 1)"
                      >
                        ↓
                      </button>
                      <button
                        type="button"
                        class="th-icon-button text-danger"
                        [attr.aria-label]="'Remove item ' + (i + 1)"
                        (click)="removeLine(i)"
                      >
                        ✕
                      </button>
                    </div>
                  </li>
                }
              </ol>
            }
            @if (checklist().length < maxChecklistItems) {
              <button type="button" class="th-button-secondary" (click)="addLine()">
                Add an item
              </button>
            }
          </section>

          <section class="th-card space-y-5" aria-labelledby="schedule-heading">
            <h2 id="schedule-heading" class="font-semibold">Schedule</h2>
            <div class="grid gap-5 sm:grid-cols-2" formGroupName="recurrence">
              <div>
                <label class="th-label" for="kind">Repeats</label>
                <select id="kind" class="th-input" formControlName="kind">
                  <option value="weekly">Weekly, on chosen days</option>
                  <option value="monthlyByDay">Monthly, on a day of the month</option>
                  <option value="monthlyByWeekday">
                    Monthly, on a weekday (e.g. first Monday)
                  </option>
                </select>
              </div>
              <div>
                <label class="th-label" for="interval">
                  Every how many {{ kind() === 'weekly' ? 'weeks' : 'months' }}
                </label>
                <input
                  id="interval"
                  type="number"
                  inputmode="numeric"
                  min="1"
                  [max]="kind() === 'weekly' ? 8 : 12"
                  class="th-input"
                  formControlName="interval"
                  [attr.aria-invalid]="!!recurrenceError('interval')"
                  [attr.aria-describedby]="recurrenceError('interval') ? 'interval-error' : null"
                />
                @if (recurrenceError('interval'); as message) {
                  <p id="interval-error" class="th-field-error">{{ message }}</p>
                }
              </div>

              @switch (kind()) {
                @case ('weekly') {
                  <fieldset
                    class="sm:col-span-2"
                    formGroupName="weekdays"
                    [attr.aria-describedby]="recurrenceError('weekdays') ? 'weekdays-error' : null"
                  >
                    <legend class="th-label">On</legend>
                    <div class="mt-1 flex flex-wrap gap-2">
                      @for (day of weekdays; track day) {
                        <label
                          class="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-sm"
                        >
                          <input type="checkbox" [formControlName]="day" />
                          {{ weekdayLabels[day] }}
                        </label>
                      }
                    </div>
                    @if (recurrenceError('weekdays'); as message) {
                      <p id="weekdays-error" class="th-field-error">{{ message }}</p>
                    }
                  </fieldset>
                }
                @case ('monthlyByDay') {
                  <div>
                    <label class="th-label" for="day">On day</label>
                    <select
                      id="day"
                      class="th-input"
                      formControlName="day"
                      aria-describedby="day-hint"
                    >
                      @for (day of days; track day) {
                        <option [value]="day">{{ day }}</option>
                      }
                      <option value="last">The last day of the month</option>
                    </select>
                    <p id="day-hint" class="th-hint">
                      Days 29–31 do not exist in every month; "the last day" covers month ends.
                    </p>
                  </div>
                }
                @case ('monthlyByWeekday') {
                  <div>
                    <label class="th-label" for="week">Which</label>
                    <select id="week" class="th-input" formControlName="week">
                      @for (week of weeks; track week) {
                        <option [value]="week">{{ weekLabels[week] }}</option>
                      }
                    </select>
                  </div>
                  <div>
                    <label class="th-label" for="weekday">Weekday</label>
                    <select id="weekday" class="th-input" formControlName="weekday">
                      @for (day of weekdays; track day) {
                        <option [value]="day">{{ weekdayLabels[day] }}</option>
                      }
                    </select>
                  </div>
                }
              }
            </div>

            <div class="grid gap-5 sm:grid-cols-2">
              <div>
                <label class="th-label" for="startsOn">First visit on or after</label>
                <input
                  id="startsOn"
                  type="date"
                  class="th-input"
                  formControlName="startsOn"
                  [attr.aria-invalid]="!!error('startsOn')"
                  [attr.aria-describedby]="error('startsOn') ? 'startsOn-error' : null"
                />
                @if (error('startsOn'); as message) {
                  <p id="startsOn-error" class="th-field-error">{{ message }}</p>
                }
              </div>
              <fieldset>
                <legend class="th-label">Ends</legend>
                <div class="mt-1 space-y-2 text-sm">
                  <label class="flex items-center gap-2">
                    <input type="radio" formControlName="endKind" value="none" /> Never
                  </label>
                  <label class="flex flex-wrap items-center gap-2">
                    <input type="radio" formControlName="endKind" value="date" /> On a date
                    @if (endKind() === 'date') {
                      <input
                        type="date"
                        class="th-input w-auto"
                        formControlName="endsOn"
                        aria-label="End date"
                        [attr.aria-invalid]="!!error('endsOn')"
                      />
                    }
                  </label>
                  <label class="flex flex-wrap items-center gap-2">
                    <input type="radio" formControlName="endKind" value="count" /> After
                    @if (endKind() === 'count') {
                      <input
                        type="number"
                        min="1"
                        max="500"
                        class="th-input w-24"
                        formControlName="maxVisits"
                        aria-label="Number of visits"
                        [attr.aria-invalid]="!!error('maxVisits')"
                      />
                      visits
                    }
                  </label>
                </div>
                @if (error('endsOn') ?? error('maxVisits'); as message) {
                  <p class="th-field-error">{{ message }}</p>
                }
              </fieldset>
            </div>

            <div aria-live="polite" aria-labelledby="preview-heading">
              <h3 id="preview-heading" class="text-sm font-semibold">Next visits</h3>
              @if (preview(); as dates) {
                @if (dates.length === 0) {
                  <p class="mt-1 text-sm text-muted">No visit dates in the next five years.</p>
                } @else {
                  <ol class="mt-2 grid gap-1 text-sm sm:grid-cols-2">
                    @for (date of dates; track date) {
                      <li>{{ date | date: 'EEEE d MMMM y' }}</li>
                    }
                  </ol>
                }
              } @else {
                <p class="mt-1 text-sm text-muted">{{ previewNote() }}</p>
              }
            </div>
          </section>

          <div class="flex flex-wrap gap-3">
            <button type="submit" class="th-button" [disabled]="saving()">
              {{ saving() ? 'Saving…' : id() ? 'Save changes' : 'Create agreement' }}
            </button>
            <a [routerLink]="id() ? '..' : '../'" class="th-button-secondary">Cancel</a>
          </div>
          @if (id()) {
            <p class="text-sm text-muted">
              Saving updates the visits from today on that nobody has changed by hand.
            </p>
          }
        </form>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgreementFormPage {
  readonly slug = input.required<string>();
  /** Set when editing (route parameter). */
  readonly id = input<string>();
  /** Query parameter: the client page's "New agreement" chooses its client. */
  readonly clientId = input<string>();

  private readonly api = inject(AgreementsApi);
  private readonly clientsApi = inject(ClientsApi);
  private readonly membersApi = inject(MembersApi);
  private readonly session = inject(SessionStore);
  private readonly currentOrganization = inject(CurrentOrganizationStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));

  protected readonly canManage = computed(() => this.session.can(this.slug(), 'agreements.manage'));
  protected readonly weekdays = weekdays;
  protected readonly weekdayLabels = weekdayLabels;
  protected readonly weeks: readonly WeekOfMonth[] = ['first', 'second', 'third', 'fourth', 'last'];
  protected readonly weekLabels = weekLabels;
  protected readonly days = Array.from({ length: 28 }, (_, i) => i + 1);
  protected readonly priorities = jobPriorities;
  protected readonly priorityLabels = priorityLabels;

  protected readonly clients = signal<ClientOption[]>([]);
  protected readonly members = signal<Member[]>([]);
  protected readonly activeClients = computed(() =>
    this.clients().filter((c) => c.status === 'active'),
  );
  protected readonly options = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly agreement = signal<Agreement | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly limit = signal<ApiProblem | null>(null);
  protected readonly preview = signal<readonly string[] | null>(null);
  protected readonly previewNote = signal('Choose the days to see the dates.');

  /** The visit checklist (SPEC-V2 4.5), edited beside the form; `key` keeps each row's input across reorders. */
  protected readonly checklist = signal<readonly ChecklistLine[]>([]);
  protected readonly checklistErrors = signal<Readonly<Record<string, readonly string[]>>>({});
  protected readonly checklistAnnouncement = signal('');
  protected readonly maxChecklistItems = 30;
  private nextKey = 0;

  protected readonly form = inject(NonNullableFormBuilder).group({
    clientId: ['', Validators.required],
    title: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(120)]],
    description: ['', Validators.maxLength(5000)],
    assigneeUserId: [''],
    priority: ['normal' as JobPriority],
    quotedAmount: [null as number | null, [Validators.min(0), Validators.max(1_000_000)]],
    estimatedMinutes: [null as number | null, [Validators.min(1), Validators.max(1440)]],
    recurrence: inject(NonNullableFormBuilder).group({
      kind: ['weekly' as RecurrenceKind],
      interval: [1 as number | null],
      weekdays: inject(NonNullableFormBuilder).group(
        Object.fromEntries(weekdays.map((d) => [d, false])) as Record<Weekday, boolean>,
      ),
      day: ['1'],
      week: ['first' as WeekOfMonth],
      weekday: ['monday' as Weekday],
    }),
    startsOn: [todayIn(), Validators.required],
    endKind: ['none' as EndKind],
    endsOn: [''],
    maxVisits: [null as number | null],
  });

  private readonly value = toSignal(this.form.valueChanges, {
    initialValue: this.form.getRawValue(),
  });
  protected readonly kind = computed(() => this.value().recurrence?.kind ?? 'weekly');
  protected readonly endKind = computed(() => this.value().endKind ?? 'none');
  /** Only these fields change the dates; the preview follows them and nothing else (not the title, for example). */
  private readonly scheduleKey = computed(() => {
    const v = this.value();
    return JSON.stringify([v.recurrence, v.startsOn, v.endKind, v.endsOn, v.maxVisits]);
  });
  private version = 0;
  private previewTimer: ReturnType<typeof setTimeout> | undefined;
  private previewRequest = 0;

  constructor() {
    effect(() => {
      if (!this.membership() || !this.canManage()) {
        return;
      }
      void this.loadOptions();
      if (this.id()) {
        void this.load();
      }
    });

    effect(() => {
      const clientId = this.clientId();
      if (clientId && !this.id()) {
        this.form.controls.clientId.setValue(clientId);
      }
    });

    // A new agreement starts on the organization's today, unless the user already chose a date.
    effect(() => {
      const organization = this.currentOrganization.organization();
      const startsOn = this.form.controls.startsOn;
      if (
        organization &&
        organization.id === this.membership()?.organizationId &&
        !this.id() &&
        !startsOn.dirty
      ) {
        startsOn.setValue(todayIn(organization.timeZone));
      }
    });

    // The live preview follows the schedule fields, after a short pause in typing.
    effect(() => {
      this.scheduleKey();
      if (!this.membership()) {
        return;
      }
      clearTimeout(this.previewTimer);
      this.previewTimer = setTimeout(() => void this.refreshPreview(), 300);
    });

    inject(DestroyRef).onDestroy(() => clearTimeout(this.previewTimer));
  }

  protected error(field: keyof typeof messages): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  protected recurrenceError(field: 'interval' | 'weekdays'): string | null {
    return controlError(this.form.controls.recurrence.controls[field], {});
  }

  protected async loadOptions(): Promise<void> {
    const organizationId = this.membership()!.organizationId;
    this.options.set('loading');
    try {
      const [members, clients] = await Promise.all([
        this.membersApi.members(organizationId),
        this.clientsApi.options(organizationId),
      ]);
      this.members.set(members);
      this.clients.set(clients);
      this.options.set('ready');
    } catch {
      this.options.set('error');
    }
  }

  protected reload(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const a = await this.api.get(this.membership()!.organizationId, this.id()!);
      this.agreement.set(a);
      this.version = a.version;
      const r = a.recurrence;
      this.form.reset({
        clientId: a.clientId,
        title: a.title,
        description: a.description ?? '',
        assigneeUserId: a.assigneeUserId ?? '',
        priority: a.priority,
        quotedAmount: a.quotedAmount,
        estimatedMinutes: a.estimatedMinutes,
        recurrence: {
          kind: r.kind,
          interval: r.interval,
          weekdays: Object.fromEntries(weekdays.map((d) => [d, r.weekdays.includes(d)])) as Record<
            Weekday,
            boolean
          >,
          day: r.lastDay ? 'last' : String(r.day ?? 1),
          week: r.week ?? 'first',
          weekday: r.weekday ?? 'monday',
        },
        startsOn: a.startsOn,
        endKind: a.maxVisits !== null ? 'count' : a.endsOn ? 'date' : 'none',
        endsOn: a.endsOn ?? '',
        maxVisits: a.maxVisits,
      });
      this.checklist.set(
        a.checklist.map((e) => ({ key: this.nextKey++, text: e.text, required: e.required })),
      );
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  /** The recurrence as the API takes it: only the fields of the chosen pattern. */
  private recurrenceInput(): RecurrenceInput {
    const r = this.form.controls.recurrence.getRawValue();
    switch (r.kind) {
      case 'weekly':
        return {
          kind: 'weekly',
          interval: r.interval,
          weekdays: weekdays.filter((d) => r.weekdays[d]),
        };
      case 'monthlyByDay':
        return r.day === 'last'
          ? { kind: 'monthlyByDay', interval: r.interval, lastDay: true }
          : { kind: 'monthlyByDay', interval: r.interval, day: Number(r.day) };
      default:
        return { kind: 'monthlyByWeekday', interval: r.interval, week: r.week, weekday: r.weekday };
    }
  }

  private ends(): { endsOn: string | null; maxVisits: number | null } {
    const value = this.form.getRawValue();
    return {
      endsOn: value.endKind === 'date' ? value.endsOn || null : null,
      maxVisits: value.endKind === 'count' ? value.maxVisits : null,
    };
  }

  private async refreshPreview(): Promise<void> {
    const request = ++this.previewRequest;
    const recurrence = this.recurrenceInput();
    if (recurrence.kind === 'weekly' && recurrence.weekdays?.length === 0) {
      this.preview.set(null);
      this.previewNote.set('Choose at least one day to see the dates.');
      return;
    }
    try {
      const dates = await this.api.preview(this.membership()!.organizationId, {
        recurrence,
        startsOn: this.form.controls.startsOn.value || null,
        ...this.ends(),
      });
      if (request === this.previewRequest) {
        this.preview.set(dates);
      }
    } catch (error) {
      if (request === this.previewRequest) {
        this.preview.set(null);
        this.previewNote.set(
          toApiProblem(error).status === 400
            ? 'Complete the schedule to see the dates.'
            : 'The dates could not be shown just now.',
        );
      }
    }
  }

  protected async save(): Promise<void> {
    this.formError.set(null);
    this.limit.set(null);
    const recurrence = this.recurrenceInput();
    const weekdaysControl = this.form.controls.recurrence.controls.weekdays;
    if (recurrence.kind === 'weekly' && recurrence.weekdays?.length === 0) {
      weekdaysControl.setErrors({ server: 'Choose at least one weekday.' });
      weekdaysControl.markAsTouched();
    }
    if (this.id()) {
      this.form.controls.clientId.setErrors(null);
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusInvalid();
      return;
    }

    const value = this.form.getRawValue();
    const input: AgreementInput = {
      ...(this.id() ? {} : { clientId: value.clientId || null }),
      title: value.title,
      description: value.description || null,
      assigneeUserId: value.assigneeUserId || null,
      priority: value.priority,
      quotedAmount: value.quotedAmount ?? null,
      estimatedMinutes: value.estimatedMinutes ?? null,
      recurrence,
      startsOn: value.startsOn || null,
      ...this.ends(),
      // Empty lines are dropped rather than refused: an added row left blank is not an item.
      checklist: this.checklist()
        .filter((line) => line.text.trim() !== '')
        .map((line) => ({ text: line.text, required: line.required })),
    };
    this.checklistErrors.set({});
    const organizationId = this.membership()!.organizationId;
    this.saving.set(true);
    try {
      const id = this.id();
      const saved = id
        ? await this.api.update(organizationId, id, input, this.version)
        : await this.api.create(organizationId, input);
      await this.router.navigate(['/o', this.slug(), 'agreements', saved.id]);
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'plan-limit-reached') {
        this.limit.set(problem);
      } else {
        this.checklistErrors.set(
          Object.fromEntries(
            Object.entries(problem.fieldErrors).filter(([field]) => field.startsWith('checklist')),
          ),
        );
        this.formError.set(applyProblem(this.form, problem));
      }
      this.focusInvalid();
    } finally {
      this.saving.set(false);
    }
  }

  protected checklistError(field: string): string | null {
    return this.checklistErrors()[field]?.[0] ?? null;
  }

  protected addLine(): void {
    this.checklist.update((lines) => [...lines, { key: this.nextKey++, text: '', required: true }]);
    const key = this.nextKey - 1;
    afterNextRender(() => document.getElementById(`line-${key}`)?.focus(), {
      injector: this.injector,
    });
  }

  protected setLine(index: number, change: Partial<ChecklistLine>): void {
    this.checklist.update((lines) =>
      lines.map((line, i) => (i === index ? { ...line, ...change } : line)),
    );
  }

  protected moveLine(index: number, by: number): void {
    const lines = [...this.checklist()];
    const [line] = lines.splice(index, 1);
    lines.splice(index + by, 0, line!);
    this.checklist.set(lines);
    this.checklistAnnouncement.set(
      `Moved item ${index + 1} to place ${index + by + 1} of ${lines.length}.`,
    );
  }

  protected removeLine(index: number): void {
    this.checklist.update((lines) => lines.filter((_, i) => i !== index));
    this.checklistAnnouncement.set(`Removed item ${index + 1}.`);
  }

  private focusInvalid(): void {
    const form = this.formEl();
    if (form) {
      focusFirstInvalid(form, this.injector);
    }
  }
}
