import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  Router,
  provideRouter,
  withComponentInputBinding,
  withRouterConfig,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { SessionUser } from '../../core/session/session.models';
import {
  loadSession,
  olivia,
  permissionsFor,
  provideTestHttp,
  settle,
} from '../../testing/session';
import { relativeTime } from './audit-page';
import { auditApiParams, auditUrlChanges, parseAuditQuery } from './audit-query';
import { AuditEntry } from './audit.api';

const orgId = olivia.memberships[0]!.organizationId;
const auditUrl = `/api/orgs/${orgId}/audit`;
const jobId = '0199f0a0-0000-7000-8000-0000000000f1';

const entry = (overrides: Partial<AuditEntry> = {}): AuditEntry => ({
  id: 7,
  occurredAt: '2026-10-22T10:15:00Z',
  actorUserId: '0199f0a0-0000-7000-8000-000000000001',
  actorName: 'Olivia Bennett',
  action: 'job.status_changed',
  entityType: 'job',
  entityId: jobId,
  summary: 'Cancelled JOB-0042: Contract ended',
  changes: [
    { field: 'status', from: 'In progress', to: 'Cancelled' },
    { field: 'reason', from: null, to: 'Contract ended' },
  ],
  entityExists: true,
  ...overrides,
});

describe('audit query (URL ↔ API)', () => {
  it('keeps known values and drops the rest', () => {
    expect(
      parseAuditQuery({
        actor: '0199f0a0-0000-7000-8000-000000000001',
        action: 'job.created',
        entityType: 'job',
        entityId: 'nope',
        from: '2026-10-01',
        to: '1 Oct',
        before: '638960000000000000-42',
      }),
    ).toEqual({
      actor: '0199f0a0-0000-7000-8000-000000000001',
      action: 'job.created',
      entityType: 'job',
      entityId: '',
      from: '2026-10-01',
      to: '',
      before: '638960000000000000-42',
    });
    expect(parseAuditQuery({ action: 'job.exploded', entityType: 'planet', before: 'x' })).toEqual({
      actor: '',
      action: '',
      entityType: '',
      entityId: '',
      from: '',
      to: '',
      before: '',
    });
  });

  it('sends only what is set, and any filter change starts again from the newest page', () => {
    expect(auditApiParams(parseAuditQuery({ action: 'job.created', before: '1-2' }), 50)).toEqual({
      pageSize: '50',
      action: 'job.created',
      before: '1-2',
    });
    expect(auditUrlChanges({ action: 'client.updated' })).toEqual({
      action: 'client.updated',
      before: null,
    });
    expect(auditUrlChanges({ before: '9-9' })).toEqual({ before: '9-9' });
    expect(auditUrlChanges({ from: '', to: '' })).toEqual({ from: null, to: null, before: null });
  });

  it('says how long ago in plain words', () => {
    const now = Date.parse('2026-10-22T12:00:00Z');
    expect(relativeTime('2026-10-22T11:59:40Z', now)).toBe('just now');
    expect(relativeTime('2026-10-22T11:55:00Z', now)).toBe('5 minutes ago');
    expect(relativeTime('2026-10-22T09:00:00Z', now)).toBe('3 hours ago');
    expect(relativeTime('2026-10-21T12:00:00Z', now)).toBe('yesterday');
    expect(relativeTime('2026-10-01T12:00:00Z', now)).toBe('3 weeks ago');
  });
});

