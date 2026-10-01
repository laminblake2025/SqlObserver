# Deadlock count alert evidence

The revamp plan calls for deadlock alert coverage beyond engine counters.
Forward migration 0303 implements its bounded repository source and adds it to
the Collector data plane. Migration 0304 adds private durable capture and claiming;
0305 and 0306 add independently verified transitions and atomic persistence;
0307 connects them to normal runtime discovery, claiming and dispatch. Migration
0308 activates the approved catalog, API and administration UI. Real SQL Server
2022 collection through signed firing/recovery delivery is qualified in the owned
Windows lab; deployment identity/network and SQL Server 2019/2025 remain separate.

Migration 0309 advances the pinned deadlock SQL bundle after live SQL Server 2022
reads exposed false empty scans. A direct `timestamp_utc` predicate returned zero
despite matching retained events; the equivalent residual `CASE` predicate read
both events under the same restricted login. The corrected 15/16/17 assets retain
the passive existing-file read, 33-day source window, latest-first ordering,
257-row sentinel, 256-event output and all byte/parse/file/time bounds. This is a
source-read correction; incremental bookmarking remains separate work.

Deploy the corrected Collector build and migration 0309 together. The guarded
upgrade accepts only the expected previous manifest/bundle/deadline, changes only
the bundle digest, and restores the append-only trigger transactionally. Existing
run digests, schedules, source schema and catalog contract 5 remain unchanged.

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
intervals. Migration 0304 reserved the kind before catalog activation; migration
0308 now approves it with independent native SQL validation.
All four queue helpers remain migrator-only, with a forced-RLS private capture
table. Migration 0304 initially gated public claiming/evaluation. Migration 0307
activates both through the normal fenced pipeline. Migration 0308 permits
Target Administrators to create deadlock rules through the approved catalog.

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
migrator-only; public runtime dispatch invokes them internally.

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

## Catalog, API and administration activation

Migration 0308 upgrades contract 4 to contract 5 only when the previous catalog
digest matches exactly. The new seven-rule catalog digest is
`b1e551855cd7731c59e024c8f35ae1375944664cd26b1d0f5b3f6dc3cfe1d1c7`.
Deploy the migration and matching Server/Collector binaries together. It does
not create or enable rules automatically. Missing or changed prior bindings roll
back the upgrade without replacing the administrative function.

The `deadlocks.count` template uses `deadlock_count_v1`, the passive
`deadlocks.system-health` schema 1 source, and count units. Defaults are `> 0`,
zero hysteresis, two fire and two clear observations, a five-minute confirmation
window and a 15-second evaluation interval. Custom comparison, integer threshold,
hysteresis and timing stay within the same domain, native SQL and web bounds.
Changing a rule's source kind or metric binding requires a new rule. Updates and
disable operations remain revision-fenced, audited and idempotent.

Target Administrators can select the template, inspect its five-minute window
meaning, and create/edit/disable rules. The editor uses whole-count fields capped
at 256, separate fire/clear counts and evaluation intervals from 15 to 60 seconds.
Rule details and alert evidence summaries name the count and recovery actions;
source digests are omitted from recognized summaries. Complete zero stays visible.
The role boundary is unchanged.

Thirteen focused native checks cover production rule administration, runtime
evaluation, revision/retag/disable fencing, forged count/source/timing rejection,
catalog upgrade rollback and existing backup/blocking administration. Seven unit
checks verify the catalog digest, defaults, bounds and preamble. Six HTTP checks
cover catalog authorization and source-kind preservation through create/list/
update/disable. Nine web checks cover count validation, zero/unknown summaries and
backup/blocking regression behavior. Two browser checks exercise deadlock and
blocking forms, identical retry payloads, revisions and disable operations, with
zero axe violations. The production web build and asset manifest verification
pass. The rendered deadlock editor was inspected.

## Live lab fixture

