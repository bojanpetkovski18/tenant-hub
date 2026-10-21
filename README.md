# TenantHub

> A multi-tenant back-office for service businesses, built on a SaaS foundation you can trust: isolated tenants, real permissions, a full audit trail.

**Status: early draft.** This README is a placeholder; the full version (demo, screenshots, how to run it, architecture, decisions) comes in the final milestone.

TenantHub lets a small service business (cleaning, landscaping, maintenance, a small agency) sign up as an organization, invite its team, manage clients and plan jobs. Underneath is the foundation most B2B SaaS products need:
- organization sign-up, email invitations, and users who belong to several organizations;
- role permissions enforced on the server, including per-resource rules;
- tenant isolation enforced in several layers and **proven by automated tests** that cover new code automatically;
- plan limits that hold under concurrent requests;
- an append-only audit log written in the same transaction as each change.

- **Stack (planned):** ASP.NET Core (.NET 10), EF Core, PostgreSQL, Angular 22 with NgRx SignalStore, Tailwind CSS, Docker, GitHub Actions.
- **Data:** entirely fictional. Organizations, people, clients and jobs are invented.
- **AI assistance:** this is an independent portfolio project built with AI assistance (Claude Code) under my direction and review. I wrote the specification and made the decisions, and I review and merge every change.

See [`docs/SPEC.md`](docs/SPEC.md) for the specification and [`PROGRESS.md`](PROGRESS.md) for the current status.
