import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
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
import { Invitation, Member } from './members.api';

const orgId = olivia.memberships[0]!.organizationId;
const membersUrl = `/api/orgs/${orgId}/members`;
const invitationsUrl = `/api/orgs/${orgId}/invitations`;

const member = (overrides: Partial<Member>): Member => ({
  userId: 'u0',
  displayName: 'Someone',
  email: 'someone@brightline.example',
  role: 'member',
  joinedAt: '2026-10-01T09:00:00Z',
  isYou: false,
  canChangeRole: false,
  canRemove: false,
  canLeave: false,
  ...overrides,
});

const asOwner: Member[] = [
  member({ userId: 'u1', displayName: 'Olivia Bennett', role: 'owner', isYou: true }),
  member({
    userId: 'u2',
    displayName: 'Adam Clarke',
    role: 'admin',
    canChangeRole: true,
    canRemove: true,
  }),
  member({
    userId: 'u3',
    displayName: 'Maya Torres',
    role: 'member',
    canChangeRole: true,
    canRemove: true,
  }),
];

const pending: Invitation = {
  id: 'i1',
  email: 'nina@brightline.example',
  role: 'member',
  state: 'pending',
  createdAt: '2026-10-20T09:00:00Z',
  expiresAt: '2026-10-27T09:00:00Z',
  invitedBy: 'Olivia Bennett',
};

const viewer: SessionUser = {
  ...olivia,
  memberships: [
    {
      ...olivia.memberships[0]!,
      role: 'viewer',
      permissions: permissionsFor.viewer,
      grantableRoles: [],
      invitableRoles: [],
    },
  ],
};

describe('members page', () => {
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

  const text = (element: Element | null) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const main = (element: HTMLElement) => element.querySelector('main') ?? element;
  const button = (root: ParentNode, label: string) =>
    [...root.querySelectorAll('button')].find((b) => text(b).startsWith(label));

  it('gives an Owner the actions the API allows on each row and explains why the last Owner cannot leave', async () => {
    const element = await open('/o/brightline/members');
    http.expectOne(membersUrl).flush(asOwner);
    await settle(harness.fixture);

    const rows = [...main(element).querySelectorAll('li')];
    expect(rows.map((row) => row.querySelector('select') !== null)).toEqual([false, true, true]);
    expect([...rows[1]!.querySelectorAll('option')].map((o) => text(o))).toEqual([
      'Owner',
      'Admin',
      'Member',
      'Viewer',
    ]);
    expect(button(rows[0]!, 'Remove')).toBeUndefined();
    expect(button(rows[2]!, 'Remove')).toBeDefined();
    const leave = button(rows[0]!, 'Leave organization')!;
    expect(leave.disabled).toBe(true);
    expect(leave.getAttribute('aria-describedby')).toBe('last-owner-hint');
    expect(text(element.querySelector('#last-owner-hint'))).toContain('You are the last Owner');
    expect(button(main(element), 'Invite')).toBeDefined();
  });

  it('shows a Viewer the list with roles only', async () => {
    const element = await open('/o/brightline/members', viewer);
    http.expectOne(membersUrl).flush([
      member({ userId: 'u1', displayName: 'Olivia Bennett', role: 'owner' }),
      member({
        userId: 'u4',
        displayName: 'Victor Lindqvist',
        role: 'viewer',
        isYou: true,
        canLeave: true,
      }),
    ]);
    await settle(harness.fixture);

    expect(main(element).querySelector('select')).toBeNull();
    expect(main(element).querySelector('nav[aria-label="Member lists"]')).toBeNull();
    expect(button(main(element), 'Invite')).toBeUndefined();
    expect(button(main(element), 'Leave organization')!.disabled).toBe(false);
  });

  it('changes a role only after confirmation, then reloads the list', async () => {
    const element = await open('/o/brightline/members');
    http.expectOne(membersUrl).flush(asOwner);
    await settle(harness.fixture);

    const select = main(element).querySelectorAll('li')[2]!.querySelector('select')!;
    select.value = 'viewer';
    select.dispatchEvent(new Event('change'));
    await settle(harness.fixture);
    expect(text(document.querySelector('#confirm-title'))).toBe('Make Maya Torres a Viewer?');
    button(document.body, 'Change role')!.click();
    await settle(harness.fixture);

    const change = http.expectOne(`${membersUrl}/u3`);
    expect(change.request.method).toBe('PATCH');
    expect(change.request.body).toEqual({ role: 'viewer' });
    change.flush(null, { status: 204, statusText: 'No Content' });
    await settle(harness.fixture);
    http
      .expectOne(membersUrl)
      .flush(asOwner.map((m) => (m.userId === 'u3' ? { ...m, role: 'viewer' } : m)));
    await settle(harness.fixture);

    expect(text(main(element).querySelector('[role="status"]'))).toBe(
      'Maya Torres is now a Viewer.',
    );
  });

  it('invites with the roles the session allows and shows the link when email is off', async () => {
    const element = await open('/o/brightline/members');
    http.expectOne(membersUrl).flush(asOwner);
    await settle(harness.fixture);

    button(main(element), 'Invite')!.click();
    await settle(harness.fixture);
    const roleOptions = [...document.querySelectorAll('#invite-role option')].map((o) => text(o));
    expect(roleOptions).toEqual(['Admin', 'Member', 'Viewer']);
    expect((document.querySelector('#invite-role') as HTMLSelectElement).value).toBe('member');

    const email = document.querySelector('#invite-email') as HTMLInputElement;
    email.value = 'nina@brightline.example';
    email.dispatchEvent(new Event('input'));
    button(document.body, 'Send invitation')!.click();
    await settle(harness.fixture);
    const invite = http.expectOne(invitationsUrl);
    expect(invite.request.body).toEqual({ email: 'nina@brightline.example', role: 'member' });
    invite.flush({
      invitation: pending,
      delivery: 'link',
      link: 'http://localhost:4200/invite#tok',
    });
    await settle(harness.fixture);

    expect(text(document.querySelector('#copy-link-title'))).toBe('Share the invitation link');
    expect((document.querySelector('#invite-link') as HTMLInputElement).value).toBe(
      'http://localhost:4200/invite#tok',
    );
  });

  it('lists pending invitations and revokes one after confirmation', async () => {
    const element = await open('/o/brightline/members?tab=pending');
    http
      .expectOne(invitationsUrl)
      .flush([
        pending,
        { ...pending, id: 'i2', email: 'old@brightline.example', state: 'expired' },
      ]);
    await settle(harness.fixture);

    const rows = [...main(element).querySelectorAll('li')];
    expect(rows.map((row) => text(row.querySelector('p')))).toEqual([
      'nina@brightline.example',
      'old@brightline.example',
    ]);
    expect(text(rows[1]!)).toContain('Expired');
    expect(main(element).querySelector('a[aria-current="page"]')?.textContent?.trim()).toBe(
      'Pending invitations',
    );

    button(rows[0]!, 'Revoke')!.click();
    await settle(harness.fixture);
    button(document.body, 'Revoke invitation')!.click();
    await settle(harness.fixture);

    const revoke = http.expectOne(`${invitationsUrl}/i1`);
    expect(revoke.request.method).toBe('DELETE');
    revoke.flush(null, { status: 204, statusText: 'No Content' });
    await settle(harness.fixture);
    http.expectOne(invitationsUrl).flush([]);
    await settle(harness.fixture);
    expect(text(main(element))).toContain('No pending invitations');
  });
});
