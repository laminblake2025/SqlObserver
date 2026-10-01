# Deadlock count alert evidence

The revamp plan calls for deadlock alert coverage beyond engine counters.
Forward migration 0303 implements its bounded repository source and adds it to
the Collector data plane. Rule capture, evaluation, catalog activation and the
administration UI are still required before a user can enable this signal.

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

## Verification and remaining work

Four native PostgreSQL checks use the full migration history and canonical
Collector begin/commit ports. They cover complete zero, recent versus old events,
duplicate scans, stable reads, both health-reader routes, exact old replay,
missing snapshots, disabled collection, partial output, future/truncated/stale
evidence, permission-denied collection, target revision changes, private table
and core-function privileges, wrong scopes/owners and released leases. Injected
snapshot failure proves full rollback, followed by one successful retry.

The fixtures seed only their target/prerequisite setup and typed collector
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

Remaining work is durable capture/claim/transition/commit, runtime discovery and
dispatch, approved catalog and UI settings, and live SQL Server-to-notification
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
