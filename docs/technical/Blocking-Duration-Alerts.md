# Blocking duration alerts

The revamp plan requires alert coverage beyond engine counters and backups.
Blocking duration is the next signal being implemented. The source contract
below is implemented and verified in PostgreSQL; rule activation is pending the
durable queue and independently verified state/outbox commit. No blocking rule
has been enabled in the product catalog yet.

## Signal meaning

Use the longest observed `wait_duration_ms` among the current blocking collector's
retained edges, expressed in seconds. This is the duration of a current observed
wait, not the total lifetime of a blocking episode or a sum across parallel tasks.
The existing SQL source excludes self-blocking and the collector's own session.
The repository source independently rejects self-blocking rows.

Only a current, complete, loss-free snapshot can supply a value. A successful
empty snapshot supplies zero; missing rows never supply zero. Missing/failed
collection, disabled collectors, stale timestamps, partial/lossy output,
revision changes, mismatched row accounting, self edges and values beyond the
exact-double integer bound supply unknown evidence. Unknown evidence must not
resolve an active alert and must interrupt pending fire/clear confirmation.

## Implemented repository source

Forward migration 0297 adds `alerting.read_blocking_alert_evidence`. It requires
the current target revision/scope and the live `alerts/evaluation/reconcile`
lease. Target and schedule share locks hold its captured context. Only the
Collector can execute it; Server, Auditor and PUBLIC cannot. The function is
owned by the migrator and retains forced row-level security.

The function uses the existing routed collector-health view, so original and
partitioned collection-run readers remain supported. It chooses the latest run
for the current schedule revision, including failures; it cannot fall back to an
older successful run. Complete evidence requires successful, non-truncated,
loss-free accounting, no rejected items and an exact retained-edge count equal
to both source/output counts. The indexed target/run query reads at most 1,025
rows and treats more than 1,024 as inconsistent evidence. No paging or omitted
look-ahead row can masquerade as a clear snapshot.

Freshness is limited to twice the configured collection interval, capped at two
minutes, and applies to completion and every observed edge timestamp. Future
source timestamps are unknown. The validity deadline is the earliest completion
or observed-row expiry. The bounded envelope carries target/revision, schedule,
run, completion digest, source timestamps, retained count and duration/reason.
Repository read time is returned separately; it is excluded from the source
digest so re-reading an unchanged snapshot does not create a new source identity.
The SHA-256 digest binds the exact source JSON envelope. The typed
Collector port validates its closed shape, size, digest and source invariants,
with a five-second call budget. It is registered in Collector composition.

## Remaining activation work

1. Add the product-owned blocking rule kind/template, duration threshold and
   hysteresis settings, with explicit fire/clear confirmation semantics.
2. Capture this source transactionally in the fenced evaluation queue. Bind
   operations to target/rule revisions and complete source identity. Refresh or
   supersede expired/unclaimed captures; do not replace another live claim.
3. Independently derive and verify state transitions in PostgreSQL before
   committing state, history, outbox and audit atomically. Unknown data holds
   active episodes and resets confirmation streaks. Replays must be exact.
4. Revalidate captured source/revisions/expiry at claim and commit. Allow each
   distinct collector snapshot to count once, so polling cannot manufacture
   sustained blocking or clear confirmation from one unchanged observation.
5. Activate the catalog only after native tests cover fire, hysteresis, clear,
   unknown interruption, maintenance, acknowledgement, revision/lease races,
   replay and outbox uniqueness. Expose the template/settings in Rules and
   describe source gaps clearly in the alert inbox.

## Current verification

Two native PostgreSQL 18 checks exercise the restricted port and full migration
history. They verify complete empty and populated snapshots, the 65-second
maximum, exact target separation, both run-reader routes, denied scopes/owners,
expired leases and function ownership/grants. They cover missing, partial,
failed, stale/future, disabled, reconfigured, wrong-revision, self-blocking,
overlarge-value and 1,025-row sources; every unavailable case remains unknown.
Evidence: `artifacts/blocking-alerts/blocking-alert-evidence.trx`.

The fixtures seed repository rows under their isolated bootstrap role to test
source integrity and corruption handling. They do not run SQL Server collection
or prove firing/delivery behavior. Those checks belong to the remaining
activation work above.
