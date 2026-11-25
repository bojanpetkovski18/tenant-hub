import AxeBuilder from '@axe-core/playwright';
import { type APIRequestContext, type BrowserContext, type Page, expect } from '@playwright/test';

// Shared by both journeys: axe, Mailpit, sign-up, and a watch on the console for errors and CSP violations.

export const mailpit = process.env['E2E_MAILPIT_URL'] ?? 'http://localhost:8026';
export const password = 'E2e-Journey-Pass-1!';

/** A suffix that makes every run's people and organizations new, so runs never depend on each other or on the seed. */
export const newRun = (): string => `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;

/** No serious or critical axe findings on the page as it is now. */
export async function expectAccessible(page: Page, where: string): Promise<void> {
  // A dialog fading in has blended colours for a moment; contrast is judged on the settled page.
  await page.waitForFunction(() => document.getAnimations().every((a) => a.playState !== 'running'));
  const { violations } = await new AxeBuilder({ page }).analyze();
  const serious = violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(
    serious.map((v) => `${v.id}: ${v.nodes[0]?.target.join(' ')} ${v.nodes[0]?.any[0]?.message ?? ''}`),
    `axe on ${where}`,
  ).toEqual([]);
}

export async function signUp(page: Page, name: string, email: string, organization: string): Promise<string> {
  await page.goto('/sign-up');
  await expectAccessible(page, 'sign up');
  await page.getByLabel('Your name').fill(name);
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByLabel('Organization name').fill(organization);
  await page.getByRole('button', { name: /create/i }).click();
  await page.waitForURL(/\/o\/[^/]+\/dashboard$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  return new URL(page.url()).pathname.split('/')[2]!;
}

/** The link to the SPA page `page` (`/page#token`) in the email Mailpit caught for this address with this subject. */
export async function emailedLink(
  request: APIRequestContext,
  email: string,
  subject: string,
  page: string,
): Promise<string> {
  const search = `${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${email}" subject:"${subject}"`)}`;
  await expect
    .poll(async () => (await (await request.get(search)).json()).messages_count, { timeout: 30_000 })
    .toBeGreaterThan(0);
  const { messages } = await (await request.get(search)).json();
  const message = await (await request.get(`${mailpit}/api/v1/message/${messages[0].ID}`)).json();
  const escaped = page.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const link = (message.Text as string).match(new RegExp(`https?://\\S+/${escaped}#[\\w-]+`))?.[0];
  expect(link, `${page} link in the email`).toBeTruthy();
  return link!;
}

/**
 * Console errors, uncaught exceptions and CSP violations on every page of a browser context. Chromium reports a CSP
 * violation as a console error ("Refused to …"); the page's own `securitypolicyviolation` event catches the rest.
 */
export class ConsoleWatch {
  readonly problems: string[] = [];

  async attach(context: BrowserContext, who: string): Promise<void> {
    await context.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (e) =>
        console.error(`CSP violation: ${e.violatedDirective} blocked ${e.blockedURI || 'inline'}`),
      );
    });
    context.on('page', (page) => this.watch(page, who));
    for (const page of context.pages()) {
      this.watch(page, who);
    }
  }

  private watch(page: Page, who: string): void {
    page.on('console', (message) => {
      if (message.type() === 'error') {
        this.problems.push(`${who} ${new URL(page.url()).pathname}: ${message.text()}`);
      }
    });
    page.on('pageerror', (error) => this.problems.push(`${who} ${new URL(page.url()).pathname}: ${error.message}`));
  }
}

/** The staff session's antiforgery token, for requests the test makes as that person (D009). */
export async function xsrfHeader(context: BrowserContext): Promise<Record<string, string>> {
  const cookie = (await context.cookies()).find((c) => c.name === 'XSRF-TOKEN' && c.path === '/');
  expect(cookie, 'the staff XSRF-TOKEN cookie').toBeTruthy();
  return { 'X-XSRF-TOKEN': cookie!.value };
}
