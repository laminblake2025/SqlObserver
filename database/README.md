# PostgreSQL repository assets

This directory contains the SQL-first repository definition for PostgreSQL 18.x.
PostgreSQL is SqlObserver's application repository; it is not a monitored target.

## Deployment authority

Only the monotonically numbered files in `migrations/` are deployable. The migration
runner must read them in numeric filename order, verify every lowercase SHA-256 value
in `migrations/checksums.sha256`, reject gaps and unknown files, and record the exact
checksum in `system.schema_migration` after each migration commits. Released migration
files are immutable. A correction is a new forward migration.

Files in `functions/` and `views/` are non-deployable review indexes that point to the
complete authoritative definition in a numbered migration. Definitions are not
duplicated, so there is no second source to drift or execute accidentally.

Automatic ORM schema generation, automatic repair, and runtime DDL outside the
allowlisted partition functions are prohibited. `seeds/` and `testdata/` never form
part of a production migration.

## Bootstrap and roles

Migration `0001` must run against a dedicated, empty database using a controlled
bootstrap login that can create roles and schemas. It creates four cluster-wide
NOLOGIN group roles and makes the bootstrap login a member of
`sqlobserver_migrator`. Production LOGIN roles and their credentials are provisioned
outside these files and are granted only the appropriate group role:

- `sqlobserver_migrator` owns repository objects and applies reviewed migrations;
- `sqlobserver_server` has bounded read access and append-only audit access;
- `sqlobserver_collector` writes observations and invokes lease/partition functions;
- `sqlobserver_auditor` reads the append-only activity audit.

None of the group roles can log in, carries a password, can create a database or role,
bypasses row security, replicates, or is a superuser. `PUBLIC` has no repository schema
access and receives no default object privileges. The MCP bridge has no repository
role or credential.

## Time, partitions, and retention

All persisted instants use `timestamptz`; migrations and programmable objects set UTC
explicitly. `telemetry.raw_metric_sample` is partitioned by UTC day and
`events.diagnostic_event` by UTC month, with no default partition. Missing partitions
therefore fail visibly instead of silently accumulating data in an unbounded table.

The collector may create only the fixed-name daily and monthly child tables through
the security-definer functions in `control`. Those functions accept typed dates, use
repository advisory locks, quote computed identifiers, and register exact UTC bounds.
Partition indexes are inherited from the partitioned indexes on each parent:

- BRIN on the append-oriented time column for broad time scans;
- B-tree on `(instance_id, time DESC)` for the primary per-instance history path.

Retention policy rows are installed disabled and without invented durations. M2 does
not include a detach/drop function or background database job. A later milestone must
add preview, recovery, audit, and coordination gates before enabling retention.

## High-volume ingestion contract

The column order of `telemetry.raw_metric_sample` is the binary `COPY` contract:

1. `observed_at`
2. `sample_id`
3. `instance_id`
4. `metric_key`
5. `metric_value`
6. `dimensions`
7. `collected_at`

The collector's database role has `TEMPORARY` solely for bounded, transaction-local
staging tables declared `ON COMMIT DROP`. Callers create the destination partition,
assert the fence in the same transaction, binary-COPY typed values into staging, move
them into the parent with conflict-safe accounting, and pass the same bounded typed
values to collector-only replay validators before the final fence assertion and
commit. The SECURITY DEFINER validators use fixed search paths and schema-qualified
parents; they never inspect caller-controlled temporary relations and do not widen
table SELECT privileges. Metric replay excludes repository-assigned `collected_at`;
event replay includes caller-owned `collected_at` plus the fixed severity/metadata
envelope. Writers never concatenate SQL values and must report divergent identities,
rejected rows, and sample loss. There is no target SQL or production connection
material in this directory.
