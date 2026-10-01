# Blocking duration alerts

The revamp plan requires alert coverage beyond engine counters and backups.
Blocking duration now has an approved product template and administration UI.
The source contract below is implemented and verified in PostgreSQL. Private
durable capture, claim, transition and atomic state/outbox commit helpers feed
the normal evaluation worker. A live SQL Server 2022/PostgreSQL 18 check now
covers collection, firing, permission gaps, recovery and signed HTTPS delivery.
Catalog activation does not automatically create or enable target rules.

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
catalog gate originally remained at contract 3; migration 0302 activates contract 4. The
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
administration are activated by migration 0302 and the matching catalog/UI.

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
execution; the scoped Collector dispatcher invokes them.

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

## Catalog and administration activation

Migration 0302 upgrades the product catalog to contract 4, digest
`2acfcab767941fa6a40dff240314662456ff9908d4422eb83afe8ede865b8f0b`,
only when the previous contract/digest match exactly. The migration and matching
application/catalog must deploy together. It does not create rules automatically.
The new `blocking.duration` template uses `blocking_duration_v1`, seconds and the
passive `blocking.current` schema 1 collector. Defaults are `>= 60` seconds,
10 seconds of hysteresis, two fire and two clear observations within five minutes,
and a 15-second evaluation interval. Recovery therefore requires values below
50 seconds, confirmed by two distinct complete source snapshots.

Target Administrators can create, edit and disable the rule through Rules and
the existing HTTP endpoints. Threshold, hysteresis, comparison, fire/clear counts
and timing are configurable within the same domain/repository bounds. Its signal
kind and metric binding cannot change after creation; changing the signal requires
a new rule. Native SQL validates the source binding independently of C#.
The Rules forms and details name seconds explicitly. The alert inbox turns the
closed blocking source reasons into evidence-gap explanations and recovery actions.
Missing, stale and incomplete evidence keeps an active alert open.

## Remaining qualification

SQL Server 2019/2025 qualification and further concurrent stress remain open.
The live check below invokes the collection engine and fenced repository ports
directly; it does not qualify the complete service startup/scheduler loop or a
deployed service identity. Production certificate trust, DNS and protected-key
provisioning remain separate from its owned lab transport. The test VM is
available for further qualification when current source can be deployed there.

## Live collection and delivery verification

`LiveBlockingCollectionCommitsPermissionGapsAndDeliversSignedFireAndRecovery`
passed on Windows with SQL Server 2022 Express and an isolated PostgreSQL 18
database containing the full migration history. It discovers actual capabilities,
reconciles the production collector catalog and runs the real core, sessions,
requests and waits prerequisites. It uses a random non-sysadmin login through
`EXECUTE AS LOGIN`, with the version-specific server-state permission. Its real
blocking collector output is validated and persisted by the collection engine
and canonical begin/commit ports; no collection runs or edges are seeded.

The test holds a row lock on an owned global temporary table. Two distinct
complete samples fire a one-second custom rule. Revoking the collector's
permission produces a committed permission-denied run and unknown evidence
without resolving the episode or sending another notification. After permission
is restored and the lock is released, two complete empty snapshots resolve the
same alert. The production delivery worker completes two outbox rows, and the
receiver verifies the HMAC signature, timestamp, distinct delivery identities,
firing/recovery events and matching target/alert identities. The destination is
created and approved through the application administration service.

The HTTPS receiver owns an untrusted self-signed certificate and an ephemeral
signing key. Production address/allowlist/pinning checks run with a synthetic DNS
answer; a test-only socket adapter routes that exact address to the owned
loopback receiver. Certificate validation is pinned to its exact certificate.
SQL certificate validation is bypassed only under the Local lab contract.
These seams do not establish production network, Kerberos, TLS or DPAPI readiness.
The fixture cleans up its SQL sessions, temporary table and random login; native
post-run catalog checks confirmed no probe logins or tables remained.

This disposable test is explicitly selected and excluded from ordinary
validation sweeps and PostgreSQL-only CI. Run it on a Windows lab with Docker,
an accessible SQL Server 2019/2022/2025 instance and sysadmin fixture setup rights:

```powershell
$env:SQLOBSERVER_VALIDATION_PROFILE = 'Local'
$env:SQLOBSERVER_LOCAL_SQLSERVER = 'Server=localhost\SQLEXPRESS;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --no-restore --filter 'FullyQualifiedName~LiveBlockingCollectionCommitsPermissionGapsAndDeliversSignedFireAndRecovery' --logger 'trx;LogFileName=blocking-alert-live-pipeline.trx' --results-directory artifacts/blocking-alerts
```

Evidence: `artifacts/blocking-alerts/blocking-alert-live-pipeline.trx` (one passed
live check, approximately 34 seconds). The fixture uses the shared SQL lab
connection contract and refuses the Release profile.

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
out-of-contract administration rejection. Evidence:
`artifacts/blocking-alerts/blocking-alert-queue.trx`.

The fixtures seed repository rows under their isolated bootstrap role to test
source integrity and corruption handling. The queue checks invoke private
helpers as the migrator and simulate a completed queue operation to test source
consumption; they do not exercise the normal worker or a state/outbox commit.
They do not run SQL Server collection or prove the normal worker or notification
transport. Later runtime checks cover the evaluation worker; the live check above
adds SQL Server collection and notification-receiver coverage.

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

Eight native PostgreSQL catalog/fencing checks validate the restricted Server
administration port, idempotent creation, editable duration settings and disable,
unchanged backup administration, rejected source/metric/comparison/timing settings,
and rollback when the prior catalog is missing or has drifted. Runtime checks
cover acknowledgement between evaluation and commit, stale source evidence,
changed target revision and forged lease ownership. Failed commits leave the
claim and durable effects unchanged; recomputation preserves acknowledgement.
Private helper grants remain closed. Evidence:
`artifacts/blocking-alerts/blocking-alert-catalog-postgresql.trx` and
`artifacts/blocking-alerts/blocking-alert-catalog-fences.trx`.

Two focused catalog unit checks, five HTTP pipeline checks, twelve browser
parser/action checks and one Chromium create/retry/edit/disable flow passed.
The browser flow verifies seconds labels, confirmation validation, stable retry
identities, revision fences and axe accessibility. It uses synthetic HTTP
responses and is separate from the native repository tests. A production web
build and the migration preamble check also passed. Evidence:
`artifacts/blocking-alerts/blocking-alert-catalog.trx`,
`artifacts/blocking-alerts/blocking-alert-catalog-http.trx`,
`artifacts/blocking-alerts/browser-results.json` and
`artifacts/blocking-alerts/blocking-alert-catalog-preamble.trx`.
