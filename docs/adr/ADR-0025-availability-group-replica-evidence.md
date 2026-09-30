# ADR-0025: Preserve per-replica availability-group evidence

Status: proposed, 2026-09-29

## Context

The current `availability-groups.health` query reads database-replica rows only
where `is_local = 1`. It cannot show a primary's remote secondary queues or
commit progress. Its database snapshot key contains a group and database
fingerprint, but no replica identity, so it cannot safely store more than one
replica's state for the same availability database. The query also requires
`operational_state_desc` for every replica before marking state available.
SQL Server deliberately returns that value as NULL for a remote replica on
the primary; NULL there does not mean the remote row was not observed.

Microsoft documents that `sys.dm_hadr_availability_replica_states` exposes
all replicas when queried on the primary and only local information on a
secondary. A remote replica's operational state is NULL even when its role
and connection state are known. See
[replica states](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-hadr-availability-replica-states-transact-sql?view=sql-server-ver16).
`sys.dm_hadr_database_replica_states` returns primary and corresponding
secondary database rows on the primary, and local rows on a secondary. Its
queue sizes are kilobytes, rates are kilobytes per second, and
`last_commit_time` is a nullable `datetime` without a time-zone offset. The
primary's remote values are its latest reported view, which may be stale.
See [database replica states](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-hadr-database-replica-states-transact-sql?view=sql-server-ver16).

## Decision

Introduce a version-2, checksum-pinned passive AG collector and keep its
version-1 assets and history immutable. Version 2 has two bounded streams:
configured replica state, and per-replica database state. The database key is
`(group_id, replica_id, group_database_id)` before hashing, and all three
identities remain part of the persisted key and cursor. A configured replica
or database whose dynamic state is absent is still represented with explicit
unavailable coverage; it is not silently dropped or treated as healthy.

On a primary, read all replica and database rows the DMVs expose. On a
secondary, retain local-only visibility and label it as such. Persist the
observation location and visibility scope with every row. Distinguish an
expected NULL remote `operational_state_desc` from an unknown role or
connection state. A remote replica can have available connection evidence
without claiming that its local operational state was observed. Snapshot
completeness must consider whether the expected fields for that location were
observed, not whether every nullable DMV column is populated.

For each database/replica row, retain nullable log-send queue KB, log-send
rate KB/s, redo queue KB, redo rate KB/s, synchronization state/health,
database state, suspension state, and source `last_commit_time`. Never convert
that offset-free source time to UTC from the collector host's clock. Keep the
raw local time explicitly labelled as such and derive an approximate
primary-to-secondary commit gap only when both comparable rows come from the
same primary scan. Do not use a zero `secondary_lag_seconds` as proof of health
when data movement is suspended; the DMV documents zero in that case.
Null and unavailable values stay distinct from zero.

Store version-2 rows in new append-only, UTC-day-partitioned tables under
forced target row-level security and restricted grants. Do not add remote
rows to the version-1 `(group, database)` table, which would collapse their
identity. The fenced commit validates row and byte bounds, all identities,
units, time shape, and the collector version before publishing the complete
run. Replay must be identical; a different payload for the same run fails.
Retention uses the existing policy, recovery-attestation, reader, and
partition gates, with new classes reviewed for both version-2 parents.

Expose the location and coverage labels with queues, rates, and commit
progress in the API, MCP, dashboard, and HA health checks. An alert on queue
growth must use a duration or confirmation window and avoid firing on a
missing/stale remote row. Keep secondary-only snapshots visibly partial for
fleet-wide conclusions. No interface should claim that the last commit time
is a UTC instant or an exact RPO.

## Delivery and verification

The staged source now carries an observing-replica fingerprint and a nullable
local/remote flag alongside the visibility scope. Forward migration 0264 stages
`telemetry.availability_replica_evidence` and
`telemetry.availability_database_replica_evidence` as private partitioned
parents. The latter preserves the full group/replica/database identity. No
partitions, retention route, writer, or schedules are activated by this
foundation; those gates must be installed together before collection starts.

