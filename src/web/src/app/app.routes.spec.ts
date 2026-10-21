import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { TitleStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { AppTitleStrategy } from './core/app-title-strategy';

describe('app routes', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
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
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/o/brightline');

    const element = harness.routeNativeElement as HTMLElement;
    expect(element.querySelector('h1')?.textContent).toContain('Dashboard');
    expect(element.querySelector('[data-testid="current-org"]')?.textContent).toContain(
      'brightline',
    );
    expect(TestBed.inject(Title).getTitle()).toBe('Dashboard · TenantHub');
  });

  it.each(['/', '/sign-in', '/sign-up', '/orgs', '/account', '/invite/abc'])(
    'renders a page for the public route %s',
    async (url) => {
      const harness = await RouterTestingHarness.create();

      await harness.navigateByUrl(url);

      expect(harness.routeNativeElement?.querySelector('main h1')).not.toBeNull();
    },
  );
});