`LiveDeadlockCollectionCommitsPermissionGapsAndDeliversSignedFireAndRecovery`
is an explicitly selected Windows lab test. It requires Docker PostgreSQL 18 and
a SQL Server 2019/2022/2025 disposable endpoint with an existing `system_health`
event-file target. The lab caller needs permission to create/drop its random
probe login; actual collection impersonates that non-sysadmin login with only
the version-appropriate server-state permission. A random global temporary table
and two owned SQL sessions produce one real error 1205. Cleanup rolls back the
transactions, closes the sessions, and drops the table/login. The test never
creates, alters, flushes or deletes the existing XE session or its files.

The fixture records real capability discovery and reconciles the production
collector registry. Core, session, request, wait and blocking prerequisites run
against SQL Server; their successful outcomes are not seeded. Deadlock collection
uses the real passive reader, bounded parser, execution engine and canonical
PostgreSQL commit. The normal alert claim/evaluation ports supply confirmation
and persistence. The real delivery worker sends firing and recovery messages to
an owned HTTPS receiver, which verifies HMAC signatures and delivery identities.

Polling follows the real 15-second collector/evaluation schedule. Existing recent
lab events must first age out of the five-minute window. The measured SQL Server
2022 `system_health` dispatch interval is 120 seconds, so the source deadline is
three minutes. The initial 65-second deadline was too short. Both that attempt and
the longer retry failed to collect the event before the SQL predicate correction;
their retained evidence is `deadlock-alert-live-pipeline.trx` and
`deadlock-alert-live-dispatch-corrected.trx` under `artifacts/deadlock-alerts/`.
The corrected-source run passed all 12 bundle migration/catalog checks and reached
real recovery, then failed an incorrect fixture assertion that expected the clear
counter to remain at two after resolution. The evaluator resets it to zero;
the fixture now verifies the first pending clear and both distinct zero observations
instead. That run is retained as `deadlock-alert-live-and-bundle-upgrade.trx`.
The fixture requires two count observations to fire, revokes the probe's permission to
prove unknown preserves the episode, and waits for the actual five-minute window
to expire before two zero observations can recover. It renews the evaluation
lease; it does not backdate events or fast-forward repository time.

To run only this check against a local Windows lab:

```powershell
$env:SQLOBSERVER_VALIDATION_PROFILE = 'Local'
$env:SQLOBSERVER_LOCAL_SQLSERVER = 'Server=localhost\SQLEXPRESS;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --no-restore --filter 'FullyQualifiedName~LiveDeadlockCollectionCommitsPermissionGapsAndDeliversSignedFireAndRecovery' --logger 'trx;LogFileName=deadlock-alert-live-pipeline.trx' --results-directory artifacts/deadlock-alerts
```

The Local certificate bypass is restricted to the disposable SQL lab contract.
Webhook approval retains the production address allowlist/pinning checks; a test
socket seam routes the approved synthetic address to the owned loopback receiver,
with an exact certificate pin and ephemeral signing key. This does not qualify
deployment DNS, certificate chains, service identity/Kerberos or protected key
provisioning. The test carries `RequiresSqlServerDisposable` and is excluded from
ordinary validation profiles.

The corrected fixture passed in 5 minutes 38 seconds against SQL Server 2022
Express `16.0.1000.6` and isolated PostgreSQL 18 with the full migration history.
It verified the actual victim/participants, stable event identity on rescans,
pending/firing, permission-denied unknown without recovery, two complete zero
observations after real window expiry, matching alert/episode identities, and
exactly two distinct signed firing/recovery deliveries. Repeated scans retained
only one new canonical event row. Native post-run checks found zero probe logins
and temporary tables. Evidence: `artifacts/deadlock-alerts/deadlock-alert-live-final.trx`.

The 12 passing migration/catalog checks in `deadlock-alert-live-and-bundle-upgrade.trx`
verify migration 0309 preserves history, schedules and all other contract fields;
missing/drifted contracts roll back and retain the append-only trigger. Existing
application catalog prefixes reconcile with the corrected pinned bundle. Two
asset-boundary checks and the migration preamble check also passed, recorded in
`deadlock-file-filter-assets.trx` and `deadlock-file-filter-preamble.trx`.

## Incremental reader foundation (not active)