The PostgreSQL 18 foundation fixture verifies distinct per-replica rows for one
database, NULL versus zero queue values, offset-free commit time, append-only
history, restricted grants, and target-scoped reads. Native primary/secondary
SQL behavior and the remaining delivery gates below are still unverified.

The staged .NET parser reads the pinned 20-column shape sequentially and
produces an immutable v2 snapshot. It re-keys SQL digests by identity class,
target and target revision, retains NULL queue/rate values and offset-free
commit time, and validates the configured replica/database relationship.
Complete coverage is separate from health: observed faults remain faults,
while secondary-only visibility and missing expected state fields carry
explicit visibility loss. Row truncation retains a disclosed prefix; byte
truncation withholds the prefix entirely. Response accounting conservatively
includes the expanded snapshot envelope. Focused tests exercise these
boundaries, cancellation and malformed input. The active v1 validator still
rejects this v2 snapshot; no collector registration or writer is activated.

Forward migration 0265 stages a private version-2 fenced commit and a separate
.NET persistence route. The commit verifies the exact JSON shape, replica and
database relationships, coverage, row/byte bounds, lease/run/schedule identity,
and circuit transitions. PostgreSQL computes and verifies the canonical payload
digest; identical retries replay and changed payloads fail. Snapshot rows,
completion accounting, visibility gaps and schedule advancement commit together.
Failed and rejected output never becomes replica evidence. The PostgreSQL 18
fixture exercises primary faults and NULLs, secondary coverage, row and byte
truncation, no-data and failure receipts, divergent replay, malformed payloads,
stale fences and rollback when the database partition is absent. The existing
version-1 AG commit regression also passes. Fixture-only contracts and manually
created partitions test this private boundary; they do not qualify production
activation. Collector/server/auditor execution remains revoked until partition
maintenance, retention and the versioned catalog gate are installed.

Forward migration 0266 registers both v2 parents with independent 30-day
retention policies and a three-partition floor. Catalog-lease-fenced maintenance
creates only UTC yesterday/today/tomorrow partitions, verifies their exact
bounds, owner, parent and registry identity, and keeps child grants private.
The existing retention worker previews and retires these populated parents
through recovery-attestation, reader, backfill and minimum-floor gates, followed
by detached grace and a fenced audited drop. A policy pause during grace
reattaches the evidence. Server and web policy/preview contracts recognize
both new classes. PostgreSQL fixtures verify these paths and that old v1
operator policy revisions survive the upgrade. The v2 collector catalog and
commit execution grant remain inactive; native AG qualification is still open.

The staged v2 collector now executes the pinned source with a single bounded
deadline, major-specific server-state grants and VIEW ANY DATABASE evidence.
It uses the secret-backed identity key and microsecond-aligned UTC observation
time, preserves the raw offset-free commit time, and reports row truncation,
byte truncation and incomplete visibility separately. Malformed input becomes
an output-invalid receipt without publishing a previously read prefix. The v2
validator checks run identity, payload accounting, coverage and failure shape;
the active v1 registration continues to reject v2 snapshots. Focused execution
tests cover primary faults, secondary visibility, no-data scans, limits,
permissions, cancellation, SQL error classification and mixed-version rejection.
The production registry and catalog still select v1, and the v2 commit grant
remains private pending the versioned rollout gate and native AG qualification.

Forward migration 0267 stages the exact v2 catalog row and a private rollout
function. Under the catalog lease it verifies enabled retention, creates and
checks the six current UTC partitions, locks the activation gate and schedules,
and refuses to switch while any v1 run remains active. A failed cutover rolls
back partitions, schedule changes and worker registration together. Successful
reconciliation registers the worker for five minutes; old or expired workers
cannot claim AG v2 work. Target creation and revision changes share the
activation lock and follow the selected version. PostgreSQL fixtures verify
these boundaries, idempotent repeats and privilege separation. The rollout
function and commit remain owner-only, with no production registry switch or
collector grant; native AG qualification remains required before activation.

