import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiProblem, LimitName, PlanLimitDetail, toApiProblem } from '../../core/http/api-problem';
import { SessionStore } from '../../core/session/session.store';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { timeZoneOptions } from '../../shared/forms/time-zones';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import {
  OrganizationOverview,
  PlanLimits,
  PlanName,
  SettingsApi,
  limitLabels,
  limitNames,
  planLabels,
} from './settings.api';

const messages = {
  name: {
    required: 'Enter a name.',
    minlength: 'Use at least 2 characters.',
    maxlength: 'Use at most 80 characters.',
  },
  timeZone: { required: 'Choose a time zone.' },
} as const;

/** "3 of 3 seats", or "12 active clients, no limit". */
export function usageText(limit: LimitName, used: number, allowed: number | null): string {
  const unit = limitLabels[limit].unit;
  return allowed === null
    ? `${used} ${unit(used)}, no limit`
    : `${used} of ${allowed} ${unit(allowed)}`;
}

/** One line per limit that blocks a downgrade: "Seats: 4 in use, Free allows 3". */
export function blockingText(detail: PlanLimitDetail, target: string): string {
  return `${limitLabels[detail.limit].title}: ${detail.usage} in use, ${target} allows ${detail.allowed}`;
}

/**
 * US-50 and US-51: General (name and time zone, Admins and Owners), Plan and usage (meters for everyone who reaches
 * Settings, plan changes for Owners) and the Danger zone (delete, Owners, after typing the name). Each section shows
 * only what the session's permissions allow; the API checks again.
 */