The review's XE bookmark refactor now has two domain boundaries, a pinned source
reader and private PostgreSQL checkpoint read/commit storage. They are staged; the production
Collector still uses the approved full-scan v1 source. Collector contracts,
the alert catalog and runtime activation remain unchanged.

`XeFileReadProgressCalculator` computes a resume point from metadata for **all**
event types, ordered by numeric file generation and buffer offset. A position
contains only generation, rollover index and offset; its identity binds the exact
target/revision and a digest of the trusted XE source incarnation. It contains no
provider path. The staged reader reconstructs the paired initial file/offset from
the current trusted target directory/prefix and these numeric tokens.

SQL Server resumes after the entire buffer named by `initial_offset`, and multiple
events can share that offset. See the
[Microsoft function contract](https://learn.microsoft.com/sql/relational-databases/system-functions/sys-fn-xe-file-target-read-file-transact-sql).
Consequently, a row/byte/deadline cutoff excludes its last, potentially partial
buffer from the safe bookmark. An earlier complete buffer can advance; retry
reads the partial buffer again in full. Only actual provider end-of-stream can
complete the last buffer. Replayed/regressed positions, interleaved buffers or
ambiguous file generations require reset. A changed source epoch or target
revision cannot reuse the old bookmark, and another target's bookmark is rejected.
The metadata budget is 8,192 rows plus a truncation probe, separate from the
existing 256-deadlock and byte/parse bounds.
Small response budgets reduce that metadata cap so the header and probe also fit.
A buffer larger than the bounded page cannot advance by itself; it remains
catching up with no new bookmark, rather than silently skipping its unread tail.

`XeFileSourceBundle` embeds checksum-approved SQL variants for majors 15, 16 and
17 in `collectors/xe-file-reader/`. Its bundle digest is
`4b00f8c40a3b0359fc1308b748e945f4df46ac0daae2b647f6c165478a951822`.
It accepts numeric positions and an expected source digest, never a provider path.
The source derives the directory/prefix from the existing bounded event-file
target, validates filenames and offsets, and checks the source again after reading.
A missing initial file (native error 25717) or changed source returns a reset.
Missing or invalid source configuration cannot produce a complete empty read.

The SQL first materializes at most 8,193 metadata rows without requesting XML.
Filename validation runs against that bounded page. If the page contains deadlocks,
a second native read requests only matching deadlock XML, with bounded rows,
512-KiB individual XML and a 1-MiB logical response budget. Both reads use the same
initial file/offset. Missing matches between those reads force reset. Unrelated
event contents never enter the result. Duplicate position/time keys use bounded
ordinals; a partial buffer still cannot advance. These writes use only connection
local temporary tables; they do not modify XE, application tables or workloads.

The source incarnation binds the trusted root, configuration caps, native session
address, SQL startup tick and session creation time. The native lab exposed up to
one datetime tick of jitter in `sys.dm_xe_sessions.create_time`. Warm reads retain
the original digest only when its creation-time anchor matches within 10 ms and
the other inputs are identical. Greater drift, a new address or a different boot
tick resets the source. The native address stays inside the hash and SQL batch.
See the [startup tick contract](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-os-sys-info-transact-sql)
and [XE session metadata](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-xe-sessions-transact-sql).

`XeFileSourceReader` validates the closed status and metadata result frames,
accounts for UTF-8 XML, and maps only safe typed deadlock evidence. Row, deadlock
and byte cutoffs preserve the previous complete buffer. Oversized, malformed or
future evidence marks explicit loss. No consumer may persist progress after such
loss without atomically preserving its coverage gap. Command timeout is 20 seconds;
caller cancellation remains active throughout SQL and parsing.

`DeadlockReadWindowCalculator` retains a bounded set of event IDs, occurrence times
and truncation flags across incremental reads. Quiet reads preserve recent events;
duplicates cannot inflate the count. Events exactly on the five-minute boundary
still count, and expire only after crossing it. Catchup can retain history but
supplies unknown until end-of-stream. A cold read must supply a proven UTC
retention boundary; a new source with less than five minutes of coverage remains
unknown until that window is covered. Source loss and parser truncation mark an
inclusive gap through the read time. Later quiet reads remain unknown while that
gap overlaps the five-minute window. The retained window is capped at 256;
overflow is explicit unknown, with a gap marker protecting against false recovery
after clipping. Future/conflicting evidence and regressed time are rejected.

Thirteen progress checks and eleven window checks pass. The read-only native
SQL Server 2022 Express check also passes: it finds a real flushed buffer with
multiple events, verifies that a two-row cutoff cannot advance its bookmark, and
confirms the actual native resume excludes every event in that buffer. The native
fixture uses existing bounded `system_health` metadata and never changes XE or
creates a workload. Evidence: `artifacts/deadlock-alerts/xe-bookmark-progress.trx`,
`xe-bookmark-window.trx` and `xe-bookmark-native-buffer.trx`.

Nineteen focused reader checks pass in `xe-source-boundaries-final.trx`. They cover
quiet reads, duplicates, metadata/deadlock/UTF-8 byte cutoffs, server loss sentinels,
malformed/oversized/future evidence, contradictory frames, unavailable sources,
revision/epoch mismatches, small-response metadata reservations and embedded checksum approval. Native catch-up, quiet
resume, missing-file reset, changed epoch and a two-row cutoff pass in
`xe-source-native-stable-epoch.trx` on SQL Server 2022 Express 16.0.1000.6.
The stronger reference comparison passes in `xe-source-native-reference.trx`:
37 bounded pages preserve the same four unique deadlock event identities as the
approved v1 full scan. It also covers native warm resume, changed source,
rolled-away file and the two-row cutoff. The separate native small-response
check is recorded in `xe-source-native-small-budget.trx`; it fits the header,
metadata and probe into the configured 4-KiB logical response budget.
Earlier failed native attempts remain recorded: they exposed the filename
allowlist, cold-read expression cost, computed DMV predicate filtering and native
creation-time jitter. This reader is not yet qualified on SQL Server 2019/2025.

Migration 0310 adds the private persistence foundation. It does not register or
activate a v2 collector. `control.deadlock_read_checkpoint` stores one bounded
row per target, containing only canonical run bindings, source digest/state,
numeric bookmark and retained window. A generated digest binds all those fields
for a future compare-and-swap commit. The table has forced RLS and no direct
application-role privileges. There is no writable Collector checkpoint API.

`control.read_deadlock_read_checkpoint` requires the exact target scope, active
target revision, v2 schedule, begun run, scheduled time, owner and fencing token.
It asserts the same target Collector lease before and after reading. Saved state
must bind to its canonical v2 run and outcome; reuse also requires matching
target/schedule revisions. The latest completion must have its matching state.
Events that could survive the next merge must exist in canonical deadlock
evidence. A current window must include every recent event from its source
snapshot, so neither an invented catch-up event nor a forged zero can become a
known count. Expired cache events do not require rows already removed by retention.
The returned envelope and stored checkpoint each carry a SHA-256 digest. Server
and Auditor roles cannot execute this read; the Collector cannot read or write
the table directly or execute its private merge helpers.

The private SQL window calculator reproduces the domain merge, including
inclusive expiry/gap boundaries, quiet history, duplicate identities, ordered
conflict detection, catch-up, source loss, parser truncation, bounded overflow,
revision changes and changed source epochs. It validates the previous window's
closed shape and coverage invariants. PostgreSQL-normalized UTC inputs avoid
inventing precision absent from repository timestamps.

Three isolated native PostgreSQL checks pass. Nineteen merge transitions match
the domain model, including expired duplicate identities. Invalid input checks
reject future/conflicting/regressed evidence, another target, missing cold
coverage and forged window reasons. Reader qualification uses bootstrap-only
v2 fixtures and canonical begin/commit ports; it checks empty state, restored
state and digests, forged zero/catch-up evidence, wrong scopes/runs/owners/fences,
released leases, corrupted completion binding and private object privileges.
Evidence is in `xe-checkpoint-foundation-final.trx`,
`xe-checkpoint-canonical-binding.trx` and `xe-checkpoint-retained-binding.trx`.
The new migration preamble check is `xe-checkpoint-preamble.trx`.

Migration 0311 stages `control.commit_deadlock_read_collection_run`, an owner-only
atomic producer. It accepts the canonical bounded graphs plus a closed source
proof: expected checkpoint digest, source state/epoch, UTC read/retention times,
all numeric metadata positions, end-of-stream and source-loss flags. The database
independently calculates the safe whole-buffer frontier, compares the predecessor,
and merges canonical event identities into the retained window. Metadata probes
cannot become a false end-of-stream. A partial buffer remains replayable; normal
catch-up does not claim permanent sample loss. Outages retain recent event history
without advancing the bookmark. Changed incarnations/regressions reset progress
and record a gap; target-clock skew cannot supply a current zero count.

Canonical events/outcome, source snapshot, checkpoint, coverage gaps and replay
receipt are written in one transaction, with the final target lease assertion
after all writes. Canonical run outcomes remain append-only. The separate private
`events.deadlock_read_receipt` contains only run/target bindings and SHA-256 hashes,
partitioned by canonical completion month and registered for retention inventory.
An exact replay validates both canonical completion and source proof, returns zero
new writes and preserves newer checkpoint state. Divergent proof or predecessor
is rejected. Existing receipt partitions require no fleet-wide DDL lock or
per-page schema mutation.

Focused native checks verify 13 whole-buffer comparisons against the domain model,
invalid positions, history across two pages and an outage, current versus unknown,
changed incarnation, quiet catch-up after a gap, clock skew, immutable receipts,
closed proof shape, compare-and-swap and divergent/old replay. A trigger invalidates
the lease after the checkpoint write: events, outcome, source snapshot, checkpoint,
receipt and partition registration all roll back. Canonical commit and fenced-read
regressions also pass. Evidence is `xe-checkpoint-atomic-native.trx` (the progress
check passed; two initial commit checks exposed append-only outcome protection),
`xe-checkpoint-atomic-receipts.trx` (both corrected commit checks),
`xe-checkpoint-atomic-final.trx` (three commit checks plus two regressions),
`xe-checkpoint-atomic-registry.trx` (registry allowlist preservation) and
`xe-checkpoint-atomic-preamble.trx`.

Migration 0312 connects receipt cleanup to the scheduled retention worker. Its
enabled policy defaults to 30 days and a floor of two monthly partitions; domain,
HTTP and PostgreSQL policy updates preserve those minima. Global administrative
authorization and same-origin mutation checks still apply. The API and web parser
accept the new `m6_deadlock_read_receipts` policy and `checkpoint_active` reason.
Manual detach cannot bypass its scheduled grace route.

An eligible month stays attached and readable for 24 hours before one fenced
transaction detaches and drops it. Policy changes cancel pending grace without
hiding data. The worker rechecks current policy, recovery attestation, partition
identity, reader leases, backfills, external foreign-key dependencies and latest
checkpoints before DDL. A latest checkpoint holds its own receipt month, including
an offline target's bookmark; other months can proceed. Analytics jobs do not
consume replay receipts, so they do not create a blanket fleet hold. Parent and
child locks use NOWAIT to leave in-flight SQL replay reads intact. Drop failures
roll back detach, remain readable and use the existing bounded retry ledger. The
final lease assertion follows DDL, registry/execution changes and audit writes.

Exact replay needs its retained receipt. After a month is retired, old run outcomes
remain unchanged, but that month's source-proof replay is unavailable; callers
cannot recreate or silently accept a missing receipt. The latest checkpoint's
month remains held until the checkpoint advances. Receipt retention does not
activate the staged v2 collector or change the active v1 deadlock alert source.

Two native retention checks cover readable grace, policy cancellation, explicit
and physical reader holds, backfill holds, scoped latest-checkpoint protection,
unrelated analytics work, recovery rechecks, retry after a dependent view blocks
drop, invalid policy bounds, private privileges, blocked manual detach and rollback
after a lease is invalidated by the final audit write. A real PostgreSQL dump is
restored before attestation and after receipt deletion; the original two receipts'
complete identities and digests are recovered. Old receipts are bootstrap-only
retention fixtures; canonical incremental producer qualification remains the
separate migration-0311 checks. Four regressions pass for backup, manual TempDB and SQL-baseline
retention and incremental deadlock replay. Four HTTP cases cover the new policy's
valid request, replay minima and origin protection. Two web behavior checks cover
policy/hold parsing, policy PUT and rejection of manual cleanup before fetch;
TypeScript checking also passes. Evidence is `xe-receipt-retention-final.trx`,
`xe-receipt-retention-restore-identity.trx`,
`xe-receipt-retention-regressions.trx`, `xe-receipt-retention-http.trx` and
`xe-receipt-retention-preamble.trx`. The initial passing native run is retained in
`xe-receipt-retention-first.trx`.

Migration 0313 grants Collector access to the atomic source-proof commit and
restricts the legacy commit to v1/schema 1. The typed `IDeadlockReadRepositoryPort`
reads the fenced checkpoint and publishes the bounded proof inside an explicit
transaction with a transaction-local target scope. The private cache, receipt
tables and merge helpers remain inaccessible to application roles. Collector
composition aliases the port to the existing runtime instance; no scheduler or
collector v2 activation is installed.

The closed checkpoint codec verifies SHA-256 over the exact PostgreSQL JSON bytes,
including the saved checkpoint body. It rejects duplicate, missing or unknown
fields, conflicting target/run/schedule bindings, inconsistent resume metadata,
noncanonical identities and non-UTC timestamps. Proof timestamps and the canonical
v2 event vector use PostgreSQL microsecond precision. The proof snapshots numeric
positions and carries no caller-computed count or window.

The pinned source page now exposes an immutable snapshot of its numeric witnesses.
Byte accounting includes the metadata probe, and XML admission reserves room for
a subsequent witness. A byte stop retains only accounted positions and leaves the
last observed buffer incomplete. A reset discards all resumable positions and
accepted events.

Focused Collector-role PostgreSQL tests cover cold reads, catch-up, quiet reads
retaining a recent deadlock, old replay preserving the latest checkpoint, stale
CAS rollback, legacy-v2 bypass rejection, exact timestamp precision and outage
state retaining evidence while its count becomes unknown. Corrupted closed frames
are rejected even when their outer digest is recomputed. The separate fence test
checks complete rollback after the final checkpoint write. Existing v1 canonical
commit behavior remains covered.

Evidence for this typed boundary is in `xe-typed-checkpoint-native.trx` (three
native checks), `xe-typed-checkpoint-final.trx` (expanded typed flow and existing
v1 regression), `xe-typed-domain-contracts.trx` (16 domain checks),
`xe-typed-reader-accounting.trx` (19 reader checks),
`xe-typed-reader-probe-and-preamble.trx` (XML budget and migration convention),
and `xe-typed-reader-native-budget.trx` (SQL Server 2022, 4 KiB page). The Collector
project builds without warnings. These are focused checks; no fleet-wide or
remote deployment validation was rerun.

Activation still requires collector context and explicit v2 routing through this
port. Cold catch-up scheduling must stay bounded and fair. The alert evidence
reader must use the retained window rather than counting only the newest delta,
and preserve the verified five-minute firing/unknown/recovery behavior in native
end-to-end tests before v2 replaces v1. No active v2 catalog contract is installed.

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

Remaining work includes SQL Server 2019/2025 and deployment-specific identity,
network and certificate qualification. The current XE reader still rescans its bounded 33-day file
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
Catalog evidence is in `deadlock-alert-catalog-postgresql.trx` (13 native checks),
`deadlock-alert-catalog-unit.trx` (seven checks) and `deadlock-alert-catalog-http.trx`
(six checks). Browser results are in `artifacts/web-browser/results.json`; the
inspected editor image is `artifacts/deadlock-alerts/deadlock-rule-editor.png`.
Web behavior checks are `deadlock-alert-rule-admin.test.mjs`,
`blocking-alert-rule-admin.test.mjs` and `backup-alert-rule-admin.test.mjs`.
