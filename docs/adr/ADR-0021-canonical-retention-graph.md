# ADR-0021: Canonical evidence and retention dependency graph

- Status: Accepted for the revamp; implementation staged
- Date: 2026-09-28

## Context

The repository has working, guarded partition retention for several written
streams, but the remaining high-volume relations do not share one lifecycle.
`telemetry.collection_run` is keyed only by `run_id` and is referenced by many
partitioned and unpartitioned evidence tables. The original M7 query family
uses run/query/plan foreign keys and append-only triggers; its observation
table has an `instance_id` and target/time index after migrations 0089-0111,
but is still unpartitioned. The newer Query Store group watermark and delta
tables are already partitioned and have enabled policies from migration 0143.

`events.diagnostic_event` is the canonical event envelope. A populated
`events.deadlock_summary` references it, and participants and relations
reference the summary. A PostgreSQL 18 integration test confirms that
detaching the monthly event partition while a deadlock summary exists fails
with SQLSTATE `23503`. The partition remains attached and the evidence stays
visible. Thus the 90-day diagnostic-event policy introduced in migration 0157
could not expire a month that contained deadlocks before migration 0160 added
an attached-grace step.

The old M5/M9 and other unused `_v2` parents are not a substitute for written
tables. Registering empty scaffolds or deleting them does not bound active
storage. The repository review's fleet estimate therefore remains a design
input, not a claim that current policies cover the whole repository.

The active M9 TempDB file partition references its same-day snapshot rows.
A populated PostgreSQL 18 test confirms that detaching the snapshot partition
alone fails with `23503`. In one transaction, detaching and dropping the file
partition first, then detaching and dropping the snapshot partition succeeds;
rollback restores both visible rows. These two written parents must share one
retention execution, not independent policies.

## Decision

Treat the **written relation and its dependent rows** as the retention unit.
Do not redirect a policy to a similarly named unused parent. Keep the
following canonical groups distinct:

| Evidence | Canonical relation or envelope | Retention relationship |
| --- | --- | --- |
| Raw metrics, M5 activity/waits/blocking, M9 and M10 snapshots, SQL volume, Query Store group deltas | Their actively written partitioned parents in `telemetry`, `events`, and `analytics` | Continue the fenced partition policy and registry path. |
| M9 TempDB snapshots and files | `telemetry.tempdb_snapshot` and `telemetry.tempdb_file_snapshot` for the same UTC day | Keep both attached and readable through grace. After all gates pass, drop the file partition before the snapshot partition in one fenced transaction. |
| Diagnostic events and deadlocks | `events.diagnostic_event` plus `events.deadlock_summary`, `deadlock_participant`, and `deadlock_relation` | Retire the event envelope and all typed deadlock rows in one reviewed operation. |
| Original M7 query performance | `events.query_performance_run`, `query`, `plan`, `observation`, status, wait snapshot, and text/plan links | Retain the complete run/query/plan graph while any observation, sensitive-content link, report, or reader dependency needs it. |
| Collection run history | `telemetry.collection_run` and outcome, loss, inventory/file, and other run-linked evidence | Retire a run only after every referencing evidence class is eligible and its replay/audit requirement is met. |
| Sensitive query text and plans | `security.protected_diagnostic_payload` with M7 content links | Remove orphan payloads only after the last link has expired; never use payload age alone. |
| Alert delivery/history/replay | Written `alerting` relations | Preserve pending delivery, idempotency, and audit evidence under separate policy gates. |

For **diagnostic-event months containing deadlocks**, use an attached grace
state: mark the partition as selected for expiry while leaving it attached and
readable for at least 24 hours. A policy revision, active reader lease,
analytics dependency, or missing recovery attestation cancels or postpones
the action. At the end of grace, a fenced transaction rechecks those gates,
sets the trusted retention context, removes typed deadlock rows for that exact
month, then detaches and drops the event partition. If the transaction fails,
it rolls back all row removal and leaves the event month readable. Bound the
typed-row work and surface an explicit blocked state when that bound is
exceeded. The detach-first path remains for other data classes. A
SecurityAdministrator's diagnostic-event `detach` action also begins attached
grace; the fenced worker completes its drop. A policy change during an
existing detached grace uses
the reattach path from migration 0159.

