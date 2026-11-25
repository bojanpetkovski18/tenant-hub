import { expect, test } from '@playwright/test';
import { emailedLink, expectAccessible, newRun, password, signUp } from './support';

// SPEC 10, criterion 10: the main journey through the real production image, with the email read from Mailpit.
// Every run uses new people and organizations, so runs never depend on each other or on the seed.

const run = newRun();

test('a team runs a job from sign-up to the audit log', async ({ browser }) => {
  const ownerEmail = `owner-${run}@e2e.example`;
  const mateEmail = `mate-${run}@e2e.example`;
  const owner = await (await browser.newContext()).newPage();

  // 1. Sign up: a new account and its organization.
  const slug = await signUp(owner, 'Erin Owner', ownerEmail, `E2E Cleaning ${run}`);
  await expectAccessible(owner, 'dashboard');

  // 1b. With email on, inviting needs a confirmed address (SPEC-V2 4.1): the banner asks, the emailed link confirms.
  await expect(owner.getByRole('heading', { name: 'Confirm your email address' })).toBeVisible();
  await owner.goto(await emailedLink(owner.request, ownerEmail, 'Confirm your email address', 'verify-email'));
  await expect(owner.getByRole('heading', { name: 'Email address confirmed' })).toBeVisible();
  await expectAccessible(owner, 'email confirmed');

  // 2. Invite a teammate as a Member.
  await owner.goto(`/o/${slug}/members`);
  await expectAccessible(owner, 'members');
  await owner.getByRole('button', { name: 'Invite', exact: true }).first().click();
  const dialog = owner.getByRole('dialog');
  await dialog.getByLabel('Email').fill(mateEmail);
  await dialog.getByLabel('Role').selectOption({ label: 'Member' });
  await expectAccessible(owner, 'invite dialog');
  await dialog.getByRole('button', { name: 'Send invitation' }).click();
  await expect(owner.getByRole('dialog')).toBeHidden();
  await expect(owner.getByText(mateEmail)).toBeVisible();

  // 3. The teammate opens the emailed link in their own browser and joins.
  const link = await emailedLink(owner.request, mateEmail, 'invited you', 'invite');
  const mate = await (await browser.newContext()).newPage();
  await mate.goto(link);
  await mate.getByLabel('Your name').fill('Mo Mate');
  await mate.getByLabel('Password', { exact: true }).fill(password);
  await expectAccessible(mate, 'accept invitation');
  await mate.getByRole('button', { name: 'Create account and join' }).click();
  await mate.waitForURL(new RegExp(`/o/${slug}/`));

  // 4. The owner adds a client.
  await owner.goto(`/o/${slug}/clients/new`);
  await owner.locator('#name').fill('Harbour View Offices');
  await expectAccessible(owner, 'new client');
  await owner.getByRole('button', { name: 'Create client' }).click();
  await expect(owner.getByRole('status')).toContainText('Client created.');

  // 5. The owner plans a job for the teammate.
  await owner.goto(`/o/${slug}/jobs/new`);
  await owner.getByLabel('Title').fill('Window cleaning');
  await owner.getByLabel('Client').selectOption({ label: 'Harbour View Offices' });
  await owner.getByLabel('Assignee').selectOption({ label: 'Mo Mate' });
  await expectAccessible(owner, 'new job');
  await owner.getByRole('button', { name: 'Create job' }).click();
  await expect(owner.getByRole('status')).toContainText('Job created.');
  const jobPath = new URL(owner.url()).pathname;

  // 6. The teammate starts and completes it.
  await mate.goto(jobPath);
  await expectAccessible(mate, 'job');
  await mate.getByRole('button', { name: 'Start' }).click();
  await expect(mate.getByRole('button', { name: 'Complete' })).toBeVisible();
  await mate.getByRole('button', { name: 'Complete' }).click();
  await expect(mate.getByText('Completed', { exact: true }).first()).toBeVisible();

  // 7. The owner sees all of it in the audit log, attributed.
  await owner.goto(`/o/${slug}/audit`);
  const log = owner.getByRole('main');
  await expect(log).toContainText(`${mateEmail} accepted the invitation and joined as Member`);
  await expect(log).toContainText('Created client “Harbour View Offices”');
  await expect(log).toContainText('Created JOB-0001 “Window cleaning”');
  await expect(log).toContainText('Moved JOB-0001 from In progress to Completed');
  await expect(log).toContainText('Mo Mate');
  await expectAccessible(owner, 'audit log');
});

test("another organization's address shows not found, and its data stays out of reach", async ({ browser }) => {
  const other = await (await browser.newContext()).newPage();
  const otherSlug = await signUp(other, 'Olga Other', `other-${run}@e2e.example`, `E2E Other ${run}`);
  const otherId = (await (await other.request.get('/api/auth/session')).json()).user.memberships[0].organizationId;

  const outsider = await (await browser.newContext()).newPage();
  await signUp(outsider, 'Una Outsider', `outsider-${run}@e2e.example`, `E2E Outsider ${run}`);

  await outsider.goto(`/o/${otherSlug}/dashboard`);
  await expect(outsider.getByRole('heading', { level: 1, name: 'Organization not found' })).toBeVisible();
  await expectAccessible(outsider, 'organization not found');
  expect((await outsider.request.get(`/api/orgs/${otherId}/clients`)).status()).toBe(404);
});
