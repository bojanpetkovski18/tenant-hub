# M21: API keys, signed webhooks and exports

Branch: `#0021-api-keys-webhooks-and-exports`

## Goal
Business customers connect other tools: an API key reaches clients and jobs within its access level, webhooks arrive signed and retried, and nothing a customer types can make the server call its own network. Data can be exported safely.

## Context
SPEC-V2 2.1, 2.4, 3.3, 4.7, 5.8, 7 (API keys, webhooks, exports), 8 (`api_keys`, `webhook_endpoints`, `webhook_deliveries`), criteria 18 and 19; D013, D025, D039, D042, D044. Stories US-110, US-111, US-112.

Security-sensitive: a second authentication path into tenant routes, and outbound requests to customer URLs. Ask the owner to switch to the strongest model if not already.

The owner approves the API-key authentication bypass (finding a key by hash before the tenant is known) by approving SPEC-V2 section 8.

## Scope
1. **API keys.**
   - Generation (random, with a visible prefix, shown once) and SHA-256 storage.
   - A bearer authentication handler that resolves the key, refuses expired or revoked keys, and builds the tenant context from the key's organization and access level. A key for another organization gets `404`.
   - Key-enabled endpoints opt in with an endpoint convention; every other endpoint refuses keys with `403 api-key-not-allowed`. Cookie plus bearer together is `400`.
   - Antiforgery skipped for bearer requests only.
   - Rate limit per key; last-used throttled to once a minute.
   - Audit actor "API key <name>".
2. **Webhooks.**
   - Endpoints with the rules in 5.8; an encrypted secret (Data Protection) and rotation with an overlap.
   - The event catalog mapped from domain events through the outbox; one delivery per endpoint and event, with a stable event id.
3. **Delivery.**
   - An `HttpClient` with a `SocketsHttpHandler` whose `ConnectCallback` resolves the host, refuses any non-public address, and connects to the checked address;
   - no redirects and a 10-second timeout;
   - the signature header as documented;
   - backoff over about a day (8 attempts), disabling after 3 days of failures with a notification to Owners (M20);
   - a delivery log without response bodies, 30 days of retention on the scheduler, and redeliver.
4. **Exports.**
   - Audit CSV for a date range (at most 366 days) and jobs CSV for the current filters;
   - streamed from the database with `AsAsyncEnumerable`, with formula-injection neutralization (5.8) and a UTF-8 BOM decided here for spreadsheet compatibility;
   - API-key access on the jobs export.
5. **OpenAPI:** the bearer scheme and which operations accept it; a short "Integrations" page in `docs/` with the signature algorithm and a verification example in two languages.
6. **Angular:**
   - Settings → Integrations (Business only; other plans see the feature and the plan that has it): API keys with a "shown once" dialog, webhooks with the event picker, secret, test delivery, delivery log and redeliver;
   - Export buttons on the audit log and the jobs list.
7. **Development:** a tiny webhook receiver for local testing and the M22 journey (a Compose profile or a test-only service), allowed by the development-only setting.

## Out of scope
OAuth, scopes beyond the two access levels, inbound webhooks, per-endpoint custom headers.

## Tests required
- **Key matrix:** each key-enabled endpoint with a key of the other organization (`404`), a read-only key on writes (`403`), a revoked or expired key (`401`), a key on a non-enabled endpoint (`403`), cookie plus key (`400`).
- **SSRF (criterion 18):** `localhost`, `127.0.0.1`, `[::1]`, `10/8`, `172.16/12`, `192.168/16`, `169.254.169.254`, `fc00::/7`, IPv4-mapped IPv6, a hostname resolving to a private address (a fake resolver), and a redirect to a private address: all refused, with the attempt logged as a security event.
- **Signature:** verified by an independent implementation in the test (not the production signer); rotation overlap.
- **Delivery:** retry schedule with a controllable clock; idempotent event id across retries; disabling after the failure window; at most one delivery in flight per message across two dispatchers.
- **Exports:** a cell starting with each dangerous character is neutralized; memory stays flat on 50,000 audit entries (measure); isolation of both exports.
- **Entitlements:** every key and webhook endpoint refused below Business.

## Acceptance criteria
- SPEC-V2 criteria 18 and 19 (API and webhooks) met.
- From a terminal: create a key in the UI, list and create jobs with `curl`, see the audit entry name the key; receive a signed webhook in the local receiver and verify it with the documented algorithm; export both CSVs and open them in a spreadsheet application.

## Verification required
Full test suite; SSRF and signature tests; browser pass at three widths, keyboard, both themes, with every dialog opened; `dotnet list package --vulnerable` and `npm audit` clean; the API log checked for keys and secrets.
