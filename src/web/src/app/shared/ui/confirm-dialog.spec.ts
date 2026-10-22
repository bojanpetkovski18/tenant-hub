import { TestBed } from '@angular/core/testing';
import { provideCspSafeCdkStyles } from '../../core/layout/csp-safe-style-loader';
import { Confirm } from './confirm-dialog';

describe('Confirm', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideCspSafeCdkStyles()] }));

  const dialog = () => document.querySelector<HTMLElement>('[role="alertdialog"]');
  const button = (label: string) =>
    [...dialog()!.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(label),
    )!;

  it('resolves true only for the confirm button', async () => {
    const confirm = TestBed.inject(Confirm);

    const yes = confirm.ask({ title: 'Archive?', message: 'Sure?', confirmLabel: 'Archive' });
    TestBed.tick();
    button('Archive').click();
    expect(await yes).toBe(true);

    const no = confirm.ask({ title: 'Archive?', message: 'Sure?', confirmLabel: 'Archive' });
    TestBed.tick();
    button('Cancel').click();
    expect(await no).toBe(false);
  });

  it('labels the dialog with its title and message', () => {
    void TestBed.inject(Confirm).ask({
      title: 'Delete Acme?',
      message: 'This cannot be undone.',
      confirmLabel: 'Delete',
    });
    TestBed.tick();

    const element = dialog()!;
    expect(
      document.getElementById(element.getAttribute('aria-labelledby')!)?.textContent,
    ).toContain('Delete Acme?');
    expect(
      document.getElementById(element.getAttribute('aria-describedby')!)?.textContent,
    ).toContain('cannot be undone');
    expect(element.getAttribute('aria-modal')).toBe('true');
  });

  // Regression (D027): the CDK dialog container's own component styles became an inline <style> that the strict
  // CSP blocked in the production build.
  it('opens without adding an inline style element', () => {
    const containerStyles = () =>
      [...document.querySelectorAll('style')].filter((s) =>
        s.textContent?.includes('.cdk-dialog-container'),
      ).length;

    void TestBed.inject(Confirm).ask({
      title: 'Archive?',
      message: 'Sure?',
      confirmLabel: 'Archive',
    });
    TestBed.tick();

    expect(dialog()).not.toBeNull();
    expect(containerStyles()).toBe(0);
  });
});
