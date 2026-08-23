# ADR-0008: Partitioning and retention

- Status: Accepted
- Date: 2026-08-23

## Context

Raw time-series collection is expected to dominate repository volume, while diagnostic events are lower volume and often queried over longer windows. Retention deletion, time-range access, and ingestion must remain predictable as the repository grows. A single unpartitioned table or indiscriminate indexes would make retention and maintenance expensive.

## Decision

Use PostgreSQL native range partitioning on UTC event/sample time:

- daily partitions for high-volume raw telemetry;
- monthly partitions for lower-volume diagnostic events;
- derived aggregates partitioned only when measured volume and retention operations justify it.

Use BRIN indexes for large append-oriented time ranges and B-tree indexes for justified instance/time access paths. Indexes must answer a measured access pattern; they are not added by template. Use binary `COPY` for bounded high-volume ingestion. Deduplicate query text and plans into sensitivity-controlled stores and reference them from observations.

Retention is an explicit, versioned policy per data class. Partition creation occurs ahead of the active UTC boundary. Expiry removes eligible partitions only after a previewable policy evaluation, lease/coordination checks, audit, and recovery prerequisites. Late data has a bounded acceptance window and a defined rejection/visibility-gap outcome. No implicit local-time conversion participates in routing or expiry.

All objects are created and evolved through [immutable SQL-first migrations](ADR-0004-sql-first-migrations.md). Partition management is a collector-service responsibility coordinated through PostgreSQL leases; it is not an unmanaged database cron side effect.

## Consequences

- Time-window queries and retention can avoid row-by-row deletion at expected volumes.
- UTC boundary correctness, default/missing partition behavior, late arrivals, future partition creation, index inheritance, and detach/drop recovery require integration and performance tests.
- Exact retention durations remain a documented operator/product policy to be chosen with capacity and compliance evidence; the architecture does not invent a universal duration.
- Too many partitions or indexes can harm planning and writes, so production sizing gates are required.
- Deduplication reduces repeated sensitive payloads but introduces access-control, reference-integrity, and cleanup responsibilities.
