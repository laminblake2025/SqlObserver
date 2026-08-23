# Contributing to SqlObserver

SqlObserver is being developed as an original, clean-room product. Contributions must preserve both the security boundary and the evidence trail behind architectural decisions.

## Before contributing

Read [the clean-room boundary](docs/product/clean-room-boundary.md), [the security policy](SECURITY.md), and the relevant records in [docs/adr](docs/adr). Check [BACKLOG.md](BACKLOG.md) for milestone ownership and dependencies.

For the current first assignment, contribute only Milestone 0 documentation and non-runtime Milestone 1 scaffolding. Do not add a production collector, target query, repository schema, installer behavior, runtime MCP endpoint, or sample that connects to a real database. Empty placeholders and compile/build contracts must not imply operational support.

## Clean-room contribution rules

- Derive requirements from public facts, standards, official platform documentation, and independently expressed product goals.
- Write original source, schemas, API shapes, user interface structures, vocabulary, prose, icons, and assets.
- Do not submit code or artifacts copied, translated, traced, decompiled, extracted, or closely imitated from a proprietary monitoring product.
- Do not use private screenshots, traffic captures, database dumps, binaries, leaked documentation, trial-installation internals, or another product's schema/API as design input.
- Do not submit generated content unless you can identify acceptable inputs and review the output for prohibited resemblance and license obligations.
- Record the provenance and license of every third-party dependency or asset. Do not paste code whose license or origin is uncertain.
- Raise uncertainty before review. The safe outcome is to redesign independently, not to disguise provenance.

Contributors must describe the public or first-party sources that informed a feature when the origin is not obvious. Reviewers may reject a technically sound change if originality cannot be established.

## Engineering standards

- Target .NET 10 with nullable reference types enabled, analyzers enabled, and warnings treated as errors.
- Use central NuGet package management and reproducible dependency locks/pinning adopted by the repository.
- Keep TypeScript strict and do not bypass it with unchecked `any` or blanket suppressions.
- Accept and propagate cancellation tokens for all I/O; never use sync-over-async.
- Use structured, bounded logging and OpenTelemetry without secrets or unrestricted SQL text.
- Use parameterized SQL only and documented database interfaces.
- Make data loss visible: batching, sampling, dropping, retries, and circuit-breaker state require metrics and audit-appropriate evidence.
- Persist and compare timestamps in UTC; convert only for user presentation.
- Enforce limits at ingress and again near resource use.
- Keep process hosts thin. Business rules belong in application/domain modules; infrastructure implements ports defined inward.

## Architecture and ADRs

An ADR is required before changing a durable boundary such as process topology, database technology, authentication, collector contract, MCP capability model, partition strategy, security classification, passive/enhanced behavior, or worker coordination. Create a new numbered ADR that links to and supersedes an old decision; do not rewrite accepted history to make a new design look original.

Do not modify completed milestone behavior merely to ease a later milestone unless the governing ADR is updated first. Proposed ADRs can be refined before acceptance, but their history should remain understandable in review.

## Database changes

Database evolution is SQL-first. Each production change must be an immutable, monotonically numbered migration with a recorded checksum. Do not enable automatic ORM schema generation or mutate an already released migration. A correction is a new forward migration. Migrations need upgrade, idempotency/guard behavior where designed, rollback-or-recovery documentation, permission tests, and compatibility coverage for the supported PostgreSQL version.

High-volume telemetry must follow the approved native partitioning and indexing design. Test retention and partition maintenance against representative volumes before treating them as operational.

## Tests and validation

Add tests before or with behavior. Choose suites that match the risk:

- unit tests for domain and application rules;
- PostgreSQL integration tests for migrations, queries, leases, partitions, ingestion, and retention;
- SQL Server integration tests for supported versions, capability/permission variants, fallbacks, cancellation, and timeouts;
- API and MCP contract tests for RBAC, allowlists, schemas, pagination, and payload limits;
- security tests for expected denial, injection, unsafe rendering, secret redaction, and audit coverage;
- performance tests for bounded collection, ingestion, queries, rollups, and retention;
- end-to-end tests for supported user journeys once runtime hosts exist.

Run the repository entry point from its root:

```powershell
pwsh ./tools/validate.ps1
```

Report the exact command and result in the change summary. A skipped suite must be explicit and justified; a placeholder test is not evidence that runtime behavior works.

## Change review

Keep changes scoped to one milestone slice. Describe assumptions, security impact, migration/compatibility impact, tests, limitations, and the next dependency. Avoid drive-by formatting or refactors, especially in a shared worktree. Never commit credentials, generated production data, dependency caches, build outputs, or captured customer diagnostics.
