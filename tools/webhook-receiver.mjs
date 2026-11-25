// A tiny local webhook receiver for development and the M22 journey (SPEC-V2 5.8, D044). It listens on 127.0.0.1,
// verifies each event's TenantHub-Signature with the documented algorithm, and prints one line per event. The API
// delivers to it only in Development, with Webhooks__AllowedReceiver set to its address (no other private address is
// ever allowed). Plain Node, no dependencies.
//
//   node tools/webhook-receiver.mjs [--port 5099] [--secret whsec_…] [--status 200] [--log events.jsonl]
//
// Without --secret the signature is shown but not checked. --status makes it answer with another status (to watch
// retries); --log appends each event, with its check, to a JSON-lines file (the journey reads it).
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, arg, i, all) => (arg.startsWith('--') ? [...pairs, [arg.slice(2), all[i + 1]]] : pairs), []),
);
const port = Number(args.port ?? 5099);
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
const server = http.createServer((request, response) => {
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

server.listen(port, '127.0.0.1', () => {
  console.log(`Webhook receiver on http://127.0.0.1:${port}/ (set Webhooks__AllowedReceiver=http://127.0.0.1:${port}/ for the API)`);
});