@Component({
  selector: 'app-settings-page',
  imports: [ReactiveFormsModule, EmptyState, ErrorState],
  template: `
    <div class="max-w-3xl space-y-6">
      <h1 class="text-2xl font-semibold tracking-tight">Settings</h1>

      @if (!canManage() && !canBill()) {
        <app-empty-state
          title="Settings are for Admins and Owners"
          message="Ask one of them if something about the organization needs to change."
        />
      } @else if (loadError(); as error) {
        <app-error-state [message]="error.title" (retry)="load()" />
      } @else if (!overview()) {
        <div class="h-96 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else {
        @let org = overview()!;
        @if (canManage()) {
          <section class="th-card" aria-labelledby="general-heading">
            <h2 id="general-heading" class="text-lg font-semibold">General</h2>
            <form #formEl class="mt-4 space-y-5" [formGroup]="form" (ngSubmit)="save()" novalidate>
              @if (formError(); as message) {
                <div class="th-alert-error" role="alert">{{ message }}</div>
              }
              @if (saved()) {
                <div class="th-alert-success" role="status">Settings saved.</div>
              }
              <div>
                <label class="th-label" for="org-name">Organization name</label>
                <input
                  id="org-name"
                  class="th-input"
                  formControlName="name"
                  [attr.aria-invalid]="!!error('name')"
                  [attr.aria-describedby]="error('name') ? 'org-name-error' : 'org-name-hint'"
                />
                <p id="org-name-hint" class="th-hint">
                  The address stays /o/{{ org.slug }}, even after a rename.
                </p>
                @if (error('name'); as message) {
                  <p id="org-name-error" class="th-field-error">{{ message }}</p>
                }
              </div>
              <div>
                <label class="th-label" for="org-zone">Time zone</label>
                <select
                  id="org-zone"
                  class="th-input"
                  formControlName="timeZone"
                  [attr.aria-invalid]="!!error('timeZone')"
                  [attr.aria-describedby]="error('timeZone') ? 'org-zone-error' : 'org-zone-hint'"
                >
                  @for (zone of timeZones; track zone) {
                    <option [value]="zone">{{ zone }}</option>
                  }
                </select>
                <p id="org-zone-hint" class="th-hint">
                  "Overdue" and "this month" follow this zone.
                </p>
                @if (error('timeZone'); as message) {
                  <p id="org-zone-error" class="th-field-error">{{ message }}</p>
                }
              </div>
              <button type="submit" class="th-button" [disabled]="saving()">
                {{ saving() ? 'Saving…' : 'Save changes' }}
              </button>
            </form>
          </section>
        }

        <section id="plan" class="th-card scroll-mt-20" aria-labelledby="plan-heading">
          <div class="flex flex-wrap items-baseline justify-between gap-2">
            <h2 id="plan-heading" class="text-lg font-semibold">Plan and usage</h2>
            <p class="text-sm text-muted">
              Current plan: <span class="font-semibold text-fg">{{ planLabels[org.plan] }}</span>
            </p>
          </div>

          <div class="mt-4 space-y-4">
            @for (limit of limitNames; track limit) {
              <div>
                <div class="flex flex-wrap items-baseline justify-between gap-2 text-sm">
                  <span class="font-medium" [id]="'meter-' + limit">{{
                    limitLabels[limit].title
                  }}</span>
                  <span class="text-muted">
                    {{ usage(limit, org) }}
                  </span>
                </div>
                @if (org.limits[limit] !== null) {
                  <div
                    role="meter"
                    class="mt-1.5 h-2 overflow-hidden rounded-full bg-surface-2"
                    aria-valuemin="0"
                    [attr.aria-valuemax]="org.limits[limit]"
                    [attr.aria-valuenow]="org.usage[limit]"
                    [attr.aria-labelledby]="'meter-' + limit"
                    [attr.aria-valuetext]="usage(limit, org)"
                  >
                    <div [class]="bar(limit, org)"></div>
                  </div>
                }
              </div>
            }
          </div>

          @if (planError(); as problem) {
            <div class="th-alert-error mt-5" role="alert">
              <p class="font-medium">You cannot move to {{ planLabels[problem.target] }} yet.</p>
              <ul class="mt-1 list-disc pl-5">
                @for (detail of problem.blocking; track detail.limit) {
                  <li>{{ blocking(detail, planLabels[problem.target]) }}</li>
                }
              </ul>
              <p class="mt-1 text-muted">Archive clients, close jobs or remove members first.</p>
            </div>
          }
          @if (planDone(); as message) {
            <div class="th-alert-success mt-5" role="status">{{ message }}</div>
          }

          <ul class="mt-5 grid gap-3 sm:grid-cols-3" aria-label="Plans">
            @for (option of org.plans; track option.plan) {
              <li
                class="flex flex-col rounded-lg border p-4"
                [class.border-accent]="option.plan === org.plan"
                [class.border-border]="option.plan !== org.plan"
              >
                <h3 class="font-semibold">
                  {{ planLabels[option.plan] }}
                  @if (option.plan === org.plan) {
                    <span class="th-badge ml-1 bg-accent-soft text-fg">Current</span>
                  }
                </h3>
                <ul class="mt-2 flex-1 space-y-1 text-sm text-muted">
                  @for (limit of limitNames; track limit) {
                    <li>{{ allowance(limit, option.limits) }}</li>
                  }
                </ul>
                @if (canBill() && option.plan !== org.plan) {
                  <button
                    type="button"
                    class="th-button-secondary mt-3"
                    [disabled]="planBusy()"
                    (click)="switchTo(option.plan)"
                  >
                    Switch to {{ planLabels[option.plan] }}
                  </button>
                }
              </li>
            }
          </ul>
          <p class="mt-3 text-xs text-muted">
            @if (canBill()) {
              Demo billing: switching plans is free and takes effect immediately.
            } @else {
              Only Owners change the plan.
            }
          </p>
        </section>

        @if (canDelete()) {
          <section class="th-card border-danger/40" aria-labelledby="danger-heading">
            <h2 id="danger-heading" class="text-lg font-semibold text-danger">Danger zone</h2>
            <p class="mt-2 text-sm">
              Deleting {{ org.name }} removes its clients, jobs, members, invitations and history
              for good. Every member loses access at once. This cannot be undone.
            </p>
            @if (deleteError(); as message) {
              <div class="th-alert-error mt-4" role="alert">{{ message }}</div>
            }
            <label class="th-label mt-4" for="confirm-name">
              Type <span class="font-semibold">{{ org.name }}</span> to confirm
            </label>
            <div class="flex flex-wrap gap-2">
              <input
                #confirmInput
                id="confirm-name"
                class="th-input max-w-sm"
                autocomplete="off"
                (input)="typedName.set(confirmInput.value)"
              />
              <button
                type="button"
                class="th-button-danger"
                [disabled]="!nameMatches() || deleting()"
                (click)="remove(org)"
              >
                {{ deleting() ? 'Deleting…' : 'Delete organization' }}
              </button>
            </div>
          </section>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsPage {
  readonly slug = input.required<string>();

  private readonly api = inject(SettingsApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  private readonly formEl = viewChild<ElementRef<HTMLElement>>('formEl');

  protected readonly planLabels = planLabels;
  protected readonly limitLabels = limitLabels;
  protected readonly limitNames = limitNames;
  protected readonly timeZones = timeZoneOptions();

  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canManage = computed(() => this.session.can(this.slug(), 'org.manage'));
  protected readonly canBill = computed(() => this.session.can(this.slug(), 'billing.manage'));
  protected readonly canDelete = computed(() => this.session.can(this.slug(), 'org.delete'));

  protected readonly overview = signal<OrganizationOverview | null>(null);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly planBusy = signal(false);
  protected readonly planDone = signal<string | null>(null);
  protected readonly planError = signal<{
    target: PlanName;
    blocking: readonly PlanLimitDetail[];
  } | null>(null);
  protected readonly typedName = signal('');
  protected readonly deleting = signal(false);
  protected readonly deleteError = signal<string | null>(null);

  /** Whitespace collapsed like the API compares; case matters. */
  protected readonly nameMatches = computed(
    () => collapse(this.typedName()) === this.overview()?.name && this.typedName().trim() !== '',
  );

  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(80)]],
    timeZone: ['', Validators.required],
  });

  constructor() {
    effect(() => {
      if (this.membership() && (this.canManage() || this.canBill())) {
        void this.load();
      }
    });
  }

  protected error(field: keyof typeof messages): string | null {
    return controlError(this.form.controls[field], messages[field]);
  }

  protected usage(limit: LimitName, org: OrganizationOverview): string {
    return usageText(limit, org.usage[limit], org.limits[limit]);
  }

  protected allowance(limit: LimitName, limits: PlanLimits): string {
    const allowed = limits[limit];
    return allowed === null
      ? `Unlimited ${limitLabels[limit].unit(2)}`
      : `${allowed.toLocaleString('en-US')} ${limitLabels[limit].unit(allowed)}`;
  }

  /** Percentage of the limit in use, for the meter's width and colour. */
  protected fill(limit: LimitName, org: OrganizationOverview): number {
    const allowed = org.limits[limit];
    return allowed ? Math.min(100, Math.round((org.usage[limit] / allowed) * 100)) : 0;
  }

  /** The meter's fill: width in 5% steps (classes, not an inline style: CSP, D023) and colour near the limit. */
  protected bar(limit: LimitName, org: OrganizationOverview): string {
    const fill = this.fill(limit, org);
    const colour = fill >= 100 ? 'bg-danger' : fill >= 80 ? 'bg-warning' : 'bg-accent';
    return `h-full rounded-full ${colour} ${widths[Math.round(fill / 5)] ?? 'w-full'}`;
  }

  protected blocking(detail: PlanLimitDetail, target: string): string {
    return blockingText(detail, target);
  }

  protected async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.show(await this.api.get(this.membership()!.organizationId));
      if (this.route.snapshot.fragment === 'plan') {
        afterNextRender(() => document.getElementById('plan')?.scrollIntoView(), {
          injector: this.injector,
        });
      }
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    }
  }

  protected async save(): Promise<void> {
    this.formError.set(null);
    this.saved.set(false);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.focusInvalid();
      return;
    }
    this.saving.set(true);
    try {
      const { name, timeZone } = this.form.getRawValue();
      this.show(await this.api.update(this.membership()!.organizationId, name, timeZone));
      this.saved.set(true);
      // The switcher and the picker show the name from the session.
      await this.session.load();
    } catch (error) {
      this.formError.set(applyProblem(this.form, toApiProblem(error)));
      this.focusInvalid();
    } finally {
      this.saving.set(false);
    }
  }

  protected async switchTo(plan: PlanName): Promise<void> {
    const current = this.overview()!.plan;
    const confirmed = await this.confirm.ask({
      title: `Switch to the ${planLabels[plan]} plan?`,
      message: `Demo billing: no payment is taken. The new limits apply at once (now on ${planLabels[current]}).`,
      confirmLabel: `Switch to ${planLabels[plan]}`,
    });
    if (!confirmed) {
      return;
    }
    this.planBusy.set(true);
    this.planError.set(null);
    this.planDone.set(null);
    try {
      this.show(await this.api.changePlan(this.membership()!.organizationId, plan));
      this.planDone.set(`You are now on the ${planLabels[plan]} plan.`);
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'plan-downgrade-blocked' && problem.blocking) {
        this.planError.set({ target: plan, blocking: problem.blocking });
      } else {
        this.planDone.set(null);
        this.planError.set(null);
        this.loadError.set(problem);
      }
    } finally {
      this.planBusy.set(false);
    }
  }

  protected async remove(org: OrganizationOverview): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Delete ${org.name}?`,
      message: 'Everything in it is removed for good, and every member loses access.',
      confirmLabel: 'Delete for good',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    this.deleting.set(true);
    this.deleteError.set(null);
    try {
      await this.api.delete(org.id, this.typedName());
      await this.session.load();
      await this.router.navigate(['/orgs'], { queryParams: { notice: 'deleted' } });
    } catch (error) {
      this.deleteError.set(toApiProblem(error).title);
      this.deleting.set(false);
    }
  }

  private show(overview: OrganizationOverview): void {
    this.overview.set(overview);
    if (!this.form.dirty || this.saved()) {
      this.form.reset({ name: overview.name, timeZone: overview.timeZone });
    }
  }

  private focusInvalid(): void {
    const form = this.formEl();
    if (form) {
      focusFirstInvalid(form, this.injector);
    }
  }
}

/** Literal class names, so Tailwind generates each width. */
const widths = [
  'w-0',
  'w-[5%]',
  'w-[10%]',
  'w-[15%]',
  'w-[20%]',
  'w-[25%]',
  'w-[30%]',
  'w-[35%]',
  'w-[40%]',
  'w-[45%]',
  'w-[50%]',
  'w-[55%]',
  'w-[60%]',
  'w-[65%]',
  'w-[70%]',
  'w-[75%]',
  'w-[80%]',
  'w-[85%]',
  'w-[90%]',
  'w-[95%]',
  'w-full',
];

function collapse(value: string): string {
  return value.split(/\s+/).filter(Boolean).join(' ');
}
