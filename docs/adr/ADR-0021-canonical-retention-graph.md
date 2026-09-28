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

## Decision

Treat the **written relation and its dependent rows** as the retention unit.
Do not redirect a policy to a similarly named unused parent. Keep the
following canonical groups distinct:

| Evidence | Canonical relation or envelope | Retention relationship |
| --- | --- | --- |
| Raw metrics, M5 activity/waits/blocking, M9 and M10 snapshots, SQL volume, Query Store group deltas | Their actively written partitioned parents in `telemetry`, `events`, and `analytics` | Continue the fenced partition policy and registry path. |
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

For **original M7 observations**, build a partitioned successor keyed by UTC
`interval_end`; its primary key must include that partition key. Preserve
`instance_id` and the `(instance_id, interval_end)` access path. Cut over the
fenced writer and target-scoped readers together after dual-write and
backfill/replay verification. Keep run/query/plan and content-link rows until
all dependent observation partitions and retained incidents/reports are
accounted for. A partition drop must not silently cascade through those
foreign keys. Use a separate bounded cleanup for graph nodes proven orphaned.

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
