# M10 database foundation

Migration `0014_analytics_host_replication_retention.sql` adds the PostgreSQL
foundation for host metrics, replication evidence, rollups, baselines,
forecasts, evidence packets, and incident threads.  All evidence is target
revision scoped and append-only.  Collector commits and reader projections are
fixed `SECURITY DEFINER` functions with `search_path` fixed to trusted schemas,
UTC timestamps, target-scope checks, row/byte bounds, and replay digests.

## Collector contract assumptions

The `host.metrics` and `replication.health` contracts are pinned to the
reviewed manifest and asset-bundle SHA-256 values in migration 0014.  No
runtime path or unchecked collector asset is accepted.

The canonical scheduled catalog is execution order 1–15: core/database and
activity collectors 1–8, `queries.performance` at 9, the four M9 collectors at
10–13, `host.metrics` at 14, and `replication.health` at 15.  The
`capability.connection` probe remains separate control-plane discovery and is
not scheduled as a target collector.  Replication requires the replication
feature only; host binding is an independent capability and endpoint.

## Partition transition

The migration creates fixed v2 parents for every eligible high-growth stream:
M5 activity/blocking, M6 deadlock detail, M7 query performance, M8 alert
history/delivery, M9 operational-health snapshots, M10 host/replication,
rollups, and evidence.  Existing parents remain available for compatibility.
`control.ensure_m10_partition_set(date)` creates and registers UTC daily
partitions D-1 through D+7 and monthly partitions for the current month plus
two future months.  Partition registry rows are authoritative; run, outcome,
and visibility-gap envelopes remain unpartitioned and indexed.

`control.run_m10_backfill` is deliberately fenced to at most 100,000 rows and
8 MiB per invocation and records resumable state one UTC day at a time.  It
does not issue an unbounded transactional copy.  Compatibility projections
under `reporting.m10_*_compat` allow readers to move to v2 independently.

## Retention safety

Retention rows remain disabled and `retain_for` remains NULL by default.  The
preview function reports missing recovery attestations, active reader leases,
incomplete backfills, pending analytics dependencies, partition floors,
retention windows, and detached state.  Detach requires a
valid attestation and no active lease; a detached partition receives a fixed
24-hour grace period before a drop can be attempted.  Drop failures are
recorded in `system.retention_drop_retry` for bounded retry and audit.

The local runtime closure includes in-process Server API contracts and
Collector composition checks for analytics worker ownership. PostgreSQL
integration, Docker/live target execution, sustained-load, and release/platform
certification remain pending gates; this milestone does not claim those
environments.

The migration repairs the M9 replay implementation with PostgreSQL core
`sha256(bytea)` and has no pgcrypto dependency.
