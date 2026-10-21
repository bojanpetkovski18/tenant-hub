import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { TitleStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { AppTitleStrategy } from './core/app-title-strategy';
import { loadSession, olivia, provideTestHttp } from './testing/session';

describe('app routes', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestHttp(),
        provideRouter(routes, withComponentInputBinding()),
        { provide: TitleStrategy, useClass: AppTitleStrategy },
      ],
    });
  });

  it('renders the not-found page for an unknown URL', async () => {
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/no/such/page');

    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain(
      'Page not found',
    );
    expect(TestBed.inject(Title).getTitle()).toBe('Page not found · TenantHub');
  });

  it('redirects an organization root to its dashboard inside the app shell', async () => {
    await loadSession(olivia);
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/o/brightline');

    const element = harness.routeNativeElement as HTMLElement;
    expect(element.querySelector('h1')?.textContent).toContain('Dashboard');
    expect(element.querySelector('[data-testid="current-org"]')?.textContent).toContain(
      'Brightline Cleaning Co.',
    );
    expect(TestBed.inject(Title).getTitle()).toBe('Dashboard · TenantHub');
  });

  it.each(['/', '/sign-in', '/sign-up', '/invite/abc'])(
    'renders a page for the public route %s when signed out',
    async (url) => {
      await loadSession(null);
      const harness = await RouterTestingHarness.create();

      await harness.navigateByUrl(url);

      expect(harness.routeNativeElement?.querySelector('main h1')).not.toBeNull();
    },
  );

  it.each(['/orgs', '/account'])('renders %s for a signed-in user', async (url) => {
    await loadSession(olivia);
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl(url);

    expect(harness.routeNativeElement?.querySelector('main h1')).not.toBeNull();
  });

  it('explains on the picker that access to an organization was lost', async () => {
    await loadSession(olivia);
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/orgs?notice=access-lost');

    expect(harness.routeNativeElement?.querySelector('[role="status"]')?.textContent).toContain(
      'You no longer have access to that organization.',
    );
  });
});
