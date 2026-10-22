import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  Router,
  provideRouter,
  withComponentInputBinding,
  withRouterConfig,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { SessionUser } from '../../core/session/session.models';
import {
  loadSession,
  olivia,
  permissionsFor,
  provideTestHttp,
  settle,
} from '../../testing/session';

const orgId = olivia.memberships[0]!.organizationId;
const listUrl = `/api/orgs/${orgId}/clients`;
const page = (items: { id: string; name: string }[], total = items.length) => ({
  items: items.map((c) => ({
    ...c,
    contactName: null,
    email: null,
    phone: null,
    status: 'active',
    updatedAt: '2026-10-22T09:00:00Z',
  })),
  page: 1,
  pageSize: 25,
  total,
});

describe('ClientsListPage', () => {
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
        provideCspSafeCdkStyles(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(user);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    return harness.routeNativeElement as HTMLElement;
  }

  const listRequest = (): TestRequest => http.expectOne((r) => r.url === listUrl);

  it('sends the query from the URL to the API', async () => {
    await open('/o/brightline/clients?q=acme&status=archived&sort=-updated&page=2');

    const request = listRequest();
    expect(request.request.params.get('q')).toBe('acme');
    expect(request.request.params.get('status')).toBe('archived');
    expect(request.request.params.get('sort')).toBe('-updated');
    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.get('pageSize')).toBe('25');
  });

  it('falls back to the defaults for unknown values in the URL', async () => {
    await open('/o/brightline/clients?status=bogus&sort=evil&page=-3');

    const request = listRequest();
    expect([
      request.request.params.get('status'),
      request.request.params.get('sort'),
      request.request.params.get('page'),
    ]).toEqual(['active', 'name', '1']);
  });

  it('sorts from the header: the URL changes and the header announces the order', async () => {
    const element = await open('/o/brightline/clients');
    listRequest().flush(page([{ id: 'c1', name: 'Acme Offices' }]));
    await settle(harness.fixture);
    const nameHeader = () => element.querySelector<HTMLElement>('th[appSortHeader="name"]')!;
    expect(nameHeader().getAttribute('aria-sort')).toBe('ascending');

    nameHeader().querySelector('button')!.click();
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/o/brightline/clients?sort=-name');
    listRequest().flush(page([{ id: 'c1', name: 'Acme Offices' }]));
    await settle(harness.fixture);
    expect(nameHeader().getAttribute('aria-sort')).toBe('descending');
  });

  it('shows rows linking to each client, and the total', async () => {
    const element = await open('/o/brightline/clients');
    listRequest().flush(
      page(
        [
          { id: 'c1', name: 'Acme Offices' },
          { id: 'c2', name: 'Birch Dental' },
        ],
        2,
      ),
    );
    await settle(harness.fixture);

    const links = [...element.querySelectorAll<HTMLAnchorElement>('table tbody a')];
    expect(links.map((a) => [a.textContent?.trim(), a.getAttribute('href')])).toEqual([
      ['Acme Offices', '/o/brightline/clients/c1'],
      ['Birch Dental', '/o/brightline/clients/c2'],
    ]);
    expect(element.textContent).toContain('Showing 1–2 of 2');
  });

  it('distinguishes "no clients yet" from "no matches"', async () => {
    let element = await open('/o/brightline/clients');
    listRequest().flush(page([]));
    await settle(harness.fixture);
    expect(element.textContent).toContain('No clients yet');
    expect(element.textContent).toContain('Add your first client');

    TestBed.resetTestingModule();
    element = await open('/o/brightline/clients?q=zzz');
    listRequest().flush(page([]));
    await settle(harness.fixture);
    expect(element.textContent).toContain('No clients match');
  });

  it('shows the error and retries the same request', async () => {
    const element = await open('/o/brightline/clients?q=acme');
    listRequest().flush(
      { title: 'Something went wrong on our side.' },
      { status: 500, statusText: 'Error' },
    );
    await settle(harness.fixture);
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Something went wrong');

    element.querySelector<HTMLButtonElement>('[role="alert"] button')!.click();
    expect(listRequest().request.params.get('q')).toBe('acme');
  });

  it('shows a Viewer no way to add clients', async () => {
    const viewer = {
      ...olivia,
      memberships: [
        { ...olivia.memberships[0]!, role: 'viewer' as const, permissions: permissionsFor.viewer },
      ],
    };
    const element = await open('/o/brightline/clients', viewer);
    listRequest().flush(page([]));
    await settle(harness.fixture);

    expect(element.textContent).not.toContain('New client');
    expect(element.textContent).not.toContain('Add your first client');
  });
});
