# Blocking duration alerts

The revamp plan requires alert coverage beyond engine counters and backups.
Blocking duration is the next signal being implemented. The source contract
below is implemented and verified in PostgreSQL. Private durable capture and
claim helpers are also implemented; runtime queue dispatch and the independently
verified state/outbox commit remain pending. No blocking rule
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

## Private durable capture and claims

Forward migration 0298 reserves repository rule kind 6 for blocking duration and
adds a forced-RLS `alerting.blocking_evaluation_capture` table. Its observation,
producer, claim and claim-verification functions are migrator-owned and private:
Collector, Server, Auditor and PUBLIC have no execution or table grants. The
existing catalog remains contract 3 and rejects creating a blocking rule. The
normal worker is not wired to the private producer/claim helpers yet. The current
commit dispatcher rejects their source kind instead of accepting a caller's
blocking transition through the engine path.

The producer holds target/rule locks, reads the 0297 source transactionally,
and binds each queued operation to target revision, rule revision, source
digest, read time and canonical operation/evidence identities. Observations
carry the maximum duration in seconds, or null for unknown evidence. An indexed
completed-operation lookup prevents an unchanged source digest from supplying
another confirmation for the same rule revision. Cancelled operations do not
consume the source. Pending unchanged captures retain their exact identities;
expired claims can be reclaimed without changing the observation or digest.

Each rule has at most one pending blocking capture. A source/revision change or
expired capture supersedes only available work, preserving another live claim.
Claim verification rereads the current source and checks the exact observation,
accounting envelope, deadline, target/rule revisions and owner/fencing token.
Changed evidence invalidates a live claim even though reconciliation cannot
steal it. Available completed/cancelled capture bodies are deleted in bounded
batches; queue metadata remains the source-consumption ledger. Future queue
retention must preserve that identity throughout the source validity horizon.
These helpers bound reconciliation and claiming to 100 operations per call.

The repository constraints reserve an upper-duration comparison with a
0–86,400 second threshold and nonnegative hysteresis no larger than the
threshold. Evaluation intervals are 5–60 seconds; the confirmation window must
cover the configured fire and clear counts. Product defaults, C# rule-kind
support and administration remain part of activation, rather than being exposed
before the verified commit path exists.

## Remaining activation work

1. Add the product-owned blocking rule kind/template, duration threshold and
   hysteresis settings, with explicit fire/clear confirmation semantics.
2. Wire the private capture/claim helpers into coordinator discovery and the
   normal evaluation queue dispatcher, retaining global claim budgets and
   target isolation alongside existing engine and backup work.
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

Four native PostgreSQL 18 queue checks verify SQL/C# canonical identities for
a 65.001-second observation, pending deduplication, exact claim recovery,
completed-source suppression after the real evaluation interval, and a distinct
complete-empty snapshot. They also exercise concurrent bounded producers,
changed/unknown evidence, schedule/target/rule revision changes, rule disable,
forged observation fields, wrong/expired owners, aged unknown captures,
inactive-target cancellation and capture cleanup, private RLS/grants, and explicit
catalog/engine-dispatch rejection. Evidence:
`artifacts/blocking-alerts/blocking-alert-queue.trx`.

The fixtures seed repository rows under their isolated bootstrap role to test
source integrity and corruption handling. The queue checks invoke private
helpers as the migrator and simulate a completed queue operation to test source
consumption; they do not exercise the normal worker or a state/outbox commit.
They do not run SQL Server collection or prove firing/delivery behavior. Those checks belong to the remaining
activation work above.
