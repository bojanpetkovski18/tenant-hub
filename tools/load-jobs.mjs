// Jobs list API under load (SPEC 9: p95 < 200 ms locally with 10,000 jobs across 20 organizations).
//
// Seeds through the API like real users (so the data passes every rule and limit): 4 owners with 5 organizations
// each (the five-owned limit), moved to Business, 25 clients and 500 jobs per organization in a status mix, then
// times the list queries the SPA makes, from several concurrent clients. No dependencies beyond Node 24.
//
//   node tools/load-jobs.mjs [--base http://localhost:5090] [--orgs 20] [--jobs 500] [--requests 2000] [--concurrency 10]
//
// Run the API with request logging quiet, or the console becomes the bottleneck, for example
//   Logging__LogLevel__Default=Warning dotnet run --project src/api/TenantHub.Api
// Every run creates new organizations (names carry a run id); use a scratch database (CLAUDE.md).

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, value, i, all) => (value.startsWith('--') ? [...pairs, [value.slice(2), all[i + 1]]] : pairs), []),
);
const base = args.base ?? 'http://localhost:5090';
const orgCount = Number(args.orgs ?? 20);
const jobsPerOrg = Number(args.jobs ?? 500);
const requests = Number(args.requests ?? 2000);
const concurrency = Number(args.concurrency ?? 10);
const clientsPerOrg = 25;
const password = 'Load-Test-Pass-42!';
const run = Date.now().toString(36);

/** A signed-in browser-like client: cookie jar, XSRF header on unsafe requests. */
class Session {
  cookies = new Map();

  async request(method, path, body) {
    const headers = { cookie: [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ') };
    if (body !== undefined) headers['content-type'] = 'application/json';
    if (method !== 'GET' && this.cookies.has('XSRF-TOKEN')) headers['x-xsrf-token'] = decodeURIComponent(this.cookies.get('XSRF-TOKEN'));
    const response = await fetch(base + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    for (const cookie of response.headers.getSetCookie()) {
      const [pair] = cookie.split(';');
      const at = pair.indexOf('=');
      this.cookies.set(pair.slice(0, at), pair.slice(at + 1));
    }
    const text = await response.text();
    if (!response.ok) throw new Error(`${method} ${path} → ${response.status} ${text.slice(0, 200)}`);
    return text ? JSON.parse(text) : null;
  }
}

/** Runs tasks with at most `limit` in flight. */
async function pool(items, limit, task) {
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const item = items[next++];
      await task(item);
    }
  }));
}

const day = (offset) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);
const words = ['Window', 'Deep', 'Office', 'Carpet', 'Kitchen', 'Lawn', 'Hedge', 'Gutter', 'Floor', 'Sanitize'];

// --- Seed ---------------------------------------------------------------------------------------------------
const seedStart = performance.now();
const orgs = [];
for (let owner = 0; orgs.length < orgCount; owner++) {
  const session = new Session();
  await session.request('GET', '/api/auth/session');
  const signedUp = await session.request('POST', '/api/auth/sign-up', {
    displayName: `Load Owner ${owner + 1}`,
    email: `load-${run}-${owner + 1}@load.example`,
    password,
    organizationName: `Load ${run} ${orgs.length + 1}`,
    timeZone: 'Europe/Amsterdam',
  });
  await session.request('GET', '/api/auth/session'); // the XSRF token for the signed-in user
  const userId = signedUp.user.id;
  orgs.push({ session, userId, id: signedUp.user.memberships[0].organizationId });
  for (let more = 1; more < 5 && orgs.length < orgCount; more++) {
    const membership = await session.request('POST', '/api/orgs', { name: `Load ${run} ${orgs.length + 1}`, timeZone: 'Europe/Amsterdam' });
    orgs.push({ session, userId, id: membership.organizationId });
  }
}

await pool(orgs, orgs.length, async (org) => {
  const api = `/api/orgs/${org.id}`;
  await org.session.request('PUT', `${api}/plan`, { plan: 'business' });
  org.clients = [];
  for (let c = 0; c < clientsPerOrg; c++) {
    org.clients.push((await org.session.request('POST', `${api}/clients`, { name: `Client ${c + 1}` })).id);
  }
});

