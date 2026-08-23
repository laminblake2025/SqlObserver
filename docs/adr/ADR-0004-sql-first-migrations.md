# ADR-0004: SQL-first migrations

- Status: Accepted
- Date: 2026-08-23

## Context

The PostgreSQL repository will use schemas, range partitions, indexes, views, functions, binary ingestion, security grants, and operational metadata whose exact SQL matters. Automatic ORM generation can obscure permissions, locking, data motion, and version-specific behavior, and it weakens the ability to reproduce and audit upgrades.

## Decision

Manage production repository changes with immutable, monotonically numbered PostgreSQL SQL migration files. Record and verify a checksum for every applied migration. A released migration is never edited; corrections use a new forward migration. Do not use automatic ORM schema generation.

Migrations explicitly qualify objects by schema, use UTC semantics, parameterize runtime data, set appropriate lock/statement expectations, and declare transactional boundaries. Versioned functions and views are installed through migrations even when their readable definitions also live in dedicated source folders. Seed and test data are not production migrations.

The migration runner acquires exclusive migration coordination, verifies the ordered history and checksums, fails on drift or unknown gaps, and records a durable result. Destructive or long-running transformations require recovery guidance, representative-volume tests, and an operator-visible plan.

## Consequences

- Repository shape, permissions, and upgrade history are reviewable and reproducible.
- PostgreSQL-specific SQL is an intentional part of the product rather than an incidental provider detail.
- Developers must write upgrade and failure-path integration tests and cannot repair deployed history in place.
- Downgrade may require restore or an explicit compensating migration; reversible migration syntax is not assumed to make data recovery safe.
- Schema models in application code must follow the migration truth, not generate it.
