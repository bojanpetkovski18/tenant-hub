import crypto from 'node:crypto';
import zlib from 'node:zlib';
import { type Page, expect, test } from '@playwright/test';
import {
  ConsoleWatch,
  emailedLink,
  expectAccessible,
  newRun,
  password,
  signUp,
  xsrfHeader,
} from './support';

// SPEC-V2 10, criterion 20: a recurring visit from the agreement to a signed webhook, through the production image in
// the E2E environment (docker-compose.e2e.yml, D052) with Mailpit and the test receiver. Every page is checked with
// axe, and any console error or CSP violation fails the journey.

const receiver = process.env['E2E_RECEIVER_URL'] ?? 'http://localhost:5099';
const run = newRun();

/** A small PNG made here, so the upload is a real image the server decodes and re-encodes. */
function png(width: number, height: number): Buffer {
  const chunk = (type: string, data: Buffer): Buffer => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(zlib.crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8-bit RGB
  const rows = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      rows.set([(x * 4) % 256, (y * 4) % 256, 160], y * (width * 3 + 1) + 1 + x * 3);
    }
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', zlib.deflateSync(rows)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** The documented check (docs/INTEGRATIONS.md), written here rather than taken from the product. */
function signatureMatches(header: string, body: string, secret: string): boolean {
  const parts = header.split(',');
  const t = parts.find((p) => p.startsWith('t='))?.slice(2);
  if (!t) {
    return false;
  }
  const expected = crypto.createHmac('sha256', secret).update(`${t}.${body}`).digest('hex');
  return parts
    .filter((p) => p.startsWith('v1='))
    .some((p) => p.length === expected.length + 3 && crypto.timingSafeEqual(Buffer.from(p.slice(3)), Buffer.from(expected)));
}

interface Received {
  path: string;
  signature: string;
  body: string;
}

async function organizationId(page: Page): Promise<string> {
  const session = await (await page.request.get('/api/auth/session')).json();
  return session.user.memberships[0].organizationId as string;
}

test('a recurring visit runs from the agreement to a signed webhook', async ({ browser }) => {
  const watch = new ConsoleWatch();
  const ownerEmail = `visits-owner-${run}@e2e.example`;
  const mateEmail = `visits-mate-${run}@e2e.example`;
  const contactEmail = `visits-contact-${run}@e2e.example`;
  const hookPath = `/e2e/${run}`;

  const ownerContext = await browser.newContext();
  await watch.attach(ownerContext, 'owner');
  const owner = await ownerContext.newPage();

  // 1. An Owner signs up, confirms the address and moves to Business (the portal and webhooks).
  const slug = await signUp(owner, 'Vera Owner', ownerEmail, `E2E Visits ${run}`);
  await owner.goto(await emailedLink(owner.request, ownerEmail, 'Confirm your email address', 'verify-email'));
  await expect(owner.getByRole('heading', { name: 'Email address confirmed' })).toBeVisible();
  await owner.goto(`/o/${slug}/settings`);
  await expect(owner.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
  await expectAccessible(owner, 'settings');
  await owner.getByRole('button', { name: 'Switch to Business' }).click();
  await owner.getByRole('alertdialog').getByRole('button', { name: 'Switch to Business' }).click();
  await expect(owner.getByRole('alertdialog')).toBeHidden();
  await expect(owner.getByText('Current plan: Business')).toBeVisible();

  // 2. A teammate joins as a Member: the one who will do the visit.
  await owner.goto(`/o/${slug}/members`);
  await owner.getByRole('button', { name: 'Invite', exact: true }).first().click();
  const invite = owner.getByRole('dialog');
  await invite.getByLabel('Email').fill(mateEmail);
  await invite.getByLabel('Role').selectOption({ label: 'Member' });
  await invite.getByRole('button', { name: 'Send invitation' }).click();
  await expect(owner.getByRole('dialog')).toBeHidden();
  const mateContext = await browser.newContext();
  await watch.attach(mateContext, 'member');
  const mate = await mateContext.newPage();
  await mate.goto(await emailedLink(owner.request, mateEmail, 'invited you', 'invite'));
  await mate.getByLabel('Your name').fill('Milo Member');
  await mate.getByLabel('Password', { exact: true }).fill(password);
  await mate.getByRole('button', { name: 'Create account and join' }).click();
  await mate.waitForURL(new RegExp(`/o/${slug}/`));

  // 3. The receiver's endpoint. The form takes https addresses only; the test receiver is the one http address the
  //    E2E environment allows (D052), so the test registers it through the API as the Owner, and keeps the secret.
  const orgId = await organizationId(owner);
  const created = await owner.request.post(`/api/orgs/${orgId}/webhooks`, {
    headers: await xsrfHeader(ownerContext),
    data: { url: `http://receiver:5099${hookPath}`, events: ['quote.approved', 'job.status_changed', 'visit.confirmed'] },
  });
  expect(created.ok(), `endpoint created (${created.status()})`).toBe(true);
  const secret = (await created.json()).secret as string;
  await owner.goto(`/o/${slug}/settings/integrations`);
  await expect(owner.getByRole('list', { name: 'Webhook endpoints' })).toContainText(`receiver:5099`);
  await expectAccessible(owner, 'integrations');

  // 4. A client with a portal contact.
  await owner.goto(`/o/${slug}/clients/new`);
  await owner.locator('#name').fill('Lakeside Dental');
  await owner.getByRole('button', { name: 'Create client' }).click();
  await expect(owner.getByRole('status').first()).toContainText('Client created.');
  await owner.locator('#contact-name').fill('Dana Contact');
  await owner.locator('#contact-email').fill(contactEmail);
  await owner.getByRole('button', { name: 'Add contact' }).click();
  await expect(owner.getByRole('list', { name: 'Portal contacts' })).toContainText(contactEmail);
  await expectAccessible(owner, 'client');

  // 5. An agreement whose one visit falls today: every weekday ticked, ending after one visit.
  await owner.goto(`/o/${slug}/agreements/new`);
  await owner.getByLabel('Client').selectOption({ label: 'Lakeside Dental' });
  await owner.getByLabel('Title').fill('Surgery deep clean');
  await owner.getByLabel(/Quoted per visit/).fill('180');
  await owner.getByRole('button', { name: /Add (an )?item/ }).click();
  await owner.getByRole('textbox', { name: 'Item 1', exact: true }).fill('Disinfect the chairs');
  await owner.getByRole('checkbox', { name: 'Required' }).check();
  for (const day of ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']) {
    await owner.getByRole('checkbox', { name: day }).check();
  }
  await owner.getByRole('radio', { name: 'After' }).check();
  await owner.getByLabel('Number of visits').fill('1');
  await expectAccessible(owner, 'new agreement');
  await owner.getByRole('button', { name: 'Create agreement' }).click();
  await expect(owner.getByRole('heading', { level: 1, name: 'Surgery deep clean' })).toBeVisible();
  await expectAccessible(owner, 'agreement');

  // 6. On the board the visit waits in the Unassigned row; moved to the teammate by keyboard alone.
  await owner.goto(`/o/${slug}/board`);
  await expect(owner.getByRole('heading', { level: 1, name: 'Board' })).toBeVisible();
  await expectAccessible(owner, 'board');
  const move = owner.getByRole('button', { name: /^Move to… JOB-0001/ });
  await move.focus();
  await owner.keyboard.press('Enter');
  const moveDialog = owner.getByRole('dialog');
  await expect(moveDialog).toBeVisible();
  await expectAccessible(owner, 'move dialog');
  await moveDialog.getByLabel('Person').focus();
  await moveDialog.getByLabel('Person').selectOption({ label: 'Milo Member' });
  await moveDialog.getByLabel('Day').press('Enter');
  await expect(moveDialog).toBeHidden();
  await expect(owner.getByText(/^JOB-0001 moved to Milo Member, /)).toBeAttached();

  // 7. The Owner sends the visit's quote for approval.
  await owner.goto(`/o/${slug}/jobs`);
  await owner.getByRole('link', { name: /Surgery deep clean/ }).first().click();
  await expect(owner.getByRole('heading', { level: 1, name: /Surgery deep clean/ })).toBeVisible();
  const jobPath = new URL(owner.url()).pathname;
  await owner.getByRole('button', { name: 'Send for approval' }).click();
  await owner.getByRole('alertdialog').getByRole('button', { name: 'Send for approval' }).click();
  await expect(owner.getByText('Waiting for the client')).toBeVisible();
  await expectAccessible(owner, 'job with a pending quote');

  // 8. The contact signs in from the emailed link and approves the quote.
  const contactContext = await browser.newContext();
  await watch.attach(contactContext, 'contact');
  const contact = await contactContext.newPage();
  await contact.goto(`/portal/${slug}`);
  await expectAccessible(contact, 'portal sign-in');
  await contact.getByLabel('Email').fill(contactEmail);
  await contact.getByRole('button', { name: 'Email me a sign-in link' }).click();
  await expect(contact.getByRole('heading', { name: 'Check your email' })).toBeVisible();
  await contact.goto(await emailedLink(owner.request, contactEmail, 'sign', `portal/${slug}/sign-in`));
  await expect(contact.getByRole('heading', { level: 1, name: 'Visits' })).toBeVisible();
  await expectAccessible(contact, 'portal visits');
  await contact.getByRole('link', { name: 'Quotes' }).click();
  await expect(contact.getByRole('heading', { level: 1, name: 'Quotes' })).toBeVisible();
  await expectAccessible(contact, 'portal quotes');
  await contact.getByRole('button', { name: /^Approve JOB-0001/ }).click();
  const approve = contact.getByRole('dialog').or(contact.getByRole('alertdialog'));
  await approve.getByLabel(/Comment/).fill('Thank you, see you then.');
  await expectAccessible(contact, 'approve dialog');
  await approve.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(contact.getByRole('heading', { name: 'Decided' })).toBeVisible();

  // 9. The teammate starts the visit, ticks the checklist, adds a photo and completes it.
  await mate.goto(jobPath);
  await expectAccessible(mate, 'job, as the assignee');
  await mate.getByRole('button', { name: 'Start' }).click();
  await mate.getByRole('checkbox', { name: 'Disinfect the chairs' }).check();
  const chooser = mate.waitForEvent('filechooser');
  await mate.getByRole('button', { name: 'Add photos' }).click();
  await (await chooser).setFiles({ name: 'chairs.png', mimeType: 'image/png', buffer: png(96, 64) });
  await expect(mate.getByRole('button', { name: 'Open photo 1 of 1' })).toBeVisible();
  await mate.getByRole('button', { name: 'Complete' }).click();
  await expect(mate.getByText('Completed', { exact: true }).first()).toBeVisible();
  await expectAccessible(mate, 'completed job');

  // 10. The contact sees the proof and confirms the visit.
  await contact.getByRole('link', { name: 'Visits' }).click();
  await contact.getByRole('link', { name: /Past/ }).click();
  await contact.getByRole('link', { name: /Surgery deep clean/ }).click();
  await expect(contact.getByRole('heading', { name: 'What was done' })).toBeVisible();
  await expect(contact.getByText('Disinfect the chairs')).toBeVisible();
  await expect(contact.getByRole('heading', { name: 'Photos' })).toBeVisible();
  await expectAccessible(contact, 'portal visit');
  await contact.getByRole('button', { name: 'Looks good' }).click();
  await contact.getByRole('alertdialog').getByRole('button', { name: 'Looks good' }).click();
  await expect(contact.getByText(/confirmed this visit/)).toBeVisible();

  // 11. The receiver got the events, each signed as documented with the endpoint's secret.
  let events: Received[] = [];
  await expect
    .poll(
      async () => {
        const all = (await (await owner.request.get(`${receiver}/events`)).json()) as Received[];
        events = all.filter((e) => e.path === hookPath);
        return events.map((e) => JSON.parse(e.body).type as string);
      },
      { timeout: 60_000 },
    )
    .toEqual(expect.arrayContaining(['quote.approved', 'job.status_changed', 'visit.confirmed']));
  for (const event of events) {
    expect(signatureMatches(event.signature, event.body, secret), `signature of ${event.body}`).toBe(true);
    expect(JSON.parse(event.body).organizationId).toBe(orgId);
  }

  expect(watch.problems).toEqual([]);
});
