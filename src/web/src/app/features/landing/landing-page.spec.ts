import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { answerDemo, loadSession, olivia, provideTestHttp, settle } from '../../testing/session';

describe('LandingPage', () => {
  let http: HttpTestingController;

  async function open(demo: boolean, signedIn = false): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(signedIn ? olivia : null);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/');
    answerDemo(http, demo ? { resetIntervalHours: 1, nextResetAt: null, accounts: [] } : null);
    await settle(harness.fixture);
    return harness.routeNativeElement as HTMLElement;
  }

  afterEach(() => http.verify());

  const actions = (page: HTMLElement) =>
    [...page.querySelectorAll('section a')].map((a) => [
      a.textContent!.trim(),
      a.getAttribute('href'),
    ]);

  it('leads with "Try the demo" in demo mode, and shows the banner', async () => {
    const page = await open(true);

    expect(page.querySelector('h1')?.textContent).toContain(
      'back office for small service businesses',
    );
    expect(actions(page)).toEqual([
      ['Try the demo', '/sign-in'],
      ['Create your organization', '/sign-up'],
    ]);
    expect(document.body.textContent).toContain('data resets every hour');
  });

  it('offers sign-up and sign-in outside demo mode', async () => {
    const page = await open(false);

    expect(actions(page)).toEqual([
      ['Create your organization', '/sign-up'],
      ['Sign in', '/sign-in'],
    ]);
    expect(page.textContent).not.toContain('demo');
  });

  it('sends a signed-in visitor to their organizations', async () => {
    const page = await open(false, true);

    expect(actions(page)).toEqual([['Go to your organizations', '/orgs']]);
  });
});
