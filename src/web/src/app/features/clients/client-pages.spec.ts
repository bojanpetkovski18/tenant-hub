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
const clientUrl = `/api/orgs/${orgId}/clients/c1`;
const acme = {
  id: 'c1',
  name: 'Acme Offices',
  contactName: 'Dana Reyes',
  email: 'facilities@acme.example',
  phone: null,
  address: null,
  notes: null,
  status: 'active',
  createdAt: '2026-10-01T09:00:00Z',
  updatedAt: '2026-10-22T09:00:00Z',
  version: 41,
};
const viewer = {
  ...olivia,
  memberships: [
    { ...olivia.memberships[0]!, role: 'viewer' as const, permissions: permissionsFor.viewer },
  ],
};

describe('client pages', () => {
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

  const buttonsIn = (element: HTMLElement) =>
    [...element.querySelectorAll('main button, main a.th-button-secondary')].map((b) =>
      b.textContent?.trim(),
    );

  describe('detail', () => {
    it('offers edit, archive and delete to an Owner, nothing to a Viewer', async () => {
      let element = await open('/o/brightline/clients/c1');
      http.expectOne(clientUrl).flush(acme);
      await settle(harness.fixture);
      // Clients actions, then the jobs section's link to a new job for this client (jobs.create).
      expect(buttonsIn(element)).toEqual(['Edit', 'Archive', 'Delete', 'New job']);

      TestBed.resetTestingModule();
      element = await open('/o/brightline/clients/c1', viewer);
      http.expectOne(clientUrl).flush(acme);
      await settle(harness.fixture);
      expect(element.querySelector('h1')?.textContent).toContain('Acme Offices');
      expect(buttonsIn(element)).toEqual([]);
    });

    it('explains why a delete was refused', async () => {
      const element = await open('/o/brightline/clients/c1');
      http.expectOne(clientUrl).flush(acme);
      await settle(harness.fixture);

      element.querySelectorAll<HTMLButtonElement>('main button')[1]!.click(); // Delete
      await settle(harness.fixture);
      const confirm = [
        ...document.querySelectorAll<HTMLButtonElement>('[role="alertdialog"] button'),
      ].find((b) => b.textContent?.includes('Delete client'))!;
      confirm.click();
      await settle(harness.fixture);
      http
        .expectOne((r) => r.method === 'DELETE' && r.url === clientUrl)
        .flush(
          { title: 'This client has jobs; archive it instead.', code: 'client-has-jobs' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle(harness.fixture);

      expect(element.querySelector('[role="alert"]')?.textContent).toContain('archive it instead');
    });

    it('says when the client does not exist (or belongs to another organization)', async () => {
      const element = await open('/o/brightline/clients/c1');
      http
        .expectOne(clientUrl)
        .flush(
          { title: 'Client not found.', code: 'client-not-found' },
          { status: 404, statusText: 'Not Found' },
        );
      await settle(harness.fixture);

      expect(element.textContent).toContain('Client not found');
    });
  });

  describe('form', () => {
    async function fillAndSave(element: HTMLElement, name: string): Promise<void> {
      const input = element.querySelector<HTMLInputElement>('#name')!;
      input.value = name;
      input.dispatchEvent(new Event('input'));
      element.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle(harness.fixture);
    }

    it('puts a duplicate-name conflict on the name field', async () => {
      const element = await open('/o/brightline/clients/new');

      await fillAndSave(element, 'Acme Offices');
      http
        .expectOne((r) => r.method === 'POST' && r.url === `/api/orgs/${orgId}/clients`)
        .flush(
          { title: 'Another active client already has this name.', code: 'client-name-taken' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle(harness.fixture);

      expect(element.querySelector('#name-error')?.textContent).toContain('already has this name');
      expect(document.activeElement?.id).toBe('name');
    });

    it('sends the version it loaded, and on a conflict offers to reload', async () => {
      const element = await open('/o/brightline/clients/c1/edit');
      http.expectOne(clientUrl).flush(acme);
      await settle(harness.fixture);

      await fillAndSave(element, 'Acme Group');
      const put = http.expectOne((r) => r.method === 'PUT' && r.url === clientUrl);
      expect(put.request.body).toMatchObject({ name: 'Acme Group', version: 41 });
      put.flush(
        { title: 'Someone else changed this client.', code: 'concurrency-conflict' },
        { status: 409, statusText: 'Conflict' },
      );
      await settle(harness.fixture);

      const dialog = document.querySelector('[role="alertdialog"]');
      expect(dialog?.textContent).toContain('changed by someone else');
      [...dialog!.querySelectorAll<HTMLButtonElement>('button')]
        .find((b) => b.textContent?.includes('Reload'))!
        .click();
      await settle(harness.fixture);
      http.expectOne(clientUrl).flush({ ...acme, name: 'Acme Holdings', version: 42 });
      await settle(harness.fixture);

      expect(element.querySelector<HTMLInputElement>('#name')!.value).toBe('Acme Holdings');
    });

    it('opens the new client after creating it', async () => {
      const element = await open('/o/brightline/clients/new');

      await fillAndSave(element, 'Birch Dental');
      http.expectOne((r) => r.method === 'POST').flush({ ...acme, id: 'c9', name: 'Birch Dental' });
      await settle(harness.fixture);

      expect(TestBed.inject(Router).url).toBe('/o/brightline/clients/c9');
    });
  });
});
