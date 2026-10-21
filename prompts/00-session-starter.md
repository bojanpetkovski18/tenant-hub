# Session starter

Paste this at the start of a new session, followed by the milestone brief (`prompts/Mnn-*.md`).

---

You are the coding agent on TenantHub. Before doing anything:

1. Read `CLAUDE.md` in full; it overrides your defaults. Then read `PROGRESS.md` (status, known issues, next step), `DECISIONS.md` and `docs/CHECKLISTS.md`.
2. Read the milestone brief I give you, the sections of `docs/SPEC.md` it references and its user stories in `docs/USER-STORIES.md`.
3. Inspect the repository yourself: `git status`, `git log --oneline -15`, `git branch -a` and the file tree of the areas you will touch. Assume nothing you have not seen.
4. Make sure you are on a fresh branch from an up-to-date `dev`:
   ```
   git switch dev && git pull
   git switch -c "#00nn-descriptive-name"
   ```
   If the working tree is not clean, stop and tell me.
5. Run the verified commands from the CLAUDE.md "Commands" section (build and tests) to confirm a green baseline. Report any failure before starting.
6. Briefly restate:
   - the milestone goal;
   - your plan as a short numbered list of commits;
   - any assumptions.

   Then start. If a requirement is ambiguous in a way that changes the design, ask one clear question first.

During the milestone:
- Commit in small, building, tested steps.
- Verify for real: run the build and tests and read the output. For UI work, use a real browser at 375, 768 and 1280 px; check the console and keyboard navigation.
- Keep the isolation suite green; never widen an allow-list without asking me.
- Remind me of deferred known issues when they become relevant.

At the end:
- Check the definition of done in `docs/CHECKLISTS.md`.
- Update `PROGRESS.md`, `DECISIONS.md` and the CLAUDE.md Commands section.
- Push the branch.
- Give the milestone report in the exact format from CLAUDE.md, then the PR description and compare link in the chat.
