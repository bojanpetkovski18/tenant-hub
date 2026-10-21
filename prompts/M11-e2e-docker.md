# M11: End-to-end test, production Docker image and Compose

Branch: `#0011-e2e-production-docker-and-compose`

## Goal
One command runs the whole product from a fresh clone, and CI proves the main journey works against the production image.

## Context
SPEC 9 (reliability, security) and 10 (criterion 10); `docs/CHECKLISTS.md` → Deployment checklist.

## Scope
1. A multi-stage `Dockerfile`: build the SPA and the API; a small, non-root runtime image (chiseled or equivalent) in which the API serves the SPA with the production CSP; health check.
2. `docker-compose.yml`: PostgreSQL, Mailpit and the app; migrations and seed on start; settings from `.env`; data in a named volume.
3. Playwright journey in `tests/e2e`: sign up → invite a teammate → read the email in Mailpit → accept in a second browser context → create a client → create, assign and complete a job → see the entries in the audit log; plus a short cross-tenant check through the UI (a second organization's slug shows not found). axe check on each page visited.
4. CI job that builds the image, starts Compose and runs the journey; traces uploaded on failure.
5. Forwarded-headers support, configurable for the host chosen in M12.

## Out of scope
Hosting (M12).

## Tests required
The journey, stable over three consecutive local runs and in CI.

## Acceptance criteria
- From a fresh clone: `cp .env.example .env` and `docker compose up` gives a healthy stack with demo data (time recorded).
- The image runs as non-root; its size is recorded.
- The journey passes three times in a row locally and in CI.

## Verification required
Fresh-clone test in a temporary directory; image inspection; CI run link.
