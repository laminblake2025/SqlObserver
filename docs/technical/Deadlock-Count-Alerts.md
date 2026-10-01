# Deadlock count alert evidence

The revamp plan calls for deadlock alert coverage beyond engine counters.
Forward migration 0303 implements its bounded repository source and adds it to
the Collector data plane. Migration 0304 adds private durable capture and claiming;
0305 and 0306 add independently verified transitions and atomic persistence;
0307 connects them to normal runtime discovery, claiming and dispatch. Catalog
activation and the administration UI are still required before a user can enable
this signal.

## Signal and source

The source counts unique deadlock event identities in the five minutes ending
at the latest complete, loss-free deadlock scan's PostgreSQL completion time.
The window is anchored to that scan, so repeated reads do not move its window or
digest. A complete scan with no events in that window supplies zero. Missing,
failed, incomplete, stale, mismatched or inconsistent evidence supplies unknown.
No earlier successful scan can replace a newer failed scan.

Existing deadlock summary rows retain their first collection run because events
are deduplicated across scans. They cannot prove a later scan's exact contents,
particularly after a target revision change. The canonical deadlock commit now
also retains `alerting.deadlock_source_snapshot`: one private row per target,
with at most 256 safe event identities, occurrence timestamps and parse flags.
It binds them to the target/schedule revision, run, completion time and canonical
completion digest. It contains no raw XML, query text or provider connection data.

The wrapper delegates existing validation, deduplication, accounting and replay
to the original canonical function. It writes the snapshot only after a new
commit, in the same transaction. Exact replay never overwrites a newer snapshot.
A snapshot-write failure or lost final worker fence rolls back the canonical
outcome, evidence and snapshot together. The core function is private; only the
reviewed wrapper remains executable by the Collector.

The table has forced RLS with a migrator-only policy and no direct Collector,
Server, Auditor or PUBLIC privileges. Its target/run identity foreign keys
cascade cleanup; its size stays bounded by target count rather than scan count.
No historical snapshots are backfilled from deduplicated summary rows. Targets
need a fresh canonical scan after upgrading.

## Reader boundary

`alerting.read_deadlock_alert_evidence` and `IDeadlockAlertEvidencePort` require
the exact active target revision, target scope and live
`alerts/evaluation/reconcile` lease. The function holds target and schedule share
locks and checks ownership again before returning. Only the Collector can execute
it. It uses the routed collector-health projection, so original and partitioned
run readers remain supported.

Evidence expires after twice the collection interval, capped at two minutes.
Known evidence requires matching canonical run/snapshot identity and digest,
complete accounting, unique identities, valid occurrence times and no truncated
parses. Occurrences outside the five-minute window remain retained evidence but
do not increase the count. Future events or events outside the source's existing
33-day bound make the evidence inconsistent. The real empty-scan sentinel is
accepted without counting it as an event.

The C# port bounds calls to five seconds and the response to 2 KiB, checks SHA-256
against the exact JSON bytes, rejects unknown or duplicate fields, validates UTC
window/freshness/count bounds, and checks the returned target binding.

## Durable capture and claiming

The private queue binds each captured source to its target and rule revisions,
canonical operation and evidence identities, and the exact evaluation worker
lease. It permits one pending capture per rule. Reconciliation replaces expired
or superseded work only after its claim expires; it never takes work from a live
claimant. Completed queue metadata prevents a repeatedly read scan from supplying
another fire or clear confirmation. Capture bodies are cleaned up separately.

Known observations carry integer counts from zero through 256; unknown observations
carry no count. Count policies require integer thresholds and hysteresis, evaluation
intervals from 15 to 60 seconds, and a window that covers the requested confirmation
intervals. The new kind and wire contract are not approved by the product catalog.
All four queue helpers remain migrator-only, with a forced-RLS private capture
table. Migration 0304 initially gated public claiming/evaluation. Migration 0307
activates both through the normal fenced pipeline; the product catalog still
does not permit user creation of deadlock rules.

