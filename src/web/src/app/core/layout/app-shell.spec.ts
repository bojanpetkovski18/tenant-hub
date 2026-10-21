import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { loadSession, olivia, provideTestHttp } from '../../testing/session';
import { provideCspSafeCdkStyles } from './csp-safe-style-loader';

describe('AppShell', () => {
  let element: HTMLElement;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTestHttp(),
        provideRouter(routes, withComponentInputBinding()),
        provideCspSafeCdkStyles(),
      ],
    });
    localStorage.clear();
    await loadSession(olivia);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/o/brightline/jobs');
    element = harness.routeNativeElement as HTMLElement;
  });

  const menuButton = () =>
    element.querySelector<HTMLButtonElement>('button[aria-label="Open navigation"]')!;
  const drawer = () => element.querySelector<HTMLElement>('#nav-drawer');

  it('renders the organization navigation and marks the current page', () => {
    const links = [...element.querySelectorAll<HTMLAnchorElement>('aside nav a')];

    expect(links.map((link) => link.textContent?.trim())).toEqual([
      'Dashboard',
      'Jobs',
      'Clients',
      'Members',
      'Audit log',
      'Settings',
    ]);
    expect(links.map((link) => link.getAttribute('href'))).toContain('/o/brightline/clients');
    expect(
      links.find((link) => link.getAttribute('aria-current') === 'page')?.textContent?.trim(),
    ).toBe('Jobs');
  });

  it('has a skip link to the main content', () => {
    const skip = element.querySelector<HTMLAnchorElement>('a[href="#main"]');

    expect(skip?.textContent).toContain('Skip to content');
    expect(element.querySelector('main#main')).not.toBeNull();
  });

  // Regression: with <base href="/">, a plain href="#main" resolved to "/#main" and left the page.
  it('skip link moves focus to the main content without navigating', () => {
    const skip = element.querySelector<HTMLAnchorElement>('a[href="#main"]')!;
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });

    skip.dispatchEvent(click);

    expect(click.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(element.querySelector('main#main'));
  });

  it('opens the drawer and closes it with Escape, returning focus to the menu button', async () => {
    expect(drawer()).toBeNull();
    expect(menuButton().getAttribute('aria-expanded')).toBe('false');

    menuButton().focus();
    menuButton().click();
    await harness.fixture.whenStable();

    expect(drawer()).not.toBeNull();
    expect(menuButton().getAttribute('aria-expanded')).toBe('true');
    expect(drawer()?.getAttribute('aria-modal')).toBe('true');

    drawer()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await harness.fixture.whenStable();

    expect(drawer()).toBeNull();
    expect(document.activeElement).toBe(menuButton());
  });

  it('closes the drawer after navigating from it', async () => {
    menuButton().click();
    await harness.fixture.whenStable();

    drawer()!.querySelector<HTMLAnchorElement>('a[href="/o/brightline/clients"]')!.click();
    await harness.fixture.whenStable();

    expect(drawer()).toBeNull();
    expect(element.querySelector('main h1')?.textContent).toContain('Clients');
  });

  it('shows the current organization and switches to another from the menu', async () => {
    const trigger = element.querySelector<HTMLButtonElement>('aside [data-testid="org-switcher"]')!;
    expect(trigger.textContent).toContain('Brightline Cleaning Co.');
    expect(trigger.textContent).toContain('Owner');

    trigger.click();
    await harness.fixture.whenStable();
    const items = [
      ...document.querySelectorAll<HTMLAnchorElement>('[role="menu"] a[role="menuitem"]'),
    ];
    expect(items.map((item) => item.textContent?.trim())).toEqual([
      'Brightline Cleaning Co.',
      'Harbor & Pine Landscaping',
      'All organizations',
    ]);
    expect(items[0]?.getAttribute('aria-current')).toBe('true');

    items[1]!.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/o/harbor-pine/dashboard');
    expect(element.querySelector('[data-testid="current-org"]')?.textContent).toContain(
      'Harbor & Pine',
    );
  });

  it('remembers the organization in use for the next sign-in', () => {
    expect(localStorage.getItem('th-last-org')).toBe('brightline');
  });

  it('shows not found in place for an organization the user does not belong to', async () => {
    await harness.navigateByUrl('/o/someone-elses-org/jobs');
    const page = harness.routeNativeElement as HTMLElement;

    expect(page.querySelector('h1')?.textContent).toContain('Organization not found');
    expect(page.querySelector('aside')).toBeNull();
    expect(TestBed.inject(Router).url).toBe('/o/someone-elses-org/jobs');
    expect(localStorage.getItem('th-last-org')).toBe('brightline');
  });

  // Regression (D023): the CDK used to add its overlay styles as an inline <style>, which the strict CSP blocks.
  it('opening a menu adds no inline style element', async () => {
    const overlayStyles = () =>
      [...document.querySelectorAll('style')].filter((style) =>
        style.textContent?.includes('.cdk-overlay-container'),
      ).length;

    element.querySelector<HTMLButtonElement>('[data-testid="user-menu"]')!.click();
    await harness.fixture.whenStable();

    expect(document.querySelector('[role="menu"]')).not.toBeNull();
    expect(overlayStyles()).toBe(0);
  });
});
