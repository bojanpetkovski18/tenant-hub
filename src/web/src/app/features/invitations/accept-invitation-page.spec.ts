import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { SessionUser } from '../../core/session/session.models';
import { loadSession, olivia, provideTestHttp, settle } from '../../testing/session';
import { InvitationPreview } from '../members/members.api';

const preview = (overrides: Partial<InvitationPreview> = {}): InvitationPreview => ({
  organizationName: 'Brightline Cleaning Co.',
  invitedBy: 'Olivia Bennett',
  role: 'member',
  maskedEmail: 'n***@brightline.example',
  state: 'pending',
  expiresAt: '2026-10-29T09:00:00Z',
  accountExists: false,
  signedInEmailMatches: null,
  memberOfOrganizationId: null,
  ...overrides,
});

describe('accept invitation page', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  async function open(
    answer: InvitationPreview | 'unknown',
    user: SessionUser | null = null,
  ): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
    http = TestBed.inject(HttpTestingController);
    sessionStorage.clear();
    await loadSession(user);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/invite#tok-123');
    const request = http.expectOne('/api/invitations/preview');
    expect(request.request.body).toEqual({ token: 'tok-123' });
    if (answer === 'unknown') {
      request.flush(
        { title: 'Invitation not found.', code: 'invitation-not-found' },
        { status: 404, statusText: 'Not Found' },
      );
    } else {
      request.flush(answer);
    }
    await settle(harness.fixture);
    // Only the page: the public layout around it has buttons of its own (theme toggle).
    return (harness.routeNativeElement as HTMLElement).querySelector('app-accept-invitation-page')!;
  }

  const text = (element: Element) => element.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('lets a new person create their account and join in one step', async () => {
    const element = await open(preview());
    expect(text(element)).toContain(
      'Olivia Bennett invited n***@brightline.example to join as a Member.',
    );

    const name = element.querySelector('#displayName') as HTMLInputElement;
    const password = element.querySelector('#password') as HTMLInputElement;
    name.value = 'Nina Park';
    name.dispatchEvent(new Event('input'));
    password.value = 'Correct-Horse-9';
    password.dispatchEvent(new Event('input'));
    (element.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    await settle(harness.fixture);

    const signUp = http.expectOne('/api/invitations/sign-up');
    expect(signUp.request.body).toEqual({
      token: 'tok-123',
      displayName: 'Nina Park',
      password: 'Correct-Horse-9',
    });
    signUp.flush({
      organizationId: olivia.memberships[0]!.organizationId,
      alreadyMember: false,
      session: { user: olivia },
    });
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/o/brightline/dashboard');
  });

  it('asks someone with an account to sign in first, without the token in the return URL', async () => {
    const element = await open(preview({ accountExists: true }));

    const link = [...element.querySelectorAll('a')].find((a) => text(a) === 'Sign in to accept')!;
    expect(link.getAttribute('href')).toBe('/sign-in?returnUrl=%2Finvite');
    expect(element.querySelector('form')).toBeNull();
    expect(sessionStorage.getItem('th.pending-invitation')).toBe('tok-123');
  });

  it('joins with one click when signed in with the invited email', async () => {
    const element = await open(preview({ signedInEmailMatches: true }), olivia);

    const join = [...element.querySelectorAll('button')].find(
      (b) => text(b) === 'Join Brightline Cleaning Co.',
    )!;
    join.click();
    await settle(harness.fixture);
    http.expectOne('/api/invitations/accept').flush({
      organizationId: olivia.memberships[0]!.organizationId,
      alreadyMember: false,
      session: { user: olivia },
    });
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/o/brightline/dashboard');
    expect(sessionStorage.getItem('th.pending-invitation')).toBeNull();
  });

  it('explains a different signed-in email and offers to sign out', async () => {
    const element = await open(preview({ signedInEmailMatches: false }), olivia);

    expect(text(element)).toContain('This invitation is for a different email address.');
    expect(text(element)).toContain('olivia@brightline.example');
    expect([...element.querySelectorAll('button')].map((b) => text(b))).toEqual(['Sign out']);
  });

  it.each([
    ['expired', 'This invitation has expired.'],
    ['revoked', 'This invitation was withdrawn.'],
    ['accepted', 'This invitation has already been used.'],
  ] as const)('shows a %s link without a form', async (state, message) => {
    const element = await open(preview({ state }));

    expect(text(element)).toContain(message);
    expect(element.querySelector('form')).toBeNull();
    expect(element.querySelectorAll('button').length).toBe(0);
  });

  it('opens the organization for someone who already belongs to it', async () => {
    const element = await open(
      preview({ memberOfOrganizationId: olivia.memberships[0]!.organizationId }),
      olivia,
    );

    expect(text(element)).toContain('You are already a member of Brightline Cleaning Co.');
    [...element.querySelectorAll('button')].find((b) => text(b).startsWith('Open'))!.click();
    await settle(harness.fixture);
    expect(TestBed.inject(Router).url).toBe('/o/brightline/dashboard');
  });

  it('says so when the link is unknown', async () => {
    const element = await open('unknown');

    expect(text(element.querySelector('h1')!)).toBe('Invitation not found');
  });
});