For **TempDB days**, select the snapshot and exact-range file partitions as
one unit. Both reader leases, backfill states, policy revision, minimum
partition floors, analytics dependencies, and recovery attestation must be
checked before grace and again before retirement. Enable only the snapshot
default after the worker and administrator path use the paired execution; keep
the file policy disabled. At expiry, one fenced transaction must detach and
drop the file partition first,
then the snapshot partition; a failure must roll back both and preserve reads.
Migration 0163 installs the fenced worker and excludes the pair from generic
detach. Migration 0164 routes administrator snapshot requests into paired
attached grace, makes preview check the file partition, and enables the
30-day snapshot default. Direct file detach remains blocked.

For **original M7 observations**, build a partitioned successor keyed by UTC
`interval_end`; its primary key must include that partition key. Preserve
`instance_id` and the `(instance_id, interval_end)` access path. Cut over the
fenced writer and target-scoped readers together after dual-write and
backfill/replay verification. Keep run/query/plan and content-link rows until
all dependent observation partitions and retained incidents/reports are
accounted for. A partition drop must not silently cascade through those
foreign keys. Use a separate bounded cleanup for graph nodes proven orphaned.
Migration 0165 establishes the interval-end partitioned successor and mirrors
new canonical inserts in the same transaction. Registered daily partitions
are prepared ahead of normal windows and created on demand for older bounded
source windows; a mismatched or occupied partition fails the original insert.
The original table remains the read source and neither table has an enabled M7
retention policy. Historical backfill, digest/count parity, replay validation,
and read cutover remain required before the old table can retire.
Migration 0166 adds a migrator-only, target-scoped historical copy in batches
of at most 1,000 rows. It resumes on the original observation primary key,
derives ownership from the query row when a legacy target key is null, and
checks every copied or already-mirrored row for equality. A mismatch rolls
back the batch and its cursor. Operators must call it explicitly per target;
completion of its cursor alone is not a global parity or cutover attestation.
Migration 0167 adds a read-only, migrator-only parity check for a target and
interval-end window of at most one day and 100,000 rows on either side. It
compares counts and ordered SHA-256 digests of complete normalized rows under
one MVCC snapshot; a different row, missing row, or extra row fails parity.
All historical and live windows must be covered, with a fresh sweep at the
cutover fence. One passing window cannot authorize changing readers.
Migration 0168 adds private successor-backed copies of the top-query and
query-history projections. Their result fields, ranking, look-ahead, cursor,
and snapshot rules match the active functions in focused PostgreSQL tests,
including a legacy null-target row and a live mirrored row. The server role
cannot call the copies. Other M7 readers and the writer remain on the original
relation while fleet-wide parity and replay gates are still open.
Migration 0169 adds the remaining active API projection dependency: a private
successor-backed plan-metadata reader with the same protected-plan-link
availability check. The focused test compares its result with the active
function for a plan-bearing observation and confirms no server grant. The
production API still uses the original plan-metadata function.
Migration 0170 records bounded, target-scoped parity windows and reports the
first uncovered UTC interval across source and successor evidence. Windows may
split a dense day, but each must fit within one UTC day. A successor insert
increments that target/day generation, making earlier checks for that day
stale; the mirror now processes targets in sorted order to avoid lock-order
inversion. It also corrects the empty-window SHA-256 seed width from migration
0167 in a forward function replacement. The coverage report is a preliminary
operator ledger, not a cutover attestation: final replay and writer fencing
still have to close the active day and the remaining dependency graph.
The production fenced M7 writer now has focused PostgreSQL replay coverage
against both observation relations: an idempotent retry leaves the successor
row count and day generation unchanged, while a rejected commit or divergent
replay leaves no extra successor row. This confirms the current mirror path;
it does not yet authorize switching the writer or readers.
The large-history ranking fixture also compares the old and partitioned
top-query projections on both cursor pages with 60,000 older observations,
120 recent observations, and a foreign target. The two 100/20-row pages match
under a five-second command limit for each successor query. This checks the
shadow read path at a realistic historical volume, not fleet-wide latency.
Migration 0171 exposes a migrator-only, 100-target keyset page of parity
coverage across every registered target, including targets with no M7 rows.
It restores the caller's target scope after each page and keeps server and
collector roles out. Operators can now find incomplete targets without
maintaining an external inventory. Pages remain a preliminary report:
concurrent target registration or writes between pages prevent this from
serving as the final, writer-fenced cutover attestation.
Migration 0172 installs a database-controlled read route for the three public
M7 projections. It starts on the original table. A migrator can call
`control.set_query_observation_v3_read_route(true)` only after one transaction
blocks target registration and both observation writers, confirms identity,
append-only, mirror, generation, and forced-RLS gates, and finds valid coverage
for every registered target. The same operation changes the route before
releasing the locks. `false` restores the original readers while original rows
are retained.
Focused PostgreSQL checks exercise an incomplete empty target, activation,
all three routed projections, server grants, and rollback. Canonical writes
still enter the original table and mirror into v3, so M7 retention and original
table retirement remain gated on a later writer and dependency-graph cutover.
Migration 0173 lets a migrator record the next uncovered window without
manually splitting every day. It chooses the earlier of the source and
successor `(row cap + 1)` interval ends, so both verifier scans stay bounded.
If more than the cap shares one timestamp, it fails visibly with SQLSTATE
`54000`; migration 0174 supplies a composite-key chunk verifier for that
timestamp. It keeps independent source and successor cursors and rolling
SHA-256 digests, and adds a one-microsecond key-chunk certificate to the
existing day-generation ledger only when both sides are exhausted. A later
write resets the checkpoint, and an extra successor row fails parity. It also
increments the day generation on original-table inserts independently of the
mirror, so a source-only write cannot leave an earlier certificate valid;
activation checks that trigger. Migration
0175 builds the original-table target/time/key index concurrently for directly
keyed rows; legacy null-target rows still use their owning query. Calls
stop returning rows once current coverage is complete, and live writes still
invalidate their UTC day through the existing generation ledger.
Migration 0176 adds an operator-gated M7 writer switch after the v3 read
route has passed the fleet certificate. The collector's text wrapper first
takes a writer lock on the original observation table, then invokes the
existing canonical run and protected-text logic. Once the mirror and
accounting exist in the same transaction, it checks the v3 row count and
deletes that run's original observations using the established migrator
retention context. The outer content wrapper still appends protected plan
links; replay performs no second delete. Activation drains writers under a
conflicting table lock and repeats the read-route parity gate. It blocks
later read rollback because new runs no longer have original observations.
Historical original rows and their dependencies remain until the M7
retention graph is implemented; this switch alone does not permit dropping
the original relation.
Migration 0177 moves the `m7_query_performance` policy from the unused v2
parent to the written v3 observation parent. It keeps the policy disabled
until the writer route is active, then enables a 90-day default only for an
untouched seed policy. The generic fenced worker can detach and drop an
expired v3 day after its reader, analytics, recovery, policy-revision and
grace gates. A populated PostgreSQL test confirms the v3 rows disappear
while their run and query parents remain. This bounds observation storage;
run/query/plan, wait, status and protected-link cleanup still needs a
separate dependency-aware operation.
Migration 0178 adds a target-scoped migrator preview for one M7 run. It
distinguishes attached observations from a detached v3 partition in grace,
and also reports active readers, incident evidence, analytics work, missing
collection outcomes or recovery attestation, policy age, and foreign keys
outside the reviewed graph. The preview is deliberately non-mutating;
`no_known_hold` is not a deletion certificate. A future leased cleanup must
repeat these checks after locking its candidate and bound the number of
child rows removed in one transaction.

