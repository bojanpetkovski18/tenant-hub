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
import { LimitReached } from '../../shared/ui/limit-reached';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { ClientOption, ClientsApi } from '../clients/clients.api';
import { Member, MembersApi } from '../members/members.api';
import { Job, JobPriority, JobsApi, jobPriorities, priorityLabels } from './jobs.api';

const messages = {
  title: {
    required: 'Enter a title.',
    minlength: 'Use at least 2 characters.',
    maxlength: 'Use at most 120 characters.',
  },
  clientId: { required: 'Choose a client.' },
  assigneeUserId: {},
  priority: {},
  scheduledOn: {},
  dueOn: {},
  quotedAmount: {
    min: 'Enter an amount of 0 or more.',
    max: 'Use at most 1,000,000.00.',
  },
  description: { maxlength: 'Use at most 5,000 characters.' },
} as const;

type Field = keyof typeof messages;

/**
 * US-30: create a job, or edit one with optimistic concurrency (a conflict offers to reload). Who may assign and
 * schedule comes from the API: the job's `canAssign` when editing, the session's `jobs.manage` when creating. Without
 * it the assignee is "Unassigned" or "Me" on a new job, and fixed on an existing one.
 */
@Component({
  selector: 'app-job-form-page',
  imports: [ReactiveFormsModule, RouterLink, EmptyState, ErrorState, LimitReached],
  template: `
    <a [routerLink]="backLink()" class="text-sm text-muted hover:text-fg">← Back</a>
    <h1 class="mt-3 text-2xl font-semibold tracking-tight">
      {{ id() ? 'Edit ' + (job()?.label ?? 'job') : 'New job' }}
    </h1>

    @if (!allowed()) {
      <div class="mt-5">
        <app-empty-state
          [title]="id() ? 'You cannot change this job' : 'You cannot create jobs'"
          [message]="
            id()
              ? 'Only its assignee, Admins and Owners can.'
              : 'Viewers see jobs but do not create them. Ask an admin if you need to.'
          "
        />
      </div>
    } @else if (loading()) {
      <div class="mt-5 h-96 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
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
        @if (limit(); as problem) {
          <app-limit-reached [problem]="problem" [slug]="slug()" />
        } @else if (formError(); as message) {
          <div class="th-alert-error" role="alert">{{ message }}</div>
        }

        <div>
          <label class="th-label" for="title">Title</label>
          <input
            id="title"
            class="th-input"
            formControlName="title"
            [attr.aria-invalid]="!!error('title')"
            [attr.aria-describedby]="error('title') ? 'title-error' : null"
          />
          @if (error('title'); as message) {
            <p id="title-error" class="th-field-error">{{ message }}</p>
          }
        </div>

        <div>
          <label class="th-label" for="clientId">Client</label>
          <select
            id="clientId"
            class="th-input"
            formControlName="clientId"
            [attr.aria-invalid]="!!error('clientId')"
            [attr.aria-describedby]="error('clientId') ? 'clientId-error' : null"
          >
            <option value="">Choose a client</option>
            @for (client of clientOptions(); track client.id) {
              <option [value]="client.id">
                {{ client.name }}{{ client.status === 'archived' ? ' (archived)' : '' }}
              </option>
            }
          </select>
          @if (error('clientId'); as message) {
            <p id="clientId-error" class="th-field-error">{{ message }}</p>
          }
        </div>

        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="th-label" for="assigneeUserId">Assignee</label>
            <select
              id="assigneeUserId"
              class="th-input"
              formControlName="assigneeUserId"
              [attr.aria-invalid]="!!error('assigneeUserId')"
              [attr.aria-describedby]="
                error('assigneeUserId')
                  ? 'assignee-hint assigneeUserId-error'
                  : canAssign()
                    ? null
                    : 'assignee-hint'
              "
            >
              <option value="">Unassigned</option>
              @for (member of assigneeOptions(); track member.userId) {
                <option [value]="member.userId">
                  {{ member.displayName }}{{ member.isYou ? ' (me)' : '' }}
                </option>
              }
            </select>
            @if (!canAssign()) {
              <p id="assignee-hint" class="th-hint">
                {{
                  id()
                    ? 'Only Admins and Owners reassign jobs.'
                    : 'Leave it unassigned or take it yourself; Admins and Owners assign others.'
                }}
              </p>
            }
            @if (error('assigneeUserId'); as message) {
              <p id="assigneeUserId-error" class="th-field-error">{{ message }}</p>
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
            <label class="th-label" for="scheduledOn">
              Scheduled date <span class="font-normal text-muted">(optional)</span>
            </label>
            <input
              id="scheduledOn"
              type="date"
              class="th-input"
              formControlName="scheduledOn"
              [attr.aria-invalid]="!!error('scheduledOn')"
              [attr.aria-describedby]="
                error('scheduledOn') ? 'scheduledOn-error' : canAssign() ? null : 'scheduled-hint'
              "
            />
            @if (!canAssign()) {
              <p id="scheduled-hint" class="th-hint">Only Admins and Owners schedule jobs.</p>
            }
            @if (error('scheduledOn'); as message) {
              <p id="scheduledOn-error" class="th-field-error">{{ message }}</p>
            }
          </div>

          <div>
            <label class="th-label" for="dueOn">
              Due date <span class="font-normal text-muted">(optional)</span>
            </label>
            <input
              id="dueOn"
              type="date"
              class="th-input"
              formControlName="dueOn"
              [attr.aria-invalid]="!!error('dueOn')"
              [attr.aria-describedby]="error('dueOn') ? 'dueOn-error' : null"
            />
            @if (error('dueOn'); as message) {
              <p id="dueOn-error" class="th-field-error">{{ message }}</p>
            }
          </div>

          <div>
            <label class="th-label" for="quotedAmount">
              Quoted amount, EUR <span class="font-normal text-muted">(optional)</span>
            </label>
            <input
              id="quotedAmount"
              type="number"
              inputmode="decimal"
              min="0"
              max="1000000"
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
        </div>

        <div>
          <label class="th-label" for="description">
            Description <span class="font-normal text-muted">(optional)</span>
          </label>
          <textarea
            id="description"
            class="th-input min-h-28 py-2"
            formControlName="description"
            [attr.aria-invalid]="!!error('description')"
            [attr.aria-describedby]="error('description') ? 'description-error' : null"
          ></textarea>
          @if (error('description'); as message) {
            <p id="description-error" class="th-field-error">{{ message }}</p>
          }
        </div>

        <div class="flex flex-wrap gap-2">
          <button type="submit" class="th-button" [disabled]="saving()">
            {{ saving() ? 'Saving…' : id() ? 'Save changes' : 'Create job' }}
          </button>
          <a [routerLink]="backLink()" class="th-button-secondary">Cancel</a>
        </div>
      </form>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobFormPage {
  readonly slug = input.required<string>();
  /** Present when editing (route jobs/:id/edit). */
  readonly id = input<string>();
  /** Query parameter: preselects the client when coming from a client's page. */
  readonly clientId = input<string>();

  private readonly api = inject(JobsApi);
  private readonly membersApi = inject(MembersApi);
  private readonly clientsApi = inject(ClientsApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');

  protected readonly priorities = jobPriorities;
  protected readonly priorityLabels = priorityLabels;
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly job = signal<Job | null>(null);
  private readonly members = signal<Member[]>([]);
  private readonly clients = signal<ClientOption[]>([]);

  /** Editing: the API's verdict on this job; creating: the session's permission (D008). */
  protected readonly canAssign = computed(() =>
    this.id() ? (this.job()?.canAssign ?? false) : this.session.can(this.slug(), 'jobs.manage'),
  );
  protected readonly allowed = computed(() =>
    this.id() ? (this.job()?.canEdit ?? true) : this.session.can(this.slug(), 'jobs.create'),
  );
  protected readonly backLink = computed(() =>
    this.id() ? ['/o', this.slug(), 'jobs', this.id()!] : ['/o', this.slug(), 'jobs'],
  );

  /** Active clients, plus the job's current one if it was archived since (it stays valid for this job). */
  protected readonly clientOptions = computed(() =>
    this.clients().filter((c) => c.status === 'active' || c.id === this.job()?.clientId),
  );

  /** Everyone for a manager; otherwise only "me" on a new job, and the current assignee on an existing one. */
  protected readonly assigneeOptions = computed(() => {
    const members = this.members();
    if (this.canAssign()) {
      return members;
    }
    const current = this.job()?.assigneeUserId;
    return this.id() ? members.filter((m) => m.userId === current) : members.filter((m) => m.isYou);
  });

  protected readonly form = inject(NonNullableFormBuilder).group({
    title: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(120)]],
    clientId: ['', Validators.required],
    assigneeUserId: [''],
    priority: ['normal' as JobPriority],
    scheduledOn: [''],
    dueOn: [''],
    quotedAmount: [null as number | null, [Validators.min(0), Validators.max(1_000_000)]],
    description: ['', Validators.maxLength(5000)],
  });
  private version = 0;
  protected readonly loading = signal(false);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly limit = signal<ApiProblem | null>(null);

  constructor() {
    effect(() => {
      const membership = this.membership();
      if (!membership) {
        return;
      }
      void this.membersApi.members(membership.organizationId).then(
        (members) => this.members.set(members),
        () => this.members.set([]),
      );
      void this.clientsApi.options(membership.organizationId).then(
        (clients) => this.clients.set(clients),
        () => this.clients.set([]),
      );
      if (this.id()) {
        void this.load();
      } else if (this.clientId()) {
        this.form.controls.clientId.setValue(this.clientId()!);
      }
    });

    // Fields the caller may not change stay visible but locked; their values still go back unchanged.
    effect(() => {
      const locked = !this.canAssign();
      for (const control of [this.form.controls.scheduledOn]) {
        if (locked) control.disable({ emitEvent: false });
        else control.enable({ emitEvent: false });
      }
      if (locked && this.id()) this.form.controls.assigneeUserId.disable({ emitEvent: false });
      else this.form.controls.assigneeUserId.enable({ emitEvent: false });
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
    this.limit.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusInvalid();
      return;
    }

    const organizationId = this.membership()!.organizationId;
    const value = this.form.getRawValue();
    const input = {
      title: value.title,
      description: value.description || null,
      clientId: value.clientId || null,
      assigneeUserId: value.assigneeUserId || null,
      priority: value.priority,
      scheduledOn: value.scheduledOn || null,
      dueOn: value.dueOn || null,
      quotedAmount: value.quotedAmount ?? null,
    };
    this.saving.set(true);
    try {
      const id = this.id();
      const saved = id
        ? await this.api.update(organizationId, id, input, this.version)
        : await this.api.create(organizationId, input);
      // The detail page confirms the save (the success state; a screen reader hears it as a status).
      await this.router.navigate(['/o', this.slug(), 'jobs', saved.id], {
        queryParams: { notice: id ? 'saved' : 'created' },
      });
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'concurrency-conflict') {
        await this.resolveConflict();
        return;
      }
      if (problem.code === 'plan-limit-reached') {
        this.limit.set(problem);
        return;
      }
      this.formError.set(applyProblem(this.form, problem));
      this.focusInvalid();
    } finally {
      this.saving.set(false);
    }
  }

  /** Someone else saved first: reload their version, or keep these edits (the next save will conflict again). */
  private async resolveConflict(): Promise<void> {
    const reload = await this.confirm.ask({
      title: 'This job was changed by someone else',
      message:
        'They saved after you opened this form. Reload to see their version (your edits here will be lost), or keep editing.',
      confirmLabel: 'Reload their version',
      cancelLabel: 'Keep editing',
    });
    if (reload) {
      await this.load();
    } else {
      this.formError.set('Not saved: someone else changed this job. Reload before saving again.');
    }
  }

  private fill(job: Job): void {
    this.job.set(job);
    this.version = job.version;
    this.form.reset({
      title: job.title,
      clientId: job.clientId,
      assigneeUserId: job.assigneeUserId ?? '',
      priority: job.priority,
      scheduledOn: job.scheduledOn ?? '',
      dueOn: job.dueOn ?? '',
      quotedAmount: job.quotedAmount,
      description: job.description ?? '',
    });
  }

  private focusInvalid(): void {
    const form = this.formEl();
    if (form) {
      focusFirstInvalid(form, this.injector);
    }
  }
}
