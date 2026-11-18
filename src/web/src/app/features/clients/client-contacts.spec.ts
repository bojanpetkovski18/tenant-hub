import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { SessionUser } from '../../core/session/session.models';
import {
  loadSession,
  olivia,
  permissionsFor,
  provideTestHttp,
  settle,
} from '../../testing/session';
import { ClientContacts } from './client-contacts';

const org = olivia.memberships[0]!.organizationId;
const contactsUrl = `/api/orgs/${org}/clients/c1/contacts`;

const member: SessionUser = {
  ...olivia,
  memberships: [{ ...olivia.memberships[0]!, role: 'member', permissions: permissionsFor.member }],
};

describe('client contacts', () => {
  let fixture: ComponentFixture<ClientContacts>;
  let http: HttpTestingController;

  async function render(user: SessionUser = olivia): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter([]), provideCspSafeCdkStyles()],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(user);
    fixture = TestBed.createComponent(ClientContacts);
    fixture.componentRef.setInput('slug', 'brightline');
    fixture.componentRef.setInput('organizationId', org);
    fixture.componentRef.setInput('clientId', 'c1');
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  const text = (element: Element | null | undefined) =>
    element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const dana = {
    id: 'k1',
    clientId: 'c1',
    name: 'Dana Reyes',
    email: 'dana@acme.example',
    createdAt: '2026-11-18T09:00:00Z',
    lastSignInAt: null,
  };

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('lists contacts, adds one and announces it', async () => {
    const element = await render();
    http.expectOne(contactsUrl).flush([dana]);
    await settle(fixture);
    expect(text(element.querySelector('ul li'))).toContain('Dana Reyes');
    expect(text(element.querySelector('ul li'))).toContain('not signed in yet');

    const name = element.querySelector<HTMLInputElement>('#contact-name')!;
    const email = element.querySelector<HTMLInputElement>('#contact-email')!;
    name.value = 'Tomas Varga';
    name.dispatchEvent(new Event('input'));
    email.value = 'tomas@acme.example';
    email.dispatchEvent(new Event('input'));
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle(fixture);
    const added = http.expectOne((r) => r.url === contactsUrl && r.method === 'POST');
    expect(added.request.body).toEqual({ name: 'Tomas Varga', email: 'tomas@acme.example' });
    added.flush({ ...dana, id: 'k2', name: 'Tomas Varga', email: 'tomas@acme.example' });
    await settle(fixture);
    http.expectOne(contactsUrl).flush([dana]);
    await settle(fixture);

    expect(text(element.querySelector('[aria-live="polite"]'))).toBe(
      'Added Tomas Varga. They get an email explaining how to sign in.',
    );
  });

  it('removes a contact after asking', async () => {
    const element = await render();
    http.expectOne(contactsUrl).flush([dana]);
    await settle(fixture);

    [...element.querySelectorAll('button')].find((b) => text(b).startsWith('Remove'))!.click();
    await settle(fixture);
    expect(text(document.querySelector('#confirm-title'))).toBe('Remove Dana Reyes?');
    [...document.querySelectorAll('[role="alertdialog"] button')]
      .find((b) => text(b) === 'Remove contact')!
      .dispatchEvent(new MouseEvent('click'));
    await settle(fixture);

    http
      .expectOne((r) => r.method === 'DELETE' && r.url === `/api/orgs/${org}/contacts/k1`)
      .flush(null);
  });

  it('shows members the contacts but no add or remove', async () => {
    const element = await render(member);
    http.expectOne(contactsUrl).flush([dana]);
    await settle(fixture);

    expect(text(element)).toContain('Dana Reyes');
    expect(element.querySelector('form')).toBeNull();
    expect([...element.querySelectorAll('button')].some((b) => text(b).startsWith('Remove'))).toBe(
      false,
    );
  });

  it('says which plan has the portal, with a link for whoever can change the plan', async () => {
    const element = await render();
    http.expectOne(contactsUrl).flush(
      {
        title:
          'The client portal is part of the Team plan. An Owner can change the plan in Settings.',
        code: 'plan-feature-unavailable',
        feature: 'portal',
        plan: 'team',
      },
      { status: 403, statusText: 'Forbidden' },
    );
    await settle(fixture);

    expect(text(element)).toContain('The client portal is part of the Team plan.');
    expect(element.querySelector('a[href="/o/brightline/settings#plan"]')).not.toBeNull();
    expect(element.querySelector('form')).toBeNull();
  });
});
