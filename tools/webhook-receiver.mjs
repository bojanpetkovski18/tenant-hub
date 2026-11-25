// A tiny local webhook receiver for development and the second journey (SPEC-V2 5.8, D044, D052). It listens on
// 127.0.0.1, verifies each event's TenantHub-Signature with the documented algorithm, and prints one line per event. The
// API delivers to it only in Development and the E2E environment, with Webhooks__AllowedReceiver set to its address (no
// other private address is ever allowed). Plain Node, no dependencies.
//
//   node tools/webhook-receiver.mjs [--port 5099] [--host 127.0.0.1] [--secret whsec_…] [--status 200] [--log events.jsonl]
//
// Without --secret the signature is shown but not checked. --status makes it answer with another status (to watch
// retries); --log appends each event, with its check, to a JSON-lines file. GET /events answers with every event
// received so far, raw body and signature header included, so a test can check the signature itself (the journey
// does). --host 0.0.0.0 makes it reachable from other containers (docker-compose.e2e.yml).
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, arg, i, all) => (arg.startsWith('--') ? [...pairs, [arg.slice(2), all[i + 1]]] : pairs), []),
);
const port = Number(args.port ?? 5099);
const host = args.host ?? '127.0.0.1';
const secret = args.secret ?? process.env.WEBHOOK_SECRET ?? null;
const status = Number(args.status ?? 200);
const log = args.log ?? null;
const toleranceSeconds = 5 * 60;

/** The documented check: any v1 equal to hex HMAC-SHA256(secret, "<t>.<body>"), and t within five minutes. */
function verify(header, body, key, now = Date.now()) {
  const parts = Object.groupBy(header.split(','), (p) => p.slice(0, p.indexOf('=')));
  const t = parts.t?.[0]?.slice(2);
  if (!t || !parts.v1) {
    return { ok: false, reason: 'malformed header' };
  }
  if (Math.abs(now / 1000 - Number(t)) > toleranceSeconds) {
    return { ok: false, reason: 'timestamp too old' };
  }
  const expected = Buffer.from(crypto.createHmac('sha256', key).update(`${t}.${body}`).digest('hex'));
  const match = parts.v1.some((p) => {
    const given = Buffer.from(p.slice(3));
    return given.length === expected.length && crypto.timingSafeEqual(given, expected);
  });
  return match ? { ok: true } : { ok: false, reason: 'no signature matches' };
}

const seen = new Set();
const received = [];
const server = http.createServer((request, response) => {
  if (request.method === 'GET' && request.url === '/events') {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify(received));
    return;
  }
  const chunks = [];
  request.on('data', (chunk) => chunks.push(chunk));
  request.on('end', () => {
    const body = Buffer.concat(chunks).toString('utf8');
    const signature = String(request.headers['tenanthub-signature'] ?? '');
    let event = {};
    try {
      event = JSON.parse(body);
    } catch {
      // printed as unreadable below
    }
    const check = secret ? verify(signature, body, secret) : { ok: null, reason: 'no --secret given' };
    const duplicate = seen.has(event.id);
    seen.add(event.id);
    received.push({ receivedAt: new Date().toISOString(), path: request.url, signature, body, verified: check.ok, duplicate });
    const verdict = check.ok === true ? 'signature ok' : check.ok === false ? `SIGNATURE REJECTED (${check.reason})` : 'signature not checked';
    console.log(`${new Date().toISOString()} ${event.type ?? 'unreadable'} ${event.id ?? ''}${duplicate ? ' (seen before)' : ''}: ${verdict}`);
    if (log) {
      fs.appendFileSync(log, JSON.stringify({ receivedAt: new Date().toISOString(), verified: check.ok, duplicate, event }) + '\n');
    }
    // A receiver that rejects a bad signature answers 401, so the delivery log shows it.
    response.writeHead(check.ok === false ? 401 : status, { 'content-type': 'text/plain' });
    response.end('ok');
  });
});

server.listen(port, host, () => {
  console.log(`Webhook receiver on http://${host}:${port}/ (set Webhooks__AllowedReceiver=http://${host}:${port}/ for the API)`);
});
