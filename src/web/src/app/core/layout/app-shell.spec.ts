import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';

describe('AppShell', () => {
  let element: HTMLElement;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes, withComponentInputBinding())],
    });
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
});