Forward migration 0268 stages scoped server read functions for the v2 receipt
header and independently paged replica/database-replica streams. A fixed
repository completion cutoff selects a current-revision run within seven days;
the cursor carries that cutoff, run, revision and the full stream key. Reads
return at most 100 items plus one lookahead. A source timestamp is nullable when
no retained rows carry it, rather than substituted with receipt time. The header
distinguishes not-collected, no-data, byte-withheld, failed and retained evidence;
secondary visibility stays degraded. If retention removes either stream, it
reports the count mismatch and degraded coverage while allowing the surviving
rows to be inspected. Existing v1 header implementations explicitly exclude
v2 AG runs on both original and partitioned run routes. PostgreSQL fixtures
exercise paging, fixed-cutoff stability, stale keys, NULL versus zero units,
offset-free commit time, scope denial, empty/failure coverage and partial
retention. Production read routes still use v1.

The application now exposes separate typed replica and database-replica pages,
with target-scoped Viewer/Operator/TargetAdministrator authorization and a
five-second maximum repository deadline. Continuations bind the target, fixed
UTC cutoff, current revision, run and complete stream key; malformed bindings,
duplicate/out-of-order rows and stale runs fail closed. The PostgreSQL adapter
sets transaction-local target scope and reads the receipt and child page under
one repeatable-read snapshot. It trims one lookahead row and builds continuation
from the last returned item, preserving nullable state, queue/rate units and raw
offset-free commit time. Empty, failed, byte-withheld and partially retained
receipts retain their distinct coverage. Focused unit tests cover authorization,
request/page drift, cancellation and deadlines; the existing PostgreSQL fixtures
exercise the typed service and adapter through both streams and all 201 replicas
over three pages. These typed ports are composed into the Server for the
versioned HTTP surface below.

Versioned HTTP reads now expose
`GET /api/v2/observation-targets/{instanceId}/availability-groups/replicas` and
`GET /api/v2/observation-targets/{instanceId}/availability-groups/databases`.
Both accept `limit` (1–100, default 50), optional `atUtc` (UTC ISO timestamp ending
in `Z`, with at most six fractional digits), and optional `cursor`. The first
request defaults to the current UTC cutoff; a continuation restores its signed
cutoff when `atUtc` is omitted and rejects a different supplied cutoff. The
compact cursor authenticates target, revision, run, cutoff and every identity
key with a separate purpose for each stream, within the shared API signer's
existing byte limits. Responses include `schemaVersion: 2`, nullable
`sourceObservedAtUtc`, receipt `completedAtUtc`, coverage/accounting, and paged
items. Database items include their replica key, queues in KB, rates in KB/s
and `sourceLastCommitLocal` without an offset; null values remain explicit.
Authenticated reads return `Cache-Control: no-store`. Expected failures use
ProblemDetails with `code` and `correlationId`: invalid bounds/signatures are
400, scope denial 403, stale run/revision 409, unverifiable repository evidence
502, repository unavailability 503 and timeout 504. HTTP fixtures verify these
bindings and shapes; a production-pipeline PostgreSQL fixture pages both real
streams without gaps and preserves source-time and queue semantics. Version-1
routes and collector activation remain unchanged. MCP/UI v2 adapters and native
AG qualification remain open.

1. Stage the version-2 tables and exact three-part identity with a forward
   migration. Prove forced RLS, append-only history, replay, and retention
   dependencies in PostgreSQL 18.
2. Pin SQL Server 2019/2022/2025 query assets and a version-2 manifest. Add
   parser tests for primary, secondary, disconnected, NULL, and bounded rows.
   Qualify primary and secondary behavior on real AG lab instances using the
   least-privilege login and validated TLS.
3. Add a fenced versioned commit and catalog activation that waits for old
   runs to drain and prevents old workers from claiming version-2 work, as
   done for inventory v2. Preserve the version-1 read route for rollback.
4. Add paged version-2 reads, then API, MCP schema, and UI evidence views.
   Compare complete primary/secondary fixtures end to end before switching
   the default route. Verify unknown and stale evidence remains explicit.

## Consequences

The extra replica key increases row volume but prevents cross-replica
overwrites and makes send/redo queues meaningful. Primary scans can describe
remote replicas only as last reported to that primary; direct collection from
each secondary remains useful for local confirmation. Versioned storage and
catalog activation add rollout work, but preserve historical contract and
replay integrity.
