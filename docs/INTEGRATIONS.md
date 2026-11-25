# Integrations: API keys, webhooks and exports

For developers connecting another system to a TenantHub organization. API keys and webhooks are part of the Business plan; Owners and Admins manage them in **Settings → Integrations**. The rules behind this page are in `SPEC.md` sections 3.7 and 5.14 and decision D044.

## API keys

A key looks like `thk_` followed by 43 characters. It is shown once, when it is created; TenantHub keeps only its SHA-256 hash and its first 12 characters (to tell keys apart in the list). A key can expire (at most a year after it is made) and is revoked at once.

Send it as a bearer token to the organization's routes:

```bash
curl -H "Authorization: Bearer $TENANTHUB_KEY" \
  https://tenanthub.example/api/orgs/$ORG_ID/jobs?status=new
```

- **Organization:** the key belongs to one organization. Its id goes in the path; another organization's id answers `404`, exactly as for a missing organization.
- **Access:** a read-only key reads clients, jobs and agreements. A read-write key also creates and edits clients and jobs, and moves jobs through their statuses. No key manages members, billing, settings, the audit log, the portal or integrations.
- **Endpoints:** `GET`/`POST /clients`, `GET`/`PUT /clients/{id}`, `GET`/`POST /jobs`, `GET`/`PUT /jobs/{id}`, `POST /jobs/{id}/transitions`, `GET /jobs/export`, `GET /agreements` and `GET /agreements/{id}`, all under `/api/orgs/{orgId}`. The OpenAPI document marks them with the `ApiKey` scheme. Every other endpoint answers `403` with `code: "api-key-not-allowed"`.
- **No cookies:** a request with a session cookie and a key is refused (`400`, `credentials-conflict`). Key requests need no anti-forgery header.
- **Errors:** a revoked or expired key gets `401` (`api-key-invalid`); a plan without integrations gets `403` (`plan-feature-unavailable`); more than 120 requests a minute per key gets `429` with `Retry-After`.
- **Audit:** what a key changes is recorded as "API key <name>". Records it creates name the key's creator in their `created by` fields.

## Webhooks

An organization has at most five endpoints. Each is an `https` address that subscribes to events from this catalog:

| Event | When |
|---|---|
| `job.created` | a job is created (by a person, a key, a converted request or an agreement) |
| `job.status_changed` | a job moves to another status |
| `job.assigned` | a job gets an assignee (also on creation with one) |
| `quote.approved`, `quote.declined` | a client decides on a quote in the portal |
| `request.created` | a client asks for work in the portal |
| `visit.confirmed`, `visit.problem_reported` | a client signs off a completed visit, or reports a problem |

"Send a test event" sends `webhook.ping`, which no endpoint subscribes to.

### The request

```
POST /your/path HTTP/1.1
Content-Type: application/json; charset=utf-8
TenantHub-Signature: t=1795000000,v1=5f3a…
TenantHub-Event-Id: 0193…

{"id":"0193…","type":"job.created","occurredAt":"2026-11-21T10:00:00+00:00","organizationId":"…","data":{…}}
```

`data` is the job (or the request) as it was right after the change: ids and plain values, no names of people. Fetch more with an API key if you need it.

### Checking the signature

1. Split the `TenantHub-Signature` header on commas. Take `t` (Unix seconds) and every `v1` value.
2. Compute HMAC-SHA256 of the string `<t>.<body>` with the endpoint's secret (the whole `whsec_…` string, as UTF-8), and write it as lowercase hex. `<body>` is the raw request body, byte for byte: check it before parsing the JSON.
3. Accept the request if any `v1` equals your result (compare in constant time). For 24 hours after a secret is rotated there are two `v1` values, one per secret, so a receiver that still has the old secret keeps working.
4. Refuse a `t` more than five minutes from your clock, so a captured request cannot be replayed later.
5. Ignore an `id` you have already processed: retries and "deliver again" repeat the same `id` and body.

Node.js:

```js
import crypto from 'node:crypto';

export function verifyTenantHub(header, rawBody, secret, toleranceSeconds = 300) {
  const parts = header.split(',');
  const t = parts.find((p) => p.startsWith('t='))?.slice(2);
  const given = parts.filter((p) => p.startsWith('v1=')).map((p) => Buffer.from(p.slice(3)));
  if (!t || Math.abs(Date.now() / 1000 - Number(t)) > toleranceSeconds) return false;
  const expected = Buffer.from(crypto.createHmac('sha256', secret).update(`${t}.${rawBody}`).digest('hex'));
  return given.some((g) => g.length === expected.length && crypto.timingSafeEqual(g, expected));
}
```

C#:

```csharp
using System.Security.Cryptography;
using System.Text;

public static bool VerifyTenantHub(string header, string rawBody, string secret, TimeSpan tolerance)
{
    var parts = header.Split(',');
    var t = parts.FirstOrDefault(p => p.StartsWith("t=", StringComparison.Ordinal))?[2..];
    if (t is null || !long.TryParse(t, out var seconds)
        || (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > tolerance)
    {
        return false;
    }

    var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{rawBody}"))));
    return parts.Where(p => p.StartsWith("v1=", StringComparison.Ordinal))
        .Any(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p[3..]), expected));
}
```

### Delivery

- Any `2xx` answer within 10 seconds is a success. Redirects are not followed: a `3xx` is a failure.
- A failed delivery is tried again after 1 and 5 minutes, half an hour, and 2, 4, 8 and 10 hours: eight attempts over about a day.
- After three days in which deliveries to an endpoint have failed without a success, the endpoint is turned off and the organization's Owners are notified. Turning it back on starts the count afresh.
- The delivery log keeps each attempt's status code, duration and class of error for 30 days, never the response body. Owners and Admins can deliver any logged event again.
- TenantHub posts only to public addresses. The host is resolved when the connection is made, and the request is refused (and logged as a security event) if any of its addresses is loopback, private, link-local, unique-local, multicast or otherwise not public.

### Local development

`node tools/webhook-receiver.mjs --secret whsec_…` listens on `http://127.0.0.1:5099/`, checks each event's signature as described above and prints one line per event. Run the API in Development with `Webhooks__AllowedReceiver=http://127.0.0.1:5099/` so that this one local address is allowed; the setting is ignored in every other environment except `E2E`, which the end-to-end journey uses (D052). `GET /events` on the receiver lists what it received, raw bodies and signature headers included.

## Exports

- **Audit log:** `GET /api/orgs/{orgId}/audit/export?from=YYYY-MM-DD&to=YYYY-MM-DD` (Owners and Admins). Both days are required, in the organization's time zone, at most 366 days apart.
- **Jobs:** `GET /api/orgs/{orgId}/jobs/export` with the jobs list's filters and sort (`q`, `status`, `priority`, `assignee`, `clientId`, `overdue`, `dueFrom`, `dueTo`, `completedFrom`, `sort`); every page. API keys may use it.

Both are CSV for spreadsheets: UTF-8 with a byte-order mark, comma-separated, CRLF line ends, times in the organization's time zone. A cell that starts with `=`, `+`, `-`, `@`, a tab or a carriage return gets a leading apostrophe, so a spreadsheet shows it as text instead of running it as a formula. The file is written as rows are read from the database, so a long export does not use more memory than a short one.
