import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SessionUser } from '../session/session.models';
import { loadSession, olivia, provideTestHttp, settle } from '../../testing/session';
import { VerifyEmailBanner } from './verify-email-banner';

describe('VerifyEmailBanner', () => {
  let http: HttpTestingController;
  const unverified: SessionUser = { ...olivia, emailVerified: false };

  async function render(user: SessionUser | null, demoMode = false) {
    TestBed.configureTestingModule({ providers: [provideTestHttp()] });
    http = TestBed.inject(HttpTestingController);
    await loadSession(user, demoMode);
    const fixture = TestBed.createComponent(VerifyEmailBanner);
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => http.verify());

  it('asks an unverified account to confirm its address', async () => {
    const { element } = await render(unverified);

    expect(element.querySelector('h2')?.textContent).toContain('Confirm your email address');
    expect(element.textContent).toContain('olivia@brightline.example');
    expect(element.textContent).toContain('you cannot invite people');
  });

  it('is hidden for verified accounts and in the demo', async () => {
    expect((await render(olivia)).element.textContent?.trim()).toBe('');
    TestBed.resetTestingModule();
    expect((await render(unverified, true)).element.textContent?.trim()).toBe('');
  });

  it('sends the link again and says so', async () => {
    const { fixture, element } = await render(unverified);

    element.querySelector('button')!.click();
    await settle(fixture);
    expect(element.querySelector('button')?.textContent).toContain('Sending…');
    http
      .expectOne('/api/me/verification-email')
      .flush(null, { status: 202, statusText: 'Accepted' });
    await settle(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain(
      'Sent. Check your inbox',
    );
  });

  it('explains the rate limit in words', async () => {
    const { fixture, element } = await render(unverified);

    element.querySelector('button')!.click();
    await settle(fixture);
    http
      .expectOne('/api/me/verification-email')
      .flush({ title: 'Too many attempts.' }, { status: 429, statusText: 'Too Many Requests' });
    await settle(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain('Wait a few minutes');
  });
});
