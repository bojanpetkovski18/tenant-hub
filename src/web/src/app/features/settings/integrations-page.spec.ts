import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { SessionUser } from '../../core/session/session.models';
import { auditExportRange } from '../audit/audit-page';
import { notificationRoute } from '../notifications/notifications.api';
import {
  loadSession,
  olivia,
  permissionsFor,
  provideTestHttp,
  settle,
} from '../../testing/session';
import {
  ApiKey,
  WebhookDelivery,
  WebhookEndpoint,
  deliveryOutcome,
  exportHref,
} from './integrations.api';

const orgId = olivia.memberships[0]!.organizationId;
const orgUrl = `/api/orgs/${orgId}`;

const key = (overrides: Partial<ApiKey> = {}): ApiKey => ({
  id: 'k1',
  name: 'Booking website',
  prefix: 'thk_AbCdEfGh',
  access: 'readWrite',
  createdAt: '2026-11-01T09:00:00Z',
  createdByName: 'Olivia Bennett',
  expiresAt: null,
  lastUsedAt: null,
  revokedAt: null,
  active: true,
  ...overrides,
});

const delivery = (overrides: Partial<WebhookDelivery> = {}): WebhookDelivery => ({
  id: 'd1',
  eventId: 'e1',
  eventType: 'job.created',
  attempt: 1,
  statusCode: 500,
  durationMs: 120,
  error: 'http-500',
  createdAt: '2026-11-21T10:00:00Z',
  nextAttemptAt: '2026-11-21T10:01:00Z',
  succeeded: false,
  ...overrides,
});

const endpoint = (overrides: Partial<WebhookEndpoint> = {}): WebhookEndpoint => ({
  id: 'w1',
  url: 'https://hooks.example/tenant-hub',
  events: ['job.created'],
  enabled: true,
  createdAt: '2026-11-01T09:00:00Z',
  disabledAt: null,
  failingSince: null,
  rotationOverlap: false,
  version: 7,
  lastDelivery: null,
  ...overrides,
});

const viewer: SessionUser = {
  ...olivia,
  memberships: [{ ...olivia.memberships[0]!, role: 'viewer', permissions: permissionsFor.viewer }],
};

describe('integration texts', () => {
  it('builds export links with repeated values and names delivery failures', () => {
    expect(exportHref('/x/export', { status: ['new', 'scheduled'], q: 'a b' })).toBe(
      '/x/export?status=new&status=scheduled&q=a+b',
    );
    expect(exportHref('/x/export', {})).toBe('/x/export');
    expect(deliveryOutcome(delivery({ succeeded: true, statusCode: 204, error: null }))).toBe(
      'Delivered (204)',
    );
    expect(deliveryOutcome(delivery({ error: 'address-not-allowed', statusCode: null }))).toBe(
      'Refused: the address is not public',
    );
    expect(deliveryOutcome(delivery())).toBe('Failed (500)');
  });

  it('exports the audit filter’s days, or the last 30, and at most 366', () => {
    const today = new Date('2026-11-21T12:00:00Z');
    expect(auditExportRange({ from: '', to: '' }, 'o1', today)).toEqual({
      href: '/api/orgs/o1/audit/export?from=2026-10-23&to=2026-11-21',
      label: '2026-10-23 to 2026-11-21',
    });
    expect(auditExportRange({ from: '2026-01-01', to: '2026-01-31' }, 'o1', today)?.label).toBe(
      '2026-01-01 to 2026-01-31',
    );
    expect(auditExportRange({ from: '2025-01-01', to: '2026-01-01' }, 'o1', today)).not.toBeNull();
    expect(auditExportRange({ from: '2025-01-01', to: '2026-01-02' }, 'o1', today)).toBeNull();
    expect(auditExportRange({ from: '2026-02-01', to: '2026-01-01' }, 'o1', today)).toBeNull();
  });

  it('leads a disabled-endpoint notification to Integrations', () => {
    expect(
      notificationRoute('brightline', {
        id: 'n',
        type: 'webhookDisabled',
        text: 'x',
        link: 'integrations',
        linkId: 'w1',
        createdAt: '2026-11-21T10:00:00Z',
        readAt: null,
      }).commands,
    ).toEqual(['/o', 'brightline', 'settings', 'integrations']);
  });
});

