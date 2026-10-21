# M12: Demo hosting, README, architecture and portfolio kit

Branch: `#0012-demo-hosting-readme-and-portfolio`

## Goal
A client can try TenantHub, understand what was built and why, and inspect the evidence, from the README alone. The repository is ready to go public.

## Context
SPEC 10 (criterion 11); D016 (hosting, pending); `docs/CHECKLISTS.md` → Deployment checklist.

## Scope
1. **Hosting (D016):** research current free and low-cost options for one container plus PostgreSQL (limits, sleep behaviour, cold starts, data retention, price), compare them with a Codespaces dev container, record the findings with dates, and ask the owner to choose. Deploy and verify the choice with the deployment checklist.
2. **README for clients:** what it is and who it is for; how to try it (demo link or one-command run, demo roles); screenshots and a short recording; how tenant isolation is enforced and proven (the strongest section); features; stack and architecture overview; how to run and test; decisions and trade-offs; AI-assistance statement; licence.
3. **`docs/ARCHITECTURE.md`** as built: projects and dependencies, request flow for a tenant-scoped command (membership → permission → handler → domain → guard → audit → commit), data model diagram, the isolation layers and their tests.
4. **Media:** screenshots of the strongest screens at desktop and mobile in both themes, and a short journey recording, captured from seed data with a clean console.
5. **Portfolio kit** in `docs/portfolio/`: Upwork title, description within the field limits, role and AI-assistance statement, skills and keywords, screenshot order, and how the project maps to services the owner offers. No invented clients, numbers or outcomes; numbers only from PROGRESS.
6. **Public-repo scan:** gitleaks over the full history and working tree; search for personal data and local paths; report the results.
7. Merge `dev` to `main` and tag `v1.0.0` when the owner approves.

## Out of scope
New features.

## Acceptance criteria
- A fresh visitor can try the demo (hosted or one command) with the documented access.
- Every number in the README and docs appears in PROGRESS → Measured results.
- The scan is clean or each finding is resolved with the owner.

## Verification required
Follow the README from a fresh clone (or the live demo) exactly as written; record the result.
