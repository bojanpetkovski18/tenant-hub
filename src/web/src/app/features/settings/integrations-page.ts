import { DatePipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { CurrentOrganizationStore } from '../../core/session/current-organization.store';
import { SessionStore } from '../../core/session/session.store';
import { applyProblem, controlError } from '../../shared/forms/server-errors';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import {
  ApiKey,
  ApiKeyAccess,
  DeliveryPage,
  IntegrationsApi,
  WebhookEndpoint,
  accessLabels,
  deliveryOutcome,
  webhookEvents,
} from './integrations.api';
import { ShowOnce, ShownOnceOptions } from './shown-once-dialog';

const maxEndpoints = 5;

const keyMessages = {
  name: {
    required: 'Name the key after the system that uses it.',
    maxlength: 'Use at most 60 characters.',
  },
} as const;

const hookMessages = {
  url: { required: 'Enter the address to send events to.', pattern: 'Use an https:// address.' },
} as const;

/** Expiry choices for a new key (SPEC-V2 5.8: none, or at most a year). */
const expiries: readonly (readonly [string, string])[] = [
  ['', 'Never'],
  ['30', 'In 30 days'],
  ['90', 'In 90 days'],
  ['365', 'In a year'],
];

/**
 * US-110 and US-111: Settings → Integrations, for Owners and Admins on the Business plan. API keys (created with the
 * key shown once, revoked at once) and webhook endpoints (events from the catalog, a signing secret shown once and
 * rotated with a day's overlap, a test event, the delivery log with "deliver again"). On another plan the page says
 * which plan has the feature instead.
 */
@Component({
  selector: 'app-integrations-page',
  imports: [DatePipe, NgTemplateOutlet, ReactiveFormsModule, RouterLink, EmptyState, ErrorState],
  template: `
    <div class="max-w-4xl space-y-6">
      <div>
        <a [routerLink]="['/o', slug(), 'settings']" class="text-sm text-accent hover:underline"
          >← Settings</a
        >
        <h1 class="mt-2 text-2xl font-semibold tracking-tight">Integrations</h1>
        <p class="mt-1 text-sm text-muted">
          Connect other systems: they read and change clients and jobs with an API key, and hear
          about changes through signed webhooks.
        </p>
      </div>
      <p class="sr-only" aria-live="polite">{{ announcement() }}</p>

      @if (!canManage()) {
        <app-empty-state
          title="Integrations are for Admins and Owners"
          message="Ask one of them to connect another system."
        />
      } @else if (unavailable(); as problem) {
        <section class="th-card" aria-labelledby="unavailable-heading">
          <h2 id="unavailable-heading" class="text-lg font-semibold">API keys and webhooks</h2>
          <p class="mt-2 text-sm text-muted">{{ problem.title }}</p>
          @if (canChangePlan()) {
            <a
              [routerLink]="['/o', slug(), 'settings']"
              fragment="plan"
              class="mt-3 inline-block font-medium text-accent hover:underline"
              >See the plans</a
            >
          }
        </section>
      } @else if (loadError(); as problem) {
        <app-error-state [message]="problem.title" (retry)="load()" />
      } @else if (keys() === null || endpoints() === null) {
        <div class="h-96 animate-pulse rounded-lg bg-surface-2" aria-busy="true"></div>
      } @else {
        <section class="th-card" aria-labelledby="keys-heading">
          <h2 id="keys-heading" class="text-lg font-semibold">API keys</h2>
          <p class="mt-1 text-sm text-muted">
            Send a key as <code class="font-mono">Authorization: Bearer …</code> to
            <code class="font-mono break-all">/api/orgs/{{ organizationId() }}/…</code>: clients,
            jobs and agreements. A read-only key cannot change anything; no key can manage members,
            billing or settings.
          </p>

          @if (keys()!.length === 0) {
            <p class="mt-4 text-sm text-muted">No keys yet.</p>
          } @else {
            <ul class="mt-4 divide-y divide-border" aria-label="API keys">
              @for (key of keys(); track key.id) {
                <li class="flex flex-wrap items-start justify-between gap-3 py-3">
                  <div class="min-w-0">
                    <p class="font-medium">
                      {{ key.name }}
                      <span class="th-badge ml-1">{{ keyState(key) }}</span>
                    </p>
                    <p class="text-sm text-muted">
                      <span class="font-mono">{{ key.prefix }}…</span> ·
                      {{ accessLabels[key.access] }} · by {{ key.createdByName }} on
                      {{ key.createdAt | date: 'd MMM y' }}
                    </p>
                    <p class="text-sm text-muted">
                      {{
                        key.lastUsedAt
                          ? 'Last used ' + (key.lastUsedAt | date: 'd MMM y, HH:mm')
                          : 'Never used'
                      }}
                      @if (key.expiresAt && !key.revokedAt) {
                        · {{ key.active ? 'Expires' : 'Expired' }}
                        {{ key.expiresAt | date: 'd MMM y' }}
                      }
                    </p>
                  </div>
                  @if (key.active) {
                    <button
                      type="button"
                      class="th-button-secondary"
                      [attr.aria-label]="'Revoke ' + key.name"
                      [disabled]="busy()"
                      (click)="revoke(key)"
                    >
                      Revoke
                    </button>
                  }
                </li>
              }
            </ul>
          }

          <form
            class="mt-4 grid gap-4 border-t border-border pt-4 sm:grid-cols-[1fr_auto_auto_auto] sm:items-end"
            [formGroup]="keyForm"
            (ngSubmit)="createKey()"
            novalidate
            aria-label="New API key"
          >
            <div>
              <label class="th-label" for="key-name">New key's name</label>
              <input
                id="key-name"
                class="th-input"
                formControlName="name"
                maxlength="60"
                placeholder="e.g. Booking website"
                [attr.aria-invalid]="!!keyError('name')"
                [attr.aria-describedby]="keyError('name') ? 'key-name-error' : null"
              />
            </div>
            <div>
              <label class="th-label" for="key-access">Access</label>
              <select id="key-access" class="th-input" formControlName="access">
                <option value="readOnly">{{ accessLabels.readOnly }}</option>
                <option value="readWrite">{{ accessLabels.readWrite }}</option>
              </select>
            </div>
            <div>
              <label class="th-label" for="key-expiry">Expires</label>
              <select id="key-expiry" class="th-input" formControlName="expiry">
                @for (option of expiries; track option[0]) {
                  <option [value]="option[0]">{{ option[1] }}</option>
                }
              </select>
            </div>
            <button type="submit" class="th-button" [disabled]="busy()">Create key</button>
            @if (keyError('name'); as message) {
              <p id="key-name-error" class="th-field-error sm:col-span-4">{{ message }}</p>
            }
            @if (keyFormError(); as message) {
              <div class="th-alert-error sm:col-span-4" role="alert">{{ message }}</div>
            }
          </form>
        </section>

        <section class="th-card" aria-labelledby="hooks-heading">
          <h2 id="hooks-heading" class="text-lg font-semibold">Webhooks</h2>
          <p class="mt-1 text-sm text-muted">
            Events are posted as JSON to your https address, signed with the endpoint's secret.
            Failed deliveries are retried for about a day; after three days of failures the endpoint
            is turned off and the Owners are told.
          </p>
          <details class="mt-2 text-sm">
            <summary class="cursor-pointer font-medium text-accent">
              How to check the signature
            </summary>
            <p class="mt-2 text-muted">
              The <code class="font-mono">TenantHub-Signature</code> header reads
              <code class="font-mono">t=&lt;unix seconds&gt;,v1=&lt;signature&gt;</code>. Compute
              HMAC-SHA256 of <code class="font-mono">&lt;t&gt;.&lt;body&gt;</code> with the secret,
              in lowercase hex, and compare it with each <code class="font-mono">v1</code> (there
              are two for a day after a rotation). Refuse an old <code class="font-mono">t</code>,
              and ignore an <code class="font-mono">id</code> you have seen: retries repeat it.
            </p>
          </details>

          @if (endpoints()!.length === 0) {
            <p class="mt-4 text-sm text-muted">No endpoints yet.</p>
          } @else {
            <ul class="mt-4 divide-y divide-border" aria-label="Webhook endpoints">
              @for (hook of endpoints(); track hook.id) {
                <li class="py-4">
                  @if (editing() === hook.id) {
                    <ng-container
                      *ngTemplateOutlet="hookFormTemplate; context: { $implicit: hook }"
                    />
                  } @else {
                    <div class="flex flex-wrap items-start justify-between gap-3">
                      <div class="min-w-0">
                        <p class="font-medium break-all">{{ hook.url }}</p>
                        <p class="text-sm text-muted">
                          <span class="th-badge">{{ hookState(hook) }}</span>
                          {{ eventsText(hook.events) }}
                        </p>
                        @if (hook.lastDelivery; as last) {
                          <p class="text-sm text-muted">
                            Last delivery {{ last.createdAt | date: 'd MMM y, HH:mm' }}:
                            {{ outcome(last) }}
                          </p>
                        }
                        @if (hook.rotationOverlap) {
                          <p class="text-sm text-muted">
                            Signed with the new and the previous secret until the day after the
                            rotation.
                          </p>
                        }
                      </div>
                      <div class="flex flex-wrap gap-2">
                        <button
                          type="button"
                          class="th-button-secondary"
                          [attr.aria-expanded]="logFor() === hook.id"
                          [attr.aria-controls]="'log-' + hook.id"
                          (click)="toggleLog(hook)"
                        >
                          Delivery log
                        </button>
                        <button
                          type="button"
                          class="th-button-secondary"
                          [disabled]="busy() || !hook.enabled"
                          (click)="sendTest(hook)"
                        >
                          Send a test event
                        </button>
                        <button type="button" class="th-button-secondary" (click)="edit(hook)">
                          Edit
                        </button>
                        <button
                          type="button"
                          class="th-button-secondary"
                          [id]="'rotate-' + hook.id"
                          [disabled]="busy()"
                          (click)="rotate(hook)"
                        >
                          New secret
                        </button>
                        <button
                          type="button"
                          class="th-button-secondary"
                          [attr.aria-label]="'Remove ' + hook.url"
                          [disabled]="busy()"
                          (click)="remove(hook)"
                        >
                          Remove
                        </button>
                      </div>
                    </div>
                  }
                  @if (logFor() === hook.id) {
                    <div [id]="'log-' + hook.id" class="mt-3">
                      @if (logError(); as problem) {
                        <app-error-state
                          [message]="problem.title"
                          (retry)="loadLog(hook.id, logPage())"
                        />
                      } @else if (log() === null) {
                        <div class="h-24 animate-pulse rounded bg-surface-2" aria-busy="true"></div>
                      } @else if (log()!.items.length === 0) {
                        <p class="text-sm text-muted">Nothing delivered in the last 30 days.</p>
                      } @else {
                        <!-- relative: the visually hidden header text stays inside the scroll box, not beyond the page. Focusable: on a narrow screen the table scrolls, and keyboards must reach it (axe, M21). -->
                        <div
                          class="relative overflow-x-auto"
                          tabindex="0"
                          role="region"
                          [attr.aria-label]="'Delivery log of ' + hook.url"
                        >
                          <table class="w-full text-left text-sm">
                            <caption class="sr-only">
                              Deliveries to
                              {{
                                hook.url
                              }}, newest first
                            </caption>
                            <thead class="text-muted">
                              <tr>
                                <th scope="col" class="py-1 pr-3 font-medium">When</th>
                                <th scope="col" class="py-1 pr-3 font-medium">Event</th>
                                <th scope="col" class="py-1 pr-3 font-medium">Attempt</th>
                                <th scope="col" class="py-1 pr-3 font-medium">Outcome</th>
                                <th scope="col" class="py-1 pr-3 font-medium">Time</th>
                                <th scope="col" class="py-1 font-medium">
                                  <span class="sr-only">Actions</span>
                                </th>
                              </tr>
                            </thead>
                            <tbody class="divide-y divide-border">
                              @for (d of log()!.items; track d.id) {
                                <tr>
                                  <td class="py-2 pr-3 whitespace-nowrap">
                                    {{ d.createdAt | date: 'd MMM, HH:mm:ss' }}
                                  </td>
                                  <td class="py-2 pr-3 font-mono">{{ d.eventType }}</td>
                                  <td class="py-2 pr-3">{{ d.attempt }}</td>
                                  <td class="py-2 pr-3">
                                    {{ outcome(d) }}
                                    @if (d.nextAttemptAt) {
                                      <span class="text-muted">
                                        · next try
                                        {{ d.nextAttemptAt | date: 'd MMM, HH:mm' }}</span
                                      >
                                    }
                                  </td>
                                  <td class="py-2 pr-3 whitespace-nowrap">{{ d.durationMs }} ms</td>
                                  <td class="py-2 text-right">
                                    <button
                                      type="button"
                                      class="th-button-secondary"
                                      [attr.aria-label]="
                                        'Deliver ' +
                                        d.eventType +
                                        ' of ' +
                                        (d.createdAt | date: 'd MMM, HH:mm:ss') +
                                        ' again'
                                      "
                                      [disabled]="busy() || !hook.enabled"
                                      (click)="redeliver(hook, d.id)"
                                    >
                                      Deliver again
                                    </button>
                                  </td>
                                </tr>
                              }
                            </tbody>
                          </table>
                        </div>
                        @if (log()!.total > log()!.pageSize) {
                          <div class="mt-2 flex items-center gap-2 text-sm">
                            <button
                              type="button"
                              class="th-button-secondary"
                              [disabled]="logPage() === 1"
                              (click)="loadLog(hook.id, logPage() - 1)"
                            >
                              Newer
                            </button>
                            <span>Page {{ logPage() }} of {{ logPages() }}</span>
                            <button
                              type="button"
                              class="th-button-secondary"
                              [disabled]="logPage() >= logPages()"
                              (click)="loadLog(hook.id, logPage() + 1)"
                            >
                              Older
                            </button>
                          </div>
                        }
                      }
                    </div>
                  }
                </li>
              }
            </ul>
          }

          @if (editing() === 'new') {
            <div class="mt-4 border-t border-border pt-4">
              <ng-container *ngTemplateOutlet="hookFormTemplate; context: { $implicit: null }" />
            </div>
          } @else if (endpoints()!.length < maxEndpoints) {
            <button id="add-endpoint" type="button" class="th-button mt-4" (click)="edit(null)">
              Add an endpoint
            </button>
          } @else {
            <p class="mt-4 text-sm text-muted">
              An organization has at most {{ maxEndpoints }} endpoints. Remove one to add another.
            </p>
          }
        </section>
      }
    </div>

    <ng-template #hookFormTemplate let-hook>
      <form
        [formGroup]="hookForm"
        (ngSubmit)="saveHook(hook)"
        novalidate
        [attr.aria-label]="hook ? 'Edit endpoint' : 'New endpoint'"
      >
        <div>
          <label class="th-label" for="hook-url">Address</label>
          <input
            id="hook-url"
            class="th-input"
            type="url"
            formControlName="url"
            placeholder="https://example.org/hooks/tenant-hub"
            [attr.aria-invalid]="!!hookError('url')"
            [attr.aria-describedby]="hookError('url') ? 'hook-url-error' : null"
          />
          @if (hookError('url'); as message) {
            <p id="hook-url-error" class="th-field-error">{{ message }}</p>
          }
        </div>
        <fieldset class="mt-4" [attr.aria-describedby]="eventsError() ? 'hook-events-error' : null">
          <legend class="th-label">Events</legend>
          <div class="grid gap-1 sm:grid-cols-2">
            @for (event of webhookEvents; track event[0]) {
              <label class="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  [checked]="chosen().has(event[0])"
                  (change)="toggleEvent(event[0])"
                />
                {{ event[1] }} <span class="font-mono text-xs text-muted">{{ event[0] }}</span>
              </label>
            }
          </div>
          @if (eventsError(); as message) {
            <p id="hook-events-error" class="th-field-error">{{ message }}</p>
          }
        </fieldset>
        @if (hook) {
          <label class="mt-4 flex items-center gap-2 text-sm">
            <input type="checkbox" formControlName="enabled" />
            Enabled{{ hook.enabled ? '' : ' (turning it on starts the failure count afresh)' }}
          </label>
        }
        @if (hookFormError(); as message) {
          <div class="th-alert-error mt-4" role="alert">{{ message }}</div>
        }
        <div class="mt-4 flex flex-wrap gap-2">
          <button type="submit" class="th-button" [disabled]="busy()">
            {{ hook ? 'Save' : 'Add endpoint' }}
          </button>
          <button type="button" class="th-button-secondary" (click)="editing.set(null)">
            Cancel
          </button>
        </div>
      </form>
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IntegrationsPage {
  /** Route parameter `:slug`. */
  readonly slug = input.required<string>();

  private readonly api = inject(IntegrationsApi);
  private readonly session = inject(SessionStore);
  private readonly currentOrganization = inject(CurrentOrganizationStore);
  private readonly confirm = inject(Confirm);
  private readonly showOnce = inject(ShowOnce);
  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly accessLabels = accessLabels;
  protected readonly webhookEvents = webhookEvents;
  protected readonly expiries = expiries;
  protected readonly maxEndpoints = maxEndpoints;
  protected readonly outcome = deliveryOutcome;

  protected readonly organizationId = computed(
    () => this.session.membershipBySlug(this.slug())?.organizationId ?? '',
  );
  protected readonly canManage = computed(() =>
    this.session.can(this.slug(), 'integrations.manage'),
  );
  protected readonly canChangePlan = computed(() =>
    this.session.can(this.slug(), 'billing.manage'),
  );

  protected readonly keys = signal<ApiKey[] | null>(null);
  protected readonly endpoints = signal<WebhookEndpoint[] | null>(null);
  protected readonly unavailable = signal<ApiProblem | null>(null);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly announcement = signal('');

  protected readonly keyForm = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(60)]],
    access: this.fb.control<ApiKeyAccess>('readOnly'),
    expiry: [''],
  });
  protected readonly keyFormError = signal<string | null>(null);
  private readonly keyAttempted = signal(false);

  /** The endpoint being edited, 'new' for the add form, or null. */
  protected readonly editing = signal<string | null>(null);
  protected readonly hookForm = this.fb.group({
    url: ['', [Validators.required, Validators.pattern(/^\s*https:\/\/\S+$/i)]],
    enabled: [true],
  });
  protected readonly chosen = signal<ReadonlySet<string>>(new Set());
  protected readonly eventsError = signal<string | null>(null);
  protected readonly hookFormError = signal<string | null>(null);
  private readonly hookAttempted = signal(false);

  protected readonly logFor = signal<string | null>(null);
  protected readonly log = signal<DeliveryPage | null>(null);
  protected readonly logPage = signal(1);
  protected readonly logError = signal<ApiProblem | null>(null);
  protected readonly logPages = computed(() => {
    const page = this.log();
    return page ? Math.max(1, Math.ceil(page.total / page.pageSize)) : 1;
  });

  constructor() {
    // The plan's features come with the organization the shell loads: on a plan without integrations the page says
    // so without asking the API, which would only answer 403.
    effect(() => {
      const organization = this.currentOrganization.organization();
      if (!this.canManage() || !organization || organization.id !== this.organizationId()) {
        return;
      }
      if (organization.features && !organization.features.includes('integrations')) {
        this.unavailable.set({
          status: 403,
          title:
            'API keys and webhooks are part of the Business plan. An Owner can change the plan in Settings.',
          code: 'plan-feature-unavailable',
          fieldErrors: {},
        });
        return;
      }
      void this.load();
    });
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    this.unavailable.set(null);
    try {
      const [keys, endpoints] = await Promise.all([
        this.api.keys(this.organizationId()),
        this.api.webhooks(this.organizationId()),
      ]);
      this.keys.set(keys);
      this.endpoints.set(endpoints);
    } catch (error) {
      const problem = toApiProblem(error);
      if (problem.code === 'plan-feature-unavailable') {
        this.unavailable.set(problem);
      } else {
        this.loadError.set(problem);
      }
    }
  }

  protected keyState(key: ApiKey): string {
    return key.revokedAt ? 'Revoked' : key.active ? 'Active' : 'Expired';
  }

  protected hookState(hook: WebhookEndpoint): string {
    if (!hook.enabled) {
      return 'Turned off';
    }
    return hook.failingSince ? 'Failing' : 'Enabled';
  }

  protected eventsText(events: readonly string[]): string {
    return events.join(', ');
  }

  // These forms make new things: an empty one is not an error until someone tries to save it.
  protected keyError(field: keyof typeof keyMessages): string | null {
    return this.keyAttempted()
      ? controlError(this.keyForm.controls[field], keyMessages[field])
      : null;
  }

  protected hookError(field: keyof typeof hookMessages): string | null {
    return this.hookAttempted()
      ? controlError(this.hookForm.controls[field], hookMessages[field])
      : null;
  }

  protected async createKey(): Promise<void> {
    this.keyFormError.set(null);
    this.keyAttempted.set(true);
    if (this.keyForm.invalid) {
      this.keyForm.markAllAsTouched();
      document.getElementById('key-name')?.focus();
      return;
    }
    const { name, access, expiry } = this.keyForm.getRawValue();
    const expiresAt = expiry
      ? new Date(Date.now() + Number(expiry) * 86_400_000).toISOString()
      : null;
    let shown: ShownOnceOptions | null = null;
    await this.run(async () => {
      try {
        const created = await this.api.createKey(this.organizationId(), {
          name: name.trim(),
          access,
          expiresAt,
        });
        this.keys.update((keys) => [created.apiKey, ...(keys ?? [])]);
        this.keyForm.reset({ name: '', access: 'readOnly', expiry: '' });
        this.keyAttempted.set(false);
        this.announcement.set(`Created the key ${created.apiKey.name}.`);
        shown = {
          title: `API key “${created.apiKey.name}”`,
          message:
            'Copy the key now and keep it somewhere safe: it is not shown again. If it is lost, revoke it and create another.',
          label: 'API key',
          value: created.key,
          returnFocusTo: '#key-name',
        };
      } catch (error) {
        this.keyFormError.set(applyProblem(this.keyForm, toApiProblem(error)));
      }
    });
    await this.reveal(shown);
  }

  /** The shown-once dialog, opened after the action, when its buttons are no longer busy. */
  private async reveal(options: ShownOnceOptions | null): Promise<void> {
    if (options) {
      await this.showOnce.show(options);
    }
  }

  protected async revoke(key: ApiKey): Promise<void> {
    const ok = await this.confirm.ask({
      title: `Revoke “${key.name}”?`,
      message: 'Requests with this key are refused from now on. This cannot be undone.',
      confirmLabel: 'Revoke key',
      danger: true,
    });
    if (!ok) {
      return;
    }
    await this.run(async () => {
      await this.api.revokeKey(this.organizationId(), key.id);
      this.keys.set(await this.api.keys(this.organizationId()));
      this.announcement.set(`Revoked the key ${key.name}.`);
      // Its Revoke button is gone: continue from the form for a new key.
      setTimeout(() => document.getElementById('key-name')?.focus());
    });
  }

  protected edit(hook: WebhookEndpoint | null): void {
    this.hookFormError.set(null);
    this.hookAttempted.set(false);
    this.eventsError.set(null);
    this.hookForm.reset({ url: hook?.url ?? '', enabled: hook?.enabled ?? true });
    this.chosen.set(new Set(hook?.events ?? ['job.created']));
    this.editing.set(hook?.id ?? 'new');
    setTimeout(() => document.getElementById('hook-url')?.focus());
  }

  protected toggleEvent(event: string): void {
    this.chosen.update((set) => {
      const next = new Set(set);
      if (!next.delete(event)) {
        next.add(event);
      }
      return next;
    });
    this.eventsError.set(null);
  }

  protected async saveHook(hook: WebhookEndpoint | null): Promise<void> {
    this.hookFormError.set(null);
    this.hookAttempted.set(true);
    const events = webhookEvents.map(([e]) => e).filter((e) => this.chosen().has(e));
    this.eventsError.set(events.length === 0 ? 'Choose at least one event.' : null);
    if (this.hookForm.invalid || events.length === 0) {
      this.hookForm.markAllAsTouched();
      return;
    }
    const { url, enabled } = this.hookForm.getRawValue();
    let shown: ShownOnceOptions | null = null;
    await this.run(async () => {
      try {
        if (hook) {
          await this.api.updateWebhook(this.organizationId(), hook.id, {
            url: url.trim(),
            events,
            enabled,
          });
          this.announcement.set('Saved the endpoint.');
        } else {
          const created = await this.api.createWebhook(this.organizationId(), {
            url: url.trim(),
            events,
          });
          this.editing.set(null);
          this.endpoints.set(await this.api.webhooks(this.organizationId()));
          this.announcement.set('Added the endpoint.');
          shown = {
            title: 'The endpoint’s signing secret',
            message:
              'Your receiver checks each event with this secret. Copy it now: it is not shown again (you can make a new one later).',
            label: 'Signing secret',
            value: created.secret,
            returnFocusTo: '#add-endpoint',
          };
          return;
        }
        this.editing.set(null);
        this.endpoints.set(await this.api.webhooks(this.organizationId()));
      } catch (error) {
        const problem = toApiProblem(error);
        const events = problem.fieldErrors['events'];
        if (events) {
          this.eventsError.set(events.join(' '));
        }
        this.hookFormError.set(applyProblem(this.hookForm, problem));
      }
    });
    await this.reveal(shown);
  }

  protected async rotate(hook: WebhookEndpoint): Promise<void> {
    const ok = await this.confirm.ask({
      title: 'Make a new signing secret?',
      message:
        'Events are signed with both the new and the current secret for 24 hours, so you can update your receiver in between.',
      confirmLabel: 'Make a new secret',
    });
    if (!ok) {
      return;
    }
    let shown: ShownOnceOptions | null = null;
    await this.run(async () => {
      const rotated = await this.api.rotateSecret(this.organizationId(), hook.id);
      this.endpoints.set(await this.api.webhooks(this.organizationId()));
      shown = {
        title: 'The new signing secret',
        message:
          'Copy it now: it is not shown again. The previous secret keeps working for 24 hours.',
        label: 'Signing secret',
        value: rotated.secret,
        returnFocusTo: '#rotate-' + hook.id,
      };
    });
    await this.reveal(shown);
  }

  protected async sendTest(hook: WebhookEndpoint): Promise<void> {
    await this.run(async () => {
      await this.api.sendTest(this.organizationId(), hook.id);
      this.announcement.set('A test event is on its way. Its delivery shows in the log.');
    });
  }

  protected async remove(hook: WebhookEndpoint): Promise<void> {
    const ok = await this.confirm.ask({
      title: 'Remove this endpoint?',
      message: `Events stop going to ${hook.url}, and its delivery log is removed with it.`,
      confirmLabel: 'Remove endpoint',
      danger: true,
    });
    if (!ok) {
      return;
    }
    await this.run(async () => {
      await this.api.deleteWebhook(this.organizationId(), hook.id);
      if (this.logFor() === hook.id) {
        this.logFor.set(null);
      }
      this.endpoints.set(await this.api.webhooks(this.organizationId()));
      this.announcement.set('Removed the endpoint.');
      setTimeout(() => document.getElementById('add-endpoint')?.focus());
    });
  }

  protected toggleLog(hook: WebhookEndpoint): void {
    if (this.logFor() === hook.id) {
      this.logFor.set(null);
      return;
    }
    this.logFor.set(hook.id);
    void this.loadLog(hook.id, 1);
  }

  async loadLog(endpointId: string, page: number): Promise<void> {
    this.log.set(null);
    this.logError.set(null);
    this.logPage.set(page);
    try {
      this.log.set(await this.api.deliveries(this.organizationId(), endpointId, page));
    } catch (error) {
      this.logError.set(toApiProblem(error));
    }
  }

  protected async redeliver(hook: WebhookEndpoint, deliveryId: string): Promise<void> {
    await this.run(async () => {
      await this.api.redeliver(this.organizationId(), hook.id, deliveryId);
      this.announcement.set('The event is on its way again. Its delivery shows in the log.');
    });
  }

  /** One action at a time; an unexpected failure is said in the live region. */
  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
    } catch (error) {
      this.announcement.set(toApiProblem(error).title);
      this.loadError.set(toApiProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