describe('integrations page', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  async function open(user: SessionUser = olivia): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideTestHttp(),
        provideRouter(
          routes,
          withComponentInputBinding(),
          withRouterConfig({ paramsInheritanceStrategy: 'always' }),
        ),
        provideCspSafeCdkStyles(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(user);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/o/brightline/settings/integrations');
    await settle(harness.fixture);
    return harness.routeNativeElement as HTMLElement;
  }

  async function answer(keys: ApiKey[] = [key()], hooks: WebhookEndpoint[] = [endpoint()]) {
    http.expectOne(`${orgUrl}/api-keys`).flush(keys);
    http.expectOne(`${orgUrl}/webhooks`).flush(hooks);
    await settle(harness.fixture);
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const main = (element: HTMLElement) => element.querySelector('main') ?? element;
  const button = (root: ParentNode, label: string) =>
    [...root.querySelectorAll('button')].find((b) => text(b) === label)!;

  it('lists keys and endpoints with what people need to tell them apart', async () => {
    const element = await open();
    await answer(
      [
        key(),
        key({ id: 'k2', name: 'Old sync', active: false, revokedAt: '2026-11-10T09:00:00Z' }),
      ],
      [endpoint({ failingSince: '2026-11-20T09:00:00Z', lastDelivery: delivery() })],
    );

    const keys = [...main(element).querySelectorAll('[aria-label="API keys"] li')].map(text);
    expect(keys[0]).toContain('Booking website Active');
    expect(keys[0]).toContain('thk_AbCdEfGh… · Read and write · by Olivia Bennett');
    expect(keys[0]).toContain('Never used');
    expect(keys[1]).toContain('Old sync Revoked');
    expect(main(element).querySelectorAll('[aria-label="API keys"] button')).toHaveLength(1);
    const hook = text(main(element).querySelector('[aria-label="Webhook endpoints"] li'));
    expect(hook).toContain('https://hooks.example/tenant-hub');
    expect(hook).toContain('Failing job.created');
    expect(hook).toContain('Failed (500)');
    expect(text(main(element))).toContain(`/api/orgs/${orgId}/`);
  });

  it('shows a new key once and then only its prefix', async () => {
    const element = await open();
    await answer([]);

    const name = main(element).querySelector<HTMLInputElement>('#key-name')!;
    name.value = 'Accounting sync';
    name.dispatchEvent(new Event('input'));
    const access = main(element).querySelector<HTMLSelectElement>('#key-access')!;
    access.value = 'readWrite';
    access.dispatchEvent(new Event('change'));
    main(element)
      .querySelector<HTMLFormElement>('form[aria-label="New API key"]')!
      .dispatchEvent(new Event('submit'));
    await settle(harness.fixture);
    const created = http.expectOne((r) => r.url === `${orgUrl}/api-keys` && r.method === 'POST');
    expect(created.request.body).toEqual({
      name: 'Accounting sync',
      access: 'readWrite',
      expiresAt: null,
    });
    created.flush({
      apiKey: key({ id: 'k9', name: 'Accounting sync' }),
      key: 'thk_' + 'x'.repeat(43),
    });
    await settle(harness.fixture);

    const dialog = document.querySelector('app-shown-once-dialog')!;
    expect(text(dialog.querySelector('h2'))).toBe('API key “Accounting sync”');
    expect(dialog.querySelector<HTMLInputElement>('#shown-once-value')!.value).toBe(
      'thk_' + 'x'.repeat(43),
    );
    expect(text(dialog)).toContain('it is not shown again');
    button(dialog, 'I have copied it').click();
    await settle(harness.fixture);

    expect(document.querySelector('app-shown-once-dialog')).toBeNull();
    expect(text(main(element))).toContain('Accounting sync');
    expect(text(main(element))).not.toContain('x'.repeat(43));
  });

  it('asks for a name before creating a key', async () => {
    const element = await open();
    await answer([]);

    main(element)
      .querySelector<HTMLFormElement>('form[aria-label="New API key"]')!
      .dispatchEvent(new Event('submit'));
    await settle(harness.fixture);

    http.expectNone(`${orgUrl}/api-keys`);
    expect(text(main(element).querySelector('#key-name-error'))).toBe(
      'Name the key after the system that uses it.',
    );
  });

  it('revokes a key only after the Owner confirms', async () => {
    const element = await open();
    await answer();

    main(element)
      .querySelector<HTMLButtonElement>('[aria-label="Revoke Booking website"]')!
      .click();
    await settle(harness.fixture);
    expect(text(document.querySelector('#confirm-title'))).toBe('Revoke “Booking website”?');
    button(document.querySelector('app-confirm-dialog')!, 'Revoke key').click();
    await settle(harness.fixture);

    http.expectOne((r) => r.url === `${orgUrl}/api-keys/k1` && r.method === 'DELETE').flush(null);
    await settle(harness.fixture);
    http
      .expectOne(`${orgUrl}/api-keys`)
      .flush([key({ active: false, revokedAt: '2026-11-21T10:00:00Z' })]);
    await settle(harness.fixture);
    expect(text(main(element).querySelector('[aria-label="API keys"]'))).toContain('Revoked');
  });

  it('adds an endpoint with chosen events and shows its secret once', async () => {
    const element = await open();
    await answer([], []);

    button(main(element), 'Add an endpoint').click();
    await settle(harness.fixture);
    const url = main(element).querySelector<HTMLInputElement>('#hook-url')!;
    url.value = 'https://hooks.example/new';
    url.dispatchEvent(new Event('input'));
    const boxes = [
      ...main(element).querySelectorAll<HTMLInputElement>('fieldset input[type="checkbox"]'),
    ];
    expect(boxes.map((b) => b.checked)).toEqual([
      true,
      false,
      false,
      false,
      false,
      false,
      false,
      false,
    ]);
    boxes[3]!.click(); // quote.approved
    button(main(element), 'Add endpoint').click();
    await settle(harness.fixture);

    const created = http.expectOne((r) => r.url === `${orgUrl}/webhooks` && r.method === 'POST');
    expect(created.request.body).toEqual({
      url: 'https://hooks.example/new',
      events: ['job.created', 'quote.approved'],
    });
    created.flush({ endpoint: endpoint({ id: 'w2' }), secret: 'whsec_' + 'a'.repeat(64) });
    await settle(harness.fixture);
    http
      .expectOne(`${orgUrl}/webhooks`)
      .flush([endpoint({ id: 'w2', url: 'https://hooks.example/new' })]);
    await settle(harness.fixture);

    const dialog = document.querySelector('app-shown-once-dialog')!;
    expect(dialog.querySelector<HTMLInputElement>('#shown-once-value')!.value).toBe(
      'whsec_' + 'a'.repeat(64),
    );
    button(dialog, 'I have copied it').click();
    await settle(harness.fixture);
    expect(text(main(element))).toContain('https://hooks.example/new');
  });

  it('needs an https address and at least one event', async () => {
    const element = await open();
    await answer([], []);

    button(main(element), 'Add an endpoint').click();
    await settle(harness.fixture);
    const url = main(element).querySelector<HTMLInputElement>('#hook-url')!;
    url.value = 'http://hooks.example/plain';
    url.dispatchEvent(new Event('input'));
    main(element).querySelector<HTMLInputElement>('fieldset input[type="checkbox"]')!.click();
    button(main(element), 'Add endpoint').click();
    await settle(harness.fixture);

    http.expectNone((r) => r.method === 'POST');
    expect(text(main(element).querySelector('#hook-url-error'))).toBe('Use an https:// address.');
    expect(text(main(element).querySelector('#hook-events-error'))).toBe(
      'Choose at least one event.',
    );
  });

  it('opens the delivery log and delivers an event again', async () => {
    const element = await open();
    await answer();

    const log = button(main(element), 'Delivery log');
    log.click();
    await settle(harness.fixture);
    expect(log.getAttribute('aria-expanded')).toBe('true');
    http
      .expectOne((r) => r.url === `${orgUrl}/webhooks/w1/deliveries`)
      .flush({ items: [delivery()], page: 1, pageSize: 10, total: 1 });
    await settle(harness.fixture);

    const row = text(main(element).querySelector('tbody tr'));
    expect(row).toContain('job.created');
    expect(row).toContain('Failed (500)');
    expect(row).toContain('next try');
    button(main(element), 'Deliver again').click();
    await settle(harness.fixture);
    http
      .expectOne(`${orgUrl}/webhooks/w1/deliveries/d1/redeliver`)
      .flush(null, { status: 202, statusText: 'Accepted' });
    await settle(harness.fixture);
    expect(text(main(element).querySelector('[aria-live="polite"]'))).toContain('on its way again');
  });

  it('says which plan has integrations when this one does not', async () => {
    const element = await open();
    http.expectOne(`${orgUrl}/api-keys`).flush(
      {
        title:
          'API keys and webhooks is part of the Business plan. An Owner can change the plan in Settings.',
        code: 'plan-feature-unavailable',
      },
      { status: 403, statusText: 'Forbidden' },
    );
    http.match(`${orgUrl}/webhooks`).forEach((r) => r.flush([]));
    await settle(harness.fixture);

    expect(text(main(element))).toContain('part of the Business plan');
    expect(text(main(element))).toContain('See the plans');
    expect(main(element).querySelector('#key-name')).toBeNull();
  });

  it('is for Owners and Admins only', async () => {
    const element = await open(viewer);

    http.expectNone(`${orgUrl}/api-keys`);
    expect(text(main(element))).toContain('Integrations are for Admins and Owners');
  });
});