Three native checks cover SQL/C# identity agreement, exact reclaim, consumed-source
suppression, concurrent reconciliation, source and rule supersession, live-claim
preservation, disabled-rule cleanup, forged observations, wrong lease owners,
count bounds, private privileges, and the original public activation gates. With
0307 applied, the queue check now verifies public reclaim and rejection of an
incomplete decision batch. The consumed
source check marks its queue row completed with a bootstrap-only fixture mutation;
it does not exercise a production alert-state or notification commit. Four shared
dispatcher regressions still pass for actual engine, backup and blocking work.
Three unit checks cover zero versus unknown, forged count/source identities and
integer policy bounds; the migration preamble check also passes.

## Verified transition and atomic persistence

The C# evaluator validates the deadlock source contract before processing it.
Unknown evidence interrupts both confirmation streaks, preserves firing and
acknowledged episodes, and cannot resolve or allocate an alert. Known counts use
the configured integer threshold and hysteresis. A gap beyond the confirmation
window restarts confirmation. Replay is read-only; regressed observation time is
rejected. The independently derived private PostgreSQL transition agrees with
C# on 1,680 combinations across all states, null/zero/count boundaries, both
comparisons, hysteresis, clear counts, maintenance and gaps before, exactly at,
and beyond the window boundary. Forged counts and source bindings are rejected.

The private commit requires the exact captured observation, live claim and worker
lease, target/rule revisions, and repository-derived decision. State, history,
replay, approved notification intent, audit, queue consumption and capture cleanup
share one transaction. It rechecks source, claim and lease after durable writes.
Exact committed replay requires the same rule/target binding and canonical result;
it performs no writes and does not need the deleted capture body. Changed replay
or a changed target revision is rejected.

Three new native commit checks cover seven canonical typed collection scans at the
actual 15-second evaluation cadence, Server-role acknowledgement, unknown evidence
between clear samples, recovery, re-fire, maintenance deferral and immutable replay.
They also reject forged state/event decisions and prove rollback after injected
audit failure, lost worker ownership and expired queue claim. A successful retry
consumes the original capture once. Transition and commit functions remain
migrator-only; public runtime dispatch invokes them internally. The catalog is
still unapproved for deadlock rules.

These are isolated PostgreSQL checks. Typed collector payloads and bootstrap-only
rule/destination fixtures supply the test evidence; the seven scans do not come
from a live SQL Server XE workload. The checks persist notification intent but
do not run the normal alert worker or send a webhook. The C# regression first
failed because deadlock evidence was not accepted by the evaluator, then passed
after source-bound handling was added. Existing backup/blocking transition unit
checks also pass.

## Normal runtime integration

The Collector's existing evaluation worker uses the normal repository source,
coordinator lease and target grouping for deadlock work. Public discovery includes
due deadlock rules and expired captures needing cleanup. A private, forced-RLS
reconciliation cursor records each target check, so repeatedly consumed or
unchanged evidence cannot monopolize the first fleet page. Its target foreign key
cascades cleanup. Captures are reconciled under target scope before claiming;
deadlock work shares the existing global queue limit with engine, backup and
blocking work. Public claiming revalidates every deadlock capture and claim.

Public dispatch validates rule/source and batch bindings, derives deadlock
transitions independently, and commits them in the same transaction as the other
sources in a target batch. Forging a deadlock decision rolls back engine, backup,
blocking and deadlock effects together. Exact mixed-batch replay remains read-only.
The source helpers and atomic commit stay private; the Collector receives access
through the existing public functions.