For **collection runs**, do not delete rows simply because `started_at` is old:
the existing `run_id` primary key is the target of foreign keys across the
repository. A partitioned successor needs a key containing its partition
time, with every referencing writer and foreign key migrated in stages.
Run-level cleanup occurs only after the per-class evidence and replay windows
have ended. Preserve an audit record of the count, cutoff, policy revision,
and recovery prerequisite for each batch.

## Implementation gates

1. Migration 0160 adds an attached-grace lifecycle for diagnostic-event months
   to the fenced retention worker. Its PostgreSQL test covers selection,
   policy change, blocked reader, failed transaction, and successful
   retirement; the existing detach/drop path remains separate. Fleet-scale
   sizing of the 10,000-deadlock atomic limit remains an operational gate.
2. Publish the M7 partitioned successor with a forward migration. Verify
   duplicate/replay identity, cross-target forced RLS, UTC boundaries,
   backfill counts and digests, and old/new read parity before cutover.
3. Add graph cleanup and then collection-run migration. Use catalog foreign-key
   inventory and a zero-unaccounted-reference check before any run deletion.
4. Add alerting history/queue policies with delivery and idempotency holds.
   Measure per-instance storage and partition count at fleet scale before
   choosing final durations for each remaining class.

No migration may edit an applied migration or discard a populated relation
solely to satisfy a retention target. The SecurityAdministrator's policy
revision and recovery-attestation controls remain authoritative.
