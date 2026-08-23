# ADR-0002: PostgreSQL repository

- Status: Accepted
- Date: 2026-08-23

## Context

SqlObserver needs a durable application store for configuration, high-volume time-series telemetry, diagnostic events, analytics, alerts, reporting, and audits. The monitored engine is Microsoft SQL Server; using target instances to store product state would violate isolation, complicate lifecycle management, and risk monitoring-induced target changes.

## Decision

Use PostgreSQL 18.x as the sole application repository for the initial release. It is not a monitored database engine and is never presented as one.

Organize data into the `control`, `security`, `telemetry`, `events`, `analytics`, `alerting`, `reporting`, `audit`, and `system` schemas. Use UTC for every persisted timestamp. Use PostgreSQL native range partitioning, deliberate BRIN/B-tree indexes, binary `COPY` for justified high-volume ingestion, and deduplicated query-text/plan storage as detailed by [ADR-0008](ADR-0008-partitioning-and-retention.md). Evolve the store only through [SQL-first migrations](ADR-0004-sql-first-migrations.md).

Application hosts access the repository with separate least-privilege roles. Consumers use application services, not table access as a public API. The MCP stdio bridge has no repository credential.

## Consequences

- Product workloads and lifecycle are isolated from monitored SQL Server instances.
- Operations require supported PostgreSQL 18.x provisioning, backup, restore, patching, capacity, and availability procedures.
- The collector and server depend on repository availability, while outages must remain visible and bounded.
- PostgreSQL-specific ingestion, partitions, leases, and SQL require dedicated integration/performance tests.
- Supporting a different repository would be a migration program and a new ADR, not a provider toggle.
