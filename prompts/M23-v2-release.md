# M23: v2.0.0: documentation, portfolio and release

Branch: `#0023-v2-release-docs-and-portfolio`

## Goal
v2 is documented as built, shown well, and released as `v2.0.0` on `main`.

## Context
SPEC-V2 (all), D035 (no public demo at v1), D036, D037–D045; `docs/ARCHITECTURE.md`, `README.md`, `docs/portfolio/`; the playbook's demo, documentation and release sections.

## Scope
1. **One specification:** merge `docs/SPEC-V2.md` into `docs/SPEC.md` as the product as built (sections renumbered once, criteria 1–21), and delete SPEC-V2 with a note in `DECISIONS.md`. Update the story file's headings to match.
2. **Architecture:** update `docs/ARCHITECTURE.md` from the code: the six isolation layers with row-level security, the portal identity and its boundary, the outbox and scheduler, the upload pipeline, webhooks and the SSRF guard, the new data model diagram. Check each claim against the code.
3. **Hosting, revisited (owner's decision):** the portal is best shown live. Re-research at least three options from official sources with dates (D035's list is a starting point; prices change), recommend one, and create nothing until the owner approves. If the owner says no again, the README keeps the one-command run.
4. **README:**
   - "What's new in v2" near the top;
   - the portal and the board in the guided tour, with screenshots in both themes and at phone width;
   - the new quality numbers, taken from `PROGRESS.md` only;
   - the demo client account;
   - the AI-assistance note kept.
5. **Media:** new screenshots and a walkthrough recording from a fresh stack with seed data only (playbook: captions checked against the code, no real brand names).
6. **Portfolio kit:** `docs/portfolio/` updated with the v2 story (the client boundary, row-level security, the outbox, webhooks), with measured facts only and character counts measured by a script.
7. **Release:**
   - re-run the public-repo scan (secrets with the pinned gitleaks, personal data) on the tree and the full history, and report;
   - after the owner merges, a `dev` → `main` PR (owner), the tag `v2.0.0` and release notes (what it does, how to try it, what is inside, measured quality, known limitations, the AI note);
   - CI green on `main` checked after the merge.

## Out of scope
New features or fixes beyond what documentation reveals (those go to the owner first).

## Tests required
None new; the full suite and both journeys run before the release.

## Acceptance criteria
- The README followed from a fresh clone works exactly as written, with cold caches (playbook fresh-clone test).
- Every number in the README and the portfolio kit has a row in `PROGRESS.md` → Measured results.
- The scan is clean or its findings are reported to the owner.

## Verification required
Fresh-clone test; documentation claims checked against the code; scan results; CI on `main` after the release merge.