Four new native checks run the actual background worker over a canonical typed
deadlock commit, dispatch a mixed four-source batch, preserve a revised rule's live
claim, and exercise 129-target paging without re-consuming unchanged evidence.
The worker creates one firing notification intent. The mixed batch deliberately
uses unknown deadlock evidence and confirms that it stays Normal while valid
engine/blocking evidence fires. The fleet fixtures likewise use unknown sources;
they qualify bounded discovery and claiming, not live SQL Server throughput.
Three queue checks and four existing blocking-runtime regressions also pass.
Rules and approved destination rows remain isolated bootstrap fixtures. No live
SQL Server deadlock workload or notification delivery is qualified by these checks.

## Verification and remaining work

Four native PostgreSQL checks use the full migration history and canonical
Collector begin/commit ports. They cover complete zero, recent versus old events,
duplicate scans, stable reads, both health-reader routes, exact old replay,
missing snapshots, disabled collection, partial output, future/truncated/stale
evidence, permission-denied collection, target revision changes, private table
and core-function privileges, wrong scopes/owners and released leases. Injected
snapshot failure proves full rollback, followed by one successful retry.

The source-reader fixtures seed only their target/prerequisite setup and typed collector
payloads. They do not run a SQL Server workload or the alert evaluation/delivery
workers. Bootstrap-only mutations simulate corruption and expired timestamps.
The existing deadlock commit, projection and retention regression group is also
checked. Three retention fixtures now choose an event 32 days old, inside the
33-day ingestion bound, so their partition is eligible even on the first day
of a month. Production retention behavior is unchanged.

The active original-run dependency inventory was reconciled with the existing
availability, backup, database inventory, configuration, capacity and metric
migrations. This records current dependencies; it does not remove them or prove
original-run retirement is ready.

Remaining work is approved catalog and UI settings, and live SQL Server-to-notification
qualification. The current XE reader still rescans its bounded 33-day file
window. Incremental bookmarking remains a separate collector refactor; its
coverage contract must preserve the five-minute history before it can replace
this full-scan source. Hitting the current row/byte/parse bounds supplies unknown,
which must not resolve an active alert.

Evidence is recorded under `artifacts/deadlock-alerts/` in
`deadlock-alert-commit-regressions.trx`,
`deadlock-alert-retention-and-inventory.trx`,
`deadlock-alert-reference-inventory.trx` and `deadlock-alert-preamble.trx`.
The first regression run passed 28 of 31 cases, including all four new checks;
its three retention date failures passed after fixture correction. The targeted
retention/inventory run passed those three and exposed the stale inventory list;
the separate final inventory run passed after reconciliation. Together they
verify 32 unique native cases, plus the passing migration preamble check. Earlier
failed result files are retained; the whole group was not rerun unnecessarily.
Queue-stage evidence is in `deadlock-alert-queue.trx` (three native checks),
`deadlock-alert-shared-dispatch-regressions.trx` (four native regressions) and
`deadlock-alert-contracts-and-preamble.trx` (three unit checks and one preamble
check) in the same directory.
Transition/persistence evidence is in `deadlock-alert-transition-before.trx`
(expected failing regression), `deadlock-alert-transition-unit.trx` (25 checks),
`deadlock-alert-transition-postgresql.trx` (two native checks, including the 1,680
comparison cases), `deadlock-alert-atomic-commit.trx` (two new native commit checks
and three existing deadlock regressions), `deadlock-alert-final-fences.trx` (one
native check), and `deadlock-alert-atomic-preamble.trx` (one preamble check).
Runtime evidence is in `deadlock-alert-runtime-and-regressions.trx`: nine of 11
cases passed, including the worker, mixed dispatch, queue and existing dispatcher
checks. Two new fleet fixtures failed before exercising the runtime, first on
outdated target column names, then on duplicate fixture rule names and inconsistent
fixture timestamps (`deadlock-alert-runtime-fleet-fixtures.trx`). Both pass after
correction in `deadlock-alert-runtime-fleet-corrected.trx`; together the runs verify
11 unique native cases. Earlier failed files remain available. The new migration
preamble check passes in `deadlock-alert-runtime-preamble.trx`.
