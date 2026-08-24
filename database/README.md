# PostgreSQL repository assets

Migration `0011_query_performance.sql` adds M7’s append-only, target-scoped Query
Store/plan-cache evidence tables, cache baselines, protected-content links, and
bounded status/top/history/plan projections. Runtime collection commits require
the target claim and completion digest; content remains unavailable until M12.

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

Deploy the Server and Collector with distinct LOGIN roles and distinct
`ConnectionStrings:SqlObserverRepository` values: the Server login is a member only of
`sqlobserver_server`, while the Collector login is a member only of
`sqlobserver_collector`. Do not share one elevated repository login between processes or
grant either runtime login `sqlobserver_migrator`/`sqlobserver_auditor`; process separation
is part of the least-privilege boundary.

None of the group roles can log in, carries a password, can create a database or role,
bypasses row security, replicates, or is a superuser. `PUBLIC` has no repository schema
access and receives no default object privileges. The MCP bridge has no repository
role or credential.

## M3 target and capability persistence

Migration `0007` extends the M2 identity table without invalidating identity-only legacy
rows. New registrations must provide a structured host plus exactly one named instance
or explicit TCP port, a bounded connect timeout, optional certificate host, Windows
integrated service identity, and mandatory validated encryption. No function accepts a
connection string, reusable secret, password, arbitrary driver keyword, or target SQL.
Legacy rows remain readable by identity but are excluded from discovery until explicitly
registered through the M3 contract.

Server-only functions register, update, retire, and request rediscovery under optimistic
revision fencing. Each outcome appends its bounded administrative audit in the same
transaction, so an audit failure rolls back the target mutation. The sanitized
security-barrier status view supports authorization-scoped key/identifier paging; the
runtime applies the resolved target scope before its cursor and hard limit. The generic
audit primitive is owner-only. A separate server-only entry point can append only denied
user-administration actions with one of the closed authorization-denial reasons; the
collector can execute neither audit entry point directly.

The collector receives only a repository-clock due list of at most sixteen configured,
non-retired targets and a lease/revision-fenced profile recorder. Discovery attempts,
profile identities, capability reasons, and permission evidence are append-only. The
recorder persists bounded collector provenance, structured SQL Server identity and
security evidence, UTC validity, and closed reason codes, then reasserts the worker fence
before returning. Refresh timestamps must be finite, intervals are constrained to one
minute through seven days, and expiry is anchored to repository `recorded_at`; caller
clock skew in `checked_at`/`valid_until` cannot make work immediately overdue or defer it
indefinitely. Single and bounded bulk readers expose the latest sanitized profile without
widening table access. Direct mutation of capability history and all M3 access through
`PUBLIC` are denied.

## M4 collector runtime and core health

Migration `0008` installs the exact three-contract M4 registry, per-target schedules,
durable circuits, append-only run/outcome and visibility-gap history, and database/file
snapshots. The registry stores only reviewed digests and bounds; target SQL remains in the
checksum-pinned Collector assembly. Reconciliation, due-work selection, run start, and
atomic completion are exposed only through collector-role functions with fixed search
paths, bounded arguments, repository-clock timestamps, target-revision checks, and worker
lease fencing.

A completion transaction persists output, exact accounting, a closed outcome/reason,
explicit loss evidence, schedule advancement, and circuit transition together. Exact
replay is idempotent; divergent identities, stale schedule revisions, stale target
revisions, and lost fences fail closed. Non-success and partial runs create append-only
visibility gaps so absence is never treated as health. Direct runtime-role access to M4
base tables and the internal health view is denied.

Server-role security-definer readers return one target's core health or bounded keyset
pages of database/file health. They expose logical names and normalized evidence only;
physical file paths, target connection details, provider messages, arbitrary SQL, and
secrets are outside the schema and function contracts.

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

## M6 deadlock evidence

Migration `0010_deadlocks_extended_events.sql` adds typed, append-only deadlock
summary, participant, and relation evidence. The Collector uses only the fenced
`control.commit_deadlock_collection_run` function, which validates the exact M6
contract, worker lease, target revision, schedule revision, request digest, and
bounded arrays before inserting. Fingerprints provide deterministic duplicate
accounting and replay identity. Raw Extended Events XML is never stored; malformed
or truncated source events remain visible through collection-run loss and
visibility-gap evidence. Server reads use only target-scoped security-definer
`control.list_deadlocks` and `control.get_deadlock` functions.

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
