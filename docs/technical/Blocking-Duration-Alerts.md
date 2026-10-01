# Blocking duration alerts

The revamp plan requires alert coverage beyond engine counters and backups.
Blocking duration is the next signal being implemented. The source contract
below is implemented and verified in PostgreSQL. Private durable capture,
claim, transition and atomic state/outbox commit helpers are implemented;
runtime queue dispatch is implemented and product catalog activation remains pending. No blocking rule
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
normal worker now reaches these helpers through the scoped Collector function
surface. The commit dispatcher routes blocking decisions through the verified
commit helper, and rejects malformed decisions and attempts to bind a blocking
rule to engine evidence.

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
cover the configured fire and clear counts. C# now defines the same rule kind,
wire name, source observation contract and factory. Product defaults and
administration remain part of activation.

## Independently verified transitions and atomic commit

Forward migration 0299 adds the private `alerting.blocking_alert_transition`.
It validates the closed observation/state shapes, canonical identities/digests,
duration units, source contract and increasing observation time. PostgreSQL
derives the next state independently of the caller. Greater-than and
greater-than-or-equal comparisons apply hysteresis only to active episodes;
fire and clear counts remain separate. A confirmation-window gap restarts a
clear streak, and an expired pending-fire window starts a new fire streak.
Unknown evidence resets both streaks and pending state while preserving an
active, acknowledged or resolved episode and its existing identities.

Forward migration 0300 adds the private `alerting.commit_blocking_alert_evaluation`.
It locks target, rule and state, verifies the exact current claim/source, derives
the expected decision from repository prior state and maintenance, and rejects
any differing caller decision. State, history, immutable replay, approved
destination outbox intents, evaluation audit and queue completion share one
transaction. A second source/claim check after writes prevents consuming evidence
that expired during the transaction. Another completed operation with the same
source/rule revision cannot count again. Capture bodies are removed on commit.

Exact replay returns zero changes without consulting expired/changed source
data or writing new audit/history/outbox rows. It still requires a live lease,
current active target revision and the unchanged rule binding; divergent replay
is rejected. State/decision serialization uses the existing production serializer
and canonical SQL snapshot codec. The commit reuses the existing generic
state-snapshot helper (currently named `backup_alert_state_snapshot`) without
changing backup behavior. Both SQL functions remain migrator-only for direct
execution; the scoped Collector dispatcher invokes them. The catalog remains gated.

## Runtime dispatch

Migration 0301 integrates blocking target discovery, reconciliation, claiming and
commit into the existing Collector repository surface. The background worker's
production source, rule/state reads, evaluator and persistence path accept the
blocking contract. The repository retains its global 100-operation claim bound
and groups persistence by target. Blocking claims verify the exact current
capture after the shared claimant has assigned owner/fencing metadata.

Mixed engine, backup and blocking decisions share the request transaction.
Blocking rules cannot masquerade as engine sources; their decisions always pass
through the independent verifier. A rejected blocking decision rolls back the
engine/backup work already staged in that transaction. Exact replay preserves
the existing independent ledgers and introduces no new effects.

A private, forced-RLS cursor records the last reconciliation check per blocking
target. It advances even when the current snapshot was already consumed, so
unchanged evidence on the first discovery page cannot starve later targets.
Eligible checks are ordered by their due time and capped by the discovery limit.
Available captures for disabled rules or inactive targets remain discoverable
for cleanup. The shared claimant now cancels revised operations only when their
claims are available; an unexpired live claim keeps its ownership and is rejected
by revision verification if someone attempts to commit it.

## Remaining activation work

1. Add the product-owned catalog template/defaults and expose duration threshold,
   hysteresis and fire/clear settings through administration.
2. Qualify additional source/revision/lease and acknowledgement races at runtime dispatch,
   then activate the catalog. Expose the template/settings in Rules and describe
   source gaps clearly in the alert inbox.

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
They do not run SQL Server collection or prove the normal worker or notification
transport. Those checks belong to the remaining activation work above.

Three native PostgreSQL commit checks exercise the full migration history and
the production decision serializer. One compares PostgreSQL with the C# evaluator
across 1,120 combinations: all five states, unknown/zero/fractional duration and
threshold/hysteresis boundary values, both upper comparisons, clear counts,
confirmation-window gaps and maintenance. Another commits seven real decisions
through the private queue/commit helpers: fire, unknown hold, partial recovery,
unknown interruption, recovery and refire. It uses the existing Server-only
acknowledgement function, preserves the acknowledged episode during unknown
evidence, allocates a new episode on refire, and checks seven immutable replay
rows, eight history rows including acknowledgement, three deferred notification
intents and seven evaluation audit rows. During maintenance the existing
acknowledgement function omits its notification; the blocking fire/resolve
intents wait for the window to end. The flow also checks unchanged replay after
source changes, divergent replay and target-revision fencing.

The third commit check rejects forged state/event/suppression fields and injects
an audit-write failure after state/history/replay/outbox writes. The entire
transaction rolls back and retains the original claim; retry then commits once
and exact replay changes nothing. The private commit function's owner/grants are
checked. Evidence: `artifacts/blocking-alerts/blocking-alert-commit.trx`.
The focused C# blocking, existing backup and engine evaluator checks passed
(34 cases): `artifacts/blocking-alerts/blocking-alert-transitions.trx`.

These commit fixtures seed collector outcomes/edges and a synthetic approved
destination in isolated PostgreSQL. They prove durable state and outbox intent
creation through the private helpers, without running the normal Collector
worker, collecting from SQL Server or sending an external notification.

Eight native PostgreSQL runtime/queue checks use the full migration history and
restricted Collector repository. They verify mixed engine/backup/blocking batch
rollback and replay, shared fleet claim budgets, live revised claims, source
fences and the existing private queue boundaries. A 129-target fixture claims
100 then 29 unique targets and subsequently checks every consumed target beyond
the first page without creating repeated confirmation work. The production
`AlertEvaluationWorker` is also started with its real source/repository/lease
services and persists a firing transition plus one outbox intent. Evidence:
`artifacts/blocking-alerts/blocking-alert-runtime.trx`.
These fixtures still seed collector data as the isolated bootstrap owner; they
do not run SQL Server collection or an external delivery adapter.
