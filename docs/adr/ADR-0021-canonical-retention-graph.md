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
`no_known_hold` is not a deletion certificate. The leased cleanup repeats
these checks after locking its candidate and bounds the number of child rows
removed in one transaction.
Migration 0179 supplies that per-run, operator-invoked cleanup primitive.
It requires the dedicated retention lease and migrator target scope, locks
the collection/run/query/plan graph and overlapping v3 registry rows,
rechecks the holds and catalog dependencies, and removes at most 4,096
child rows. It records counts, policy revision, attestation and fence in a
durable retirement ledger plus audit. Collection-run accounting and shared
ciphertext remain; the orphan-payload worker handles ciphertext later.
The migration also fixes the shared M7 append-only trigger so deleting a
plan does not access an `instance_id` field absent from that table. Migration
0180 adds the target/window run index concurrently for bounded candidate
scans. Migration 0181 moves candidate discovery into the existing leased
retention worker. A migrator-owned, collector-callable step scans at most 32
targets or retires one old run per cycle, persists its cursor and hold result,
and revisits held runs on the next sweep. Oversized runs stay in a separate
blocked-run ledger for operator review. The target-scoped hold and delete
functions remain ungranted to the collector; the step checks the live lease
before and after work. Collection-run history retirement remains open.

For **collection runs**, do not delete rows simply because `started_at` is old:
the existing `run_id` primary key is the target of foreign keys across the
repository. A partitioned successor needs a key containing its partition
time, with every referencing writer and foreign key migrated in stages.
Run-level cleanup occurs only after the per-class evidence and replay windows
have ended. Preserve an audit record of the count, cutoff, policy revision,
and recovery prerequisite for each batch.
Migration 0182 creates a UTC-day `telemetry.collection_run_v2` successor keyed
by `(run_id, started_at)` and mirrors new original-table inserts in the same
transaction. It registers and precreates a bounded day window and creates
older days on demand; an occupied or mismatched partition rolls back the
original insert. The original table remains canonical and retains all 20
inbound foreign keys. A catalog regression test inventories those constraints
and other run-ID columns that need an explicit lifecycle decision. The
successor has no retention policy or read route. Historical backfill, row
parity, replay verification, referencing-writer and foreign-key cutover, and
run-level hold checks are required before any original run history can retire.
Migration 0183 builds the original table's ascending target/collector/time/run
index concurrently. Migration 0184 adds an explicit migrator-only,
target-scoped historical copy. It follows that index and processes at
most 1,000 starts per call, and compares every copied or previously mirrored
row with the original. Its durable cursor is a copy-progress marker, not a
fleet parity or cutover attestation; operators must sweep each target and
then perform independent, writer-fenced parity and replay checks.
Migration 0185 adds a read-only, migrator-only parity check for one target,
collector and UTC-day-bounded start-time window of at most 100,000 rows on
each side. It compares counts and ordered SHA-256 digests of complete rows
under one MVCC snapshot. Every target, collector and historical/live window
still needs coverage, followed by a writer-fenced final sweep before cutover.
Migration 0186 records checked windows with a target/day generation. Inserts
into either the canonical or successor run table advance that generation, so
source-only and successor-only writes invalidate prior certificates. The
target/collector coverage report merges only passing, current windows and
exposes the first gap. This is preliminary coverage: it does not enumerate
every fleet target and collector or provide the final writer-fenced sweep.
Migration 0187 adds a migrator-only keyset page over every registered target
and distinct collector contract, including combinations with no run rows. It
preserves the caller's target scope after the page and reports incomplete
backfill or coverage per pair. Pages are an operator inventory, not one
coherent attestation while targets, contracts or runs can change.
Migration 0188 checks every target/collector pair inside one migrator-owned
transaction while SHARE locks block target/contract registration, run writes,
partition changes and parity-ledger updates. It refuses disabled mirror,
generation, append-only or forced-RLS gates, detached successor partitions,
incomplete coverage and an operator-selected pair bound. The function returns
only a transient verification result: future reader and foreign-key cutover
must rerun it and make their changes before releasing the same transaction's
locks. It does not itself switch reads or retire original run rows.
Migration 0189 adds an unpartitioned, globally unique `run_id` identity
bridge with a foreign key to the successor's `(run_id, started_at)` key.
Successor inserts mirror identity rows in the same transaction, so a reused
run ID fails even across UTC partitions. This bridge gives the 20 existing
single-column child foreign keys a future target without requiring every
child to carry the partition key. Historical successor rows still need an
explicit identity backfill and parity check. No child foreign key moves in
this migration; the original run table remains canonical. A populated
PostgreSQL test confirms the bridge FK blocks detaching a successor day while
its run ID is still referenced by the bridge.
Migration 0190 adds an explicit migrator-only, target-scoped bridge backfill
after the successor copy completes. It follows the original run keyset in
batches of at most 1,000, compares each complete original row with its
successor, and verifies each copied or already-mirrored identity. A mismatch
rolls back the batch and cursor. A completed cursor is still not a global
identity parity or foreign-key cutover certificate.
Migration 0191 adds an independent, read-only, migrator-only identity check
for one target, collector and UTC-day-bounded successor window. It requires
both copy cursors to complete, compares the ordered run IDs and start times
against the identity bridge under one snapshot, and rejects windows above
100,000 successor rows. A missing bridge row fails parity. Every historical
and live window still needs coverage under the writer fence before any child
foreign key can move; this single-window result is not a cutover gate.
Migration 0192 extends the parity ledger with identity counts, hashes and a
separate identity result. Existing windows default to identity-unverified and
must be re-recorded. Coverage now requires both backfills and both parity
results; the fenced fleet check also locks the bridge and its backfill state
and requires the mirror, append-only, forced-RLS and bridge-FK gates. The
result remains transient. A future child-FK cutover must run it and make the
reference changes in the same fenced transaction.
Migration 0193 adds an operator-invoked staging function for the 20 reviewed
single-column child foreign keys. Each call rechecks fleet parity under the
writer fence, confirms the selected original FK's column and semantics, and
adds a `NOT VALID` FK to the global identity bridge. This enforces future
child writes while preserving the original FK and avoiding a historical child
scan in the short staging transaction. Validate the staged constraints over
existing rows separately, then recheck the full graph under a final fence
before removing any original FK. Neither stage nor validation retires runs.
Migration 0194 validates one staged FK per operator call, including its
partition children, while preserving the original FK. Validation scans
historical child rows; the staged FK already checks concurrent writes, so the
function takes the table's validation lock without holding the fleet writer
fence for the scan. It also exposes the reviewed 20-table inventory to the
migrator for the final cutover. A validated constraint is still not permission
to drop the original run table or its references without a fresh fleet proof.
Migration 0195 adds the operator-invoked final child-reference cutover. It
requires all 20 staged identity FKs to be validated and enabled, locks their
parent relations in a stable order, repeats the complete fleet parity proof
under the writer fence, and drops the 20 original FKs in one transaction. A
durable append-only ledger records completion; a partial or unrecorded graph
is rejected. This changes child referential integrity only. The original run
table remains the writer and read source until their own verified cutovers.
The active PostgreSQL catalog still has original-run references in collector
commit and scheduling functions, alert evaluation, reporting projections,
retention helpers, and two reporting views. A catalog regression test records
those current definitions, including function arity, so reader and writer
cutovers must account for the actual installed objects rather than every
historical migration text. Migration 0196 adds private shadow readers for
activity sessions, requests, current blocking, and blocking history over the
partitioned successor. A populated PostgreSQL test compares both pages and
cursor behavior with the public readers. The shadow functions are executable
only by the migrator; routes remain on the original readers. The writer must
remain dual-written until all active consumers move.
Migration 0197 wraps the four public M5 readers in a shared route that starts
on the original implementations. The migrator can enable partitioned reads
only after the writer-fenced fleet parity proof succeeds. The original readers
remain private rollback targets; no route changes when the migration installs.
Migration 0198 adds private shadow readers for current wait summary, wait
history, and category trend. Their baseline, cursor, reset, and bucket outputs
are compared with the original readers on populated PostgreSQL fixtures before
any public wait route is changed.
Migration 0199 adds an independent route for the three wait readers. It starts
on the original implementations and only a migrator can enable partitioned
reads after the writer-fenced fleet parity proof. The original functions stay
private and available for rollback.
Migration 0200 adds private partitioned-run versions of the M5 evidence and
collector-health views. The health view feeds alerting and other readers, so
its populated output and privileges are checked before its public definition
or dependent alerting queries change.
Migration 0201 keeps the collector-health view's public identity and security
barrier while routing it through private original and partitioned definitions.
It defaults to original data. Migrator activation requires the writer-fenced
fleet parity proof, and the original route remains available for rollback.
Dependent alerting functions continue to resolve the public view.
Migration 0202 adds a private partitioned-run M9 latest-header function. It
preserves the completion-frontier selection and target-scope check. Populated
completed, unfinished, cross-target, and invalid-collector cases are compared
with the public header before any operational-health route changes.
Migration 0203 stages a default-original route for that header. Activation
requires the writer-fenced fleet parity proof, leaves the original header
private for rollback, and keeps direct server access on the public function.
Migration 0204 keeps the public M5 snapshot-evidence view and its security
barrier while routing it with the activity-reader switch. Installation keeps
original runs; fenced activation moves both the M5 readers and evidence view
to partitioned runs, and rollback restores the original evidence source.
Migration 0205 adds a private partitioned-run SQL volume snapshot reader.
Populated first and cursor pages are compared with the public reader; this
migration does not change the public route or its original-run dependency.
Migration 0206 routes that public reader through private original and
partitioned functions. The installed route remains on original runs; only a
migrator can activate the successor after the writer-fenced fleet proof, and
the original route remains available for rollback.
Migration 0207 adds private partitioned-run readers for workload and database
activity overview history. Populated metric and session fixtures compare their
outputs before either public overview route changes.
Migration 0208 gives those two public overview histories one default-original
route. Migrator activation repeats the writer-fenced fleet proof, then both
histories read the partitioned successor; rollback restores the original
definitions while original rows remain available.
Migration 0209 stages private partitioned-run readers for Buffer Node and
Buffer Manager PLE trends, node enumeration, and visibility gaps. Populated
NUMA and aggregate samples are compared before changing the public readers.
Migration 0210 routes those four public PLE readers together, defaulting to
the original definitions. Migrator activation requires the writer-fenced
fleet proof, and the original path remains available for rollback.
Migration 0211 stages a partitioned-run query-performance database catalog
behind a default-original route. A populated inventory checks name filtering,
parity, server access, and fenced activation before original runs can be
retired from this display lookup.
Migration 0212 adds private partitioned-run M10 backfill and rollup-input
readers. A populated raw-metric fixture compares both result contracts while
the collector-facing analytics functions remain on original runs.
Migration 0213 routes both collector-facing M10 readers together, initially
to the original implementations. Activation requires the writer-fenced fleet
proof; rollback restores the original path while those runs remain retained.
Migration 0214 routes alert evidence reconciliation and claim validation through
the collector-health read switch. The private run view exposes only the identity
fields those functions need; the switch remains on original runs until fenced
fleet parity is proven, and rollback restores the original source.
Migration 0215 gives due-work listing and claiming a separate run lookup switch.
The successor lookup uses the global run-ID bridge to supply the partition key;
activation repeats the writer-fenced fleet proof. Installation and rollback
keep active-run lease checks on the original writer.
Migration 0216 uses that switch for collection-run begin/replay and abandoned-run
identity reads. The begin function still inserts into the original writer; this
stages its read half ahead of a coordinated writer and outcome-trigger cutover.

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