// Status mix per ten jobs: 3 New (one unassigned), 3 Scheduled, 2 In progress, 1 Completed, 1 Cancelled.
const plan = (i) => ['new', 'new', 'new', 'scheduled', 'scheduled', 'scheduled', 'inProgress', 'inProgress', 'completed', 'cancelled'][i % 10];
const steps = { new: [], scheduled: ['scheduled'], inProgress: ['inProgress'], completed: ['inProgress', 'completed'], cancelled: ['cancelled'] };
const jobs = orgs.flatMap((org) => Array.from({ length: jobsPerOrg }, (_, i) => ({ org, i })));
let created = 0;
await pool(jobs, concurrency * 2, async ({ org, i }) => {
  const target = plan(i);
  const api = `/api/orgs/${org.id}`;
  // Due on or after the scheduled day (a domain rule); some due dates are past, so "overdue" has rows.
  const scheduledOffset = target === 'new' && i % 2 ? null : (i % 40) - 10;
  const job = await org.session.request('POST', `${api}/jobs`, {
    title: `${words[i % words.length]} clean ${i + 1}`,
    clientId: org.clients[i % org.clients.length],
    assigneeUserId: i % 10 === 0 ? null : org.userId,
    priority: ['low', 'normal', 'normal', 'high', 'urgent'][i % 5],
    scheduledOn: scheduledOffset === null ? null : day(scheduledOffset),
    dueOn: day(scheduledOffset === null ? (i % 90) - 30 : scheduledOffset + (i % 15)),
    quotedAmount: 50 + (i % 20) * 12.5,
  });
  for (const to of steps[target]) {
    await org.session.request('POST', `${api}/jobs/${job.id}/transitions`, to === 'cancelled' ? { to, reason: 'Client cancelled' } : { to });
  }
  if (++created % 1000 === 0) console.log(`  ${created} jobs`);
});
const seedSeconds = ((performance.now() - seedStart) / 1000).toFixed(0);
console.log(`Seeded ${orgs.length} organizations, ${orgs.length * clientsPerOrg} clients and ${created} jobs in ${seedSeconds} s.`);

// --- Measure ------------------------------------------------------------------------------------------------
const scenarios = {
  'default page': '',
  'status new+scheduled': '&status=new&status=scheduled',
  'overdue': '&overdue=true',
  'search "window"': '&q=window',
  'sort by due': '&sort=due',
  'page 10': '&page=10',
  'my jobs, urgent': '&assignee=me&priority=urgent',
};
const names = Object.keys(scenarios);
const timings = Object.fromEntries(names.map((n) => [n, []]));
const call = async (k) => {
  const org = orgs[k % orgs.length];
  const name = names[k % names.length];
  const started = performance.now();
  const page = await org.session.request('GET', `/api/orgs/${org.id}/jobs?pageSize=25${scenarios[name]}`);
  return { name, ms: performance.now() - started, total: page.total };
};

await pool(Array.from({ length: 100 }, (_, k) => k), concurrency, call); // warm-up: JIT, connection pool, caches
await pool(Array.from({ length: requests }, (_, k) => k), concurrency, async (k) => {
  const { name, ms } = await call(k);
  timings[name].push(ms);
});

const percentile = (values, p) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)];
};
const row = (name, values) =>
  `| ${name} | ${values.length} | ${percentile(values, 50).toFixed(1)} | ${percentile(values, 95).toFixed(1)} | ${percentile(values, 99).toFixed(1)} | ${Math.max(...values).toFixed(1)} |`;
console.log(`\n${requests} requests, ${concurrency} concurrent, ${orgs.length} organizations × ${jobsPerOrg} jobs (run ${run})\n`);
console.log('| Query | n | p50 ms | p95 ms | p99 ms | max ms |\n|---|---|---|---|---|---|');
for (const name of names) console.log(row(name, timings[name]));
const all = Object.values(timings).flat();
console.log(row('**all**', all));
const p95 = percentile(all, 95);
console.log(`\np95 ${p95.toFixed(1)} ms: ${p95 < 200 ? 'meets' : 'MISSES'} the 200 ms target.`);
process.exit(p95 < 200 ? 0 : 1);
