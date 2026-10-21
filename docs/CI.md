# CI and the merge gate

Every pull request into `dev` (and `dev` into `main`) runs `.github/workflows/ci.yml`. A PR can be merged only when the **CI result** check is green. This file lists what the gate checks and the repository settings that enforce it.

## What runs

| Job | Fails when | Runs on |
|---|---|---|
| API (format, build and test) | a `packages.lock.json` is out of date (locked restore); `dotnet format` would change a file; any compiler or analyzer warning (warnings are errors); any unit, architecture, integration or (from M03) tenant-isolation test fails | push, PR |
| Web (lint, format, test and build) | `package-lock.json` disagrees with `package.json` (`npm ci`); ESLint, including the template accessibility rules; Prettier; any Vitest component test; the production build or its size budget | push, PR |
| Dependency audit | `npm audit` finds a moderate or higher advisory; NuGet reports a vulnerable package, transitive ones included | push, PR |
| Secret scan | gitleaks finds a secret anywhere in the history | push, PR |
| Repository lint | actionlint finds a workflow problem; a file breaks `.editorconfig` (line endings, final newline, indentation, trailing whitespace) | push, PR |
| PR conventions | the branch name is not `#00nn-three-to-seven-kebab-words`; a branch other than `dev` targets `main`; a commit subject starts with `fixup!`, `squash!`, `amend!`, `WIP` or "do not merge" | PR only |
| **CI result** | any job above failed or was cancelled | push, PR |

The dependency audit can turn red without a code change, when a new advisory is published. That is intended: the next merge waits until the dependency is patched or the finding is explained in `DECISIONS.md`.

The end-to-end job (Playwright against the production image) joins the gate in M11.

## Repository settings (owner, one-time)

GitHub enforces the gate only with branch protection; the workflow alone cannot block a merge. On GitHub: **Settings → Rules → Rulesets → New branch ruleset**:

1. **Name:** `protect dev and main`; **Enforcement:** Active.
2. **Target branches:** add `dev` and `main` (Include by pattern).
3. Enable:
   - **Restrict deletions**;
   - **Block force pushes**;
   - **Require a pull request before merging** (required approvals: 0, since the owner is the only reviewer; enable "Require conversation resolution before merging");
   - **Require status checks to pass**, with "Require branches to be up to date before merging" turned on. Add the check **`CI result`** (it appears in the list after the workflow has run once on a PR).
4. Leave **Bypass list** empty, so the rules also apply to the owner.

With "up to date" on, a PR that is behind `dev` must be updated (the **Update branch** button) and pass CI again before merging, so `dev` is always the exact code that was tested.

## Running the same checks locally
The commands are in `CLAUDE.md` → Commands. The fastest pre-push check is:
```
dotnet format TenantHub.slnx --verify-no-changes && dotnet build TenantHub.slnx && dotnet test --solution TenantHub.slnx
cd src/web && npm run lint && npm run format:check && npm run test:ci && npm run build
```