describe('audit log page', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  async function open(url: string, user: SessionUser = olivia): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideTestHttp(),
        provideRouter(
          routes,
          withComponentInputBinding(),
          withRouterConfig({ paramsInheritanceStrategy: 'always' }),
        ),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(user);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    http.match((r) => r.url.endsWith('/members')).forEach((r) => r.flush([]));
    return harness.routeNativeElement as HTMLElement;
  }

  const text = (element: Element | null) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const main = (element: HTMLElement) => element.querySelector('main') ?? element;

  it('sends the URL filters and cursor, and shows entries with their links and fields on demand', async () => {
    const element = await open(
      '/o/brightline/audit?action=job.status_changed&from=2026-10-01&before=638960000000000000-42',
    );
    const request = http.expectOne((r) => r.url === auditUrl);
    expect(request.request.params.keys().sort()).toEqual(['action', 'before', 'from', 'pageSize']);
    request.flush({
      items: [
        entry(),
        entry({
          id: 6,
          entityExists: false,
          summary: 'Deleted JOB-0041 “Old”',
          action: 'job.deleted',
          changes: [],
        }),
      ],
      nextCursor: '638950000000000000-6',
    });
    await settle(harness.fixture);

    const items = [...main(element).querySelectorAll('li')];
    expect(text(items[0]!)).toContain('Cancelled JOB-0042: Contract ended');
    expect(text(items[0]!)).toContain('Olivia Bennett');
    expect(items[0]!.querySelector(`a[href="/o/brightline/jobs/${jobId}"]`)).not.toBeNull();
    expect([...items[1]!.querySelectorAll('a')].some((a) => text(a).startsWith('Open'))).toBe(
      false,
    );

    const toggle = items[0]!.querySelector('button')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    await settle(harness.fixture);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const list = items[0]!.querySelector('dl')!;
    expect([...list.querySelectorAll('dt')].map((d) => text(d))).toEqual(['Status', 'Reason']);
    expect([...list.querySelectorAll('dd')].map((d) => text(d))).toEqual([
      'In progress → changed to Cancelled',
      'Contract ended',
    ]);
  });

  it('moves to older entries through the URL and back to the newest', async () => {
    const element = await open('/o/brightline/audit?entityType=job');
    http.expectOne((r) => r.url === auditUrl).flush({ items: [entry()], nextCursor: '100-7' });
    await settle(harness.fixture);
    const router = TestBed.inject(Router);

    [...main(element).querySelectorAll('button')].find((b) => text(b) === 'Older entries')!.click();
    await settle(harness.fixture);
    expect(router.url).toBe('/o/brightline/audit?entityType=job&before=100-7');
    http
      .expectOne((r) => r.url === auditUrl && r.params.get('before') === '100-7')
      .flush({ items: [entry({ id: 3 })], nextCursor: null });
    await settle(harness.fixture);
    expect(text(main(element))).toContain('That is everything.');

    [...main(element).querySelectorAll('button')].find((b) => text(b) === 'Newest')!.click();
    await settle(harness.fixture);
    expect(router.url).toBe('/o/brightline/audit?entityType=job');
    http.expectOne((r) => r.url === auditUrl).flush({ items: [entry()], nextCursor: null });
  });

  it('a filter change drops the cursor', async () => {
    const element = await open('/o/brightline/audit?before=100-7');
    http.expectOne((r) => r.url === auditUrl).flush({ items: [entry()], nextCursor: null });
    await settle(harness.fixture);

    const type = main(element).querySelector('#audit-type') as HTMLSelectElement;
    type.value = 'client';
    type.dispatchEvent(new Event('change'));
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/o/brightline/audit?entityType=client');
    http.expectOne((r) => r.url === auditUrl).flush({ items: [], nextCursor: null });
    await settle(harness.fixture);
    expect(text(main(element))).toContain('No activity matches');
  });

  it('shows the URL filters in the pickers after a load (regression: options render after the select)', async () => {
    const element = await open(
      '/o/brightline/audit?action=job.status_changed&entityType=job&actor=0199f0a0-0000-7000-8000-000000000003',
    );
    http.expectOne((r) => r.url === auditUrl).flush({ items: [entry()], nextCursor: null });
    await settle(harness.fixture);

    const value = (id: string) => (main(element).querySelector(id) as HTMLSelectElement).value;
    expect(value('#audit-action')).toBe('job.status_changed');
    expect(value('#audit-type')).toBe('job');
  });

  it('tells a Member it is for Admins and Owners, without asking the API', async () => {
    const member: SessionUser = {
      ...olivia,
      memberships: [
        { ...olivia.memberships[0]!, role: 'member', permissions: permissionsFor.member },
      ],
    };
    const element = await open('/o/brightline/audit', member);
    await settle(harness.fixture);

    expect(text(main(element))).toContain('The audit log is for Admins and Owners');
    http.expectNone((r) => r.url === auditUrl);
  });
});
