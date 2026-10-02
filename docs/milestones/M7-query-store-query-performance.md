# M7 â€” Query Store and query performance

M7 (`queries.performance`, collector order 9) is a passive SQL Server 15/16/17
Windows slice. It reads Query Store per accessible online user database and
labels `READ_WRITE`/`READ_ONLY` evidence. SQL Server 15 uses the database
`VIEW DATABASE STATE` permission; SQL Server 16/17 use `VIEW DATABASE
PERFORMANCE STATE`. A successful zero-row Query Store read is valid evidence.

Disabled, unsupported, denied, bounded read failure, and timeout states may use
one bounded plan-cache read when the corresponding server permission is present
(`VIEW SERVER STATE` on 15, `VIEW SERVER PERFORMANCE STATE` on 16/17). A
cancellation or lease-fence loss is never converted into fallback. Plan-cache
metrics are explicitly cumulative/baseline/delta/reset and are not equivalent
to Query Store intervals.

The immutable assets cap candidates at 2,000 per database/run, observations at
20,000, plans at 50 per query, response bytes at 8 MiB, and windows at seven
days (default 24 hours). SQL projects only opaque SHA-256 query/plan identities
and typed nullable metrics: raw SQL text, plan XML, handles, labels, and
provider errors are not selected or persisted. The production sensitive-content
capability is fail-closed and currently unavailable; content-bearing endpoints
therefore return unavailable and the web panel remains metadata-only.

PostgreSQL migration `0011_query_performance.sql` adds append-only, fenced-run
normalized run/query/plan/observation tables with UTC checks, RLS, least
privilege grants, and deterministic opaque-key projections. Runtime PostgreSQL
and SQL Server integration environments are optional gates; Release compilation,
static asset checksums, focused tests, and web type/build checks remain required.

## Plan-cache delta refactor â€” comparison foundation

As of 2026-10-01 the production fallback still emits `plan_cache_cumulative`.
The existing semantics enum does not establish that a delta collector is wired.
`PlanCacheRuntimeWatermarkCalculator` now defines the comparison needed for that
cutover; no source query, writer, catalog pin, historical row or API semantics
changes in this foundation checkpoint.

A watermark identifies one cached statement on one target/revision and database
incarnation, with an opaque query/plan identity, server source epoch and cache-entry
fingerprint. Raw SQL, provider handles and SQL-local compile timestamps are not
fields. The later source mapper must qualify all three incarnation digests from
the approved source and inventory; arbitrary hashes or inferred database names
cannot establish an incarnation. A cache-entry identity must include the exact
statement offsets, handles, compile marker and recompile generation. A changed
database, source epoch, plan, entry or target revision starts a new baseline;
a decreasing target revision is stale. Comparing another target or logical query
is rejected, including on incomplete reads.

The first complete read records a baseline with a null delta. Comparable reads
publish only the six counter differences and their actual previous/current UTC
observation window; unchanged counters produce known zeroes. Any falling counter
restarts all six fields, without clamping a negative difference. CPU and duration
remain in source microseconds until after subtraction, then convert to the existing
millisecond metric shape. An incomplete or non-increasing read advances no
watermark and emits no interval. Here completeness refers to the selected
statement's identity and full counter tuple, independently of whether the
bounded sample covers the whole cache. A gap longer than the existing seven-day query
observation limit establishes a fresh unknown baseline rather than publishing an
invalid interval. Eviction or omission is not evidence of zero activity.

This follows the documented cache statistics: execution count and time totals
accumulate from compilation; generation distinguishes recompiles, while handles
and statement offsets identify cache statements. SQL compile/execution times are
local-time values, and the counters describe completed executions rather than
in-flight work. These properties must be verified by the native source before
enabling delta semantics. See Microsoft's
[query statistics reference](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-exec-query-stats-transact-sql?view=sql-server-ver17)
and [server startup counters](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-os-sys-info-transact-sql?view=sql-server-ver17).

Remaining implementation is the bounded pinned source, typed mapping, private
target-scoped watermark/retention tables, fenced atomic publication and replay,
versioned catalog rollout, and ranking/UI cutover. The source must materialize
its bounded candidates before expanding plan attributes, report lookahead and
missing identity coverage explicitly, and preserve per-statement identities.
Failure, unknown incarnation and incomplete statement tuples must leave their
watermarks unchanged. A whole-cache candidate cutoff reports limited coverage;
complete selected statements can advance their own qualified baselines, while
omitted statements remain untouched. The cap must never be presented as a complete
fleet ranking or turn missing entries into zero activity.
The writer must independently validate the active run/target revision, lease,
source scope and replay digest before comparing or committing counters. Historical
cumulative rows retain their meaning.

Necessary native proofs include same-entry quiet reads, new completed work,
owned-plan recompile/eviction, database recreation, restart identity, capped or
unresolved source rows, and clock/permission gaps. PostgreSQL proofs must cover
transaction rollback, scope, lease loss and exact versus divergent replay before
production activation. The current pure contract is not evidence for those gates.
The focused comparison suite is recorded under `artifacts/plan-cache-deltas/`;
it covers each falling counter, refilled new incarnations, precision, stale and
incomplete reads, query/target isolation, and interval/long-counter boundaries.

## Bounded native cache source â€” staged

`collectors/plan-cache-runtime/` now contains pinned SQL Server 15/16/17 source
variants. The approved bundle digest is
`c30d5b67f02984a8860ba288de4c75d5060ccf8a19ca13b2925320b4d2369a31`.
`PlanCacheRuntimeSourceBundle` verifies the index and each SQL resource before
execution. This source is not registered in the production collector yet.

The batch materializes at most 2,000 native statement rows plus one lookahead,
ordered by cumulative CPU, before expanding their plan attributes. It preserves
each statement's complete raw counters; it does not sum unrelated cache entries
into a watermark. The accounting header reports candidate, unresolved, invisible,
excluded and emitted rows, which must add up exactly. The decoder requires the
closed result shapes, bounded UTC capture window, unique statement identities,
declared row count and bounded output. Even an excluded lookahead marks whole-cache
coverage limited. Missing attributes or missing native query/plan identities
remain unresolved rather than presenting an empty successful source.

Query fingerprints retain the existing normalized cache-query identity, checked
against the actual legacy SQL during native qualification. Plan fingerprints now
represent the native normalized plan hash, independently of the cache allocation;
their later wire/schema cutover must preserve the meaning of old plan IDs.
The separate statement-incarnation digest includes database ID, length-prefixed
native handles, offsets, generation and the raw local compile marker. Startup
ticks and the raw startup marker supply an opaque source epoch. Local timestamps
are identity material only, with no UTC conversion or age claim. Neither raw
handles nor SQL text, plan XML or database names are returned. The batch uses
only cache/server metadata and does not query database GUID catalogs.

`PlanCacheRuntimeSourcePage.Exhaustive` describes visible cache candidates in this
sample. It cannot prove that evicted work is absent or that a selected query's
other entries were observed. The later writer can compare complete, qualified
selected statements while retaining limited aggregate coverage; omitted or
unresolved entries do not advance. Missing entries must never generate a zero.
The raw source page deliberately has no database-incarnation watermark binding.
A live qualified database incarnation must bracket the counter capture, bound
to the same target/revision and database ID. A stale inventory timestamp or
arbitrary hash is insufficient, especially across database drop/restore/recreation.

Thirteen source decoder/asset cases pass in `plan-cache-source-protocol.trx`.
The final bundle load and native source case pass in
`plan-cache-source-approved-native.trx`, together proving 14 unique source checks.
The native case runs on SQL Server 2022 Express `16.0.1000.6` with a disposable
database and temporary non-sysadmin login, granted server performance-state and
database visibility reads. Two stored-procedure statements match direct native
counter reads; quiet reads yield zero differences and two executions yield only
the increment. Both recompilation and eviction of the exact owned procedure's
plan produce new identities despite counters refilling above the old values.
Permission revocation raises a classified permission error, and a one-candidate
read reports limited coverage. The first eviction fixture incorrectly assumed
a fixed 64-byte handle; its actual 44-byte handle established the need for bounded
variable-length validation and explicit hash-field length prefixes. Earlier
result files remain in the artifact directory. Post-run native inventory found
zero probe databases and logins.

This is source and comparison evidence, not production pipeline certification.
The local contract uses its explicit local-only certificate bypass and does not
qualify deployment TLS/Kerberos. SQL Server 2019/2025 execution, controlled restart
and database-replacement races, production incarnation orchestration, lease-fenced atomic
storage/replay/retention, and the catalog/API/UI cutover remain required.

## Live database identity binding â€” staged

`PlanCacheDatabaseIdentitySource` now reads a closed seven-field identity frame
inside an already-open user database, without changing database context. Its
separate pinned SQL digest is
`59f718e9c8725b4b42a2729c1ce532cbc3e26c21138c39c172933637a02bc349`.
The identity hashes the native database GUID, backup family and current recovery
fork. Missing metadata remains unknown. The frame preserves bounded UTC capture
times, target/revision, database ID, catalog eligibility and the same startup
epoch as the counter source; no raw GUID or database name enters the watermark.

`PlanCacheRuntimeSourceAssembler` accepts only matching eligible identities read
before and after the cache capture, within a 30-second overall qualification
window. It rejects foreign target/revision evidence, duplicate IDs, invalid
windows and inventories over 256 databases. A changed epoch or incarnation,
missing identity, overlapping capture or expired bracket discards only that
database's selected statements. Other qualified statements can advance even
when whole-cache coverage is limited. Each accepted statement retains both
identity snapshots alongside its raw counter watermark, and missing statements
are never synthesized. The original source accounting remains intact.

Thirty-two focused frame and binding cases pass in
`plan-cache-identity-qualification.trx`. The real SQL Server 2022 case in
`plan-cache-identity-native-background-close.trx` uses a disposable database and
temporary non-sysadmin login with server performance-state, database visibility
and a user in that database. It reads actual bracketing frames on the same open
database connection, drops and recreates the database, verifies reuse of its
numeric database ID with a different incarnation, and rejects old-to-new
comparison despite the new counters exceeding the old values. A separate
AUTO_CLOSE database stays closed throughout the identity and cache reads; the
fixture waits for its asynchronous background close before asserting that
precondition. A master context is explicitly rejected. The first native result
records the fixture's immediate-close assumption; the corrected case passes.

The restricted source must hold qualified database contexts across its future
target sweep and account for inaccessible or omitted databases. This staged
reader/mapper is not registered in production and does not yet provide that
coordinator, persistent lease-fenced watermarks, atomic delta publication,
replay/retention, or versioned catalog/ranking cutover. Restore/fork changes,
forced disconnect/restart races, SQL Server 2019/2025, and deployment TLS/Kerberos
still require native qualification. The scoped SQL Server 2022 passivity evidence
does not qualify catalog behavior on every supported version.

Native identity semantics and catalog side effects are documented in
[sys.database_recovery_status](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-recovery-status-transact-sql?view=sql-server-ver17).
The AUTO_CLOSE fixture uses the closed-database metadata behavior of
[sys.databases](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-databases-transact-sql?view=sql-server-ver17)
and the background shutdown behavior of
[ALTER DATABASE SET options](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-database-transact-sql-set-options?view=sql-server-ver17).

## Private PostgreSQL cache storage and comparison â€” staged

Forward migrations 0316 and 0317 add separate `plan_cache_runtime_watermark`,
`plan_cache_runtime_delta` and `plan_cache_runtime_receipt` parents. Daily baseline
buckets retain the latest counters for each target revision, database, normalized
query and cache entry. Plan identity, database incarnation and startup epoch are
comparison fields, so a changed incarnation starts an unknown baseline rather
than subtracting counters. Multiple entries for one query retain independent
baselines. CPU and duration remain unrounded microseconds throughout storage.

All three parents and their daily children force target-scoped row-level
security and grant no direct access to runtime roles. Delta history and replay
receipts are append-only. Partition preparation validates ownership, attachment,
UTC bounds, registry entries and the child's security policy against its parent;
an altered child policy fails closed. The existing retention registry receives
enabled policies for eight days of baseline state and 30 days of deltas/receipts.
The administrative SQL and domain validator enforce those minimum durations.
The scheduled retention driver uses its existing generic path for these
registered parents. Populated aged-partition retirement and recovery have now
been qualified as described below.

`events.apply_plan_cache_runtime_batch` is a private, closed JSON comparison
contract, with no EXECUTE grant to the collector, server, auditor or PUBLIC.
It independently checks target/revision scope, accounting including lookahead
and unqualified statements, UTC capture bounds, all six counters, unique cache
entries and matching before/after identity evidence. The batch is limited to
2,000 selected statements and four MiB. A per-target transaction lock protects
comparison and replay, without serializing unrelated targets.

First or changed identities, a falling counter, an expired comparison window
and stale reads never produce fabricated additive counters. Only a comparable
entry emits differences and an actual previous-to-current interval; a quiet
entry emits six known zero differences. Stale captures retain the preceding
watermark. A complete selected tuple can advance under limited aggregate
coverage, while an omitted or unqualified tuple does not advance. Source
coverage and unqualified counts survive in a fixed-size receipt rather than
duplicating raw rows into a replay ledger. Exact payload replay performs no
new write, and divergent replay fails. Invalid later rows roll back earlier
rows from the same invocation.
Complete current-cache coverage still cannot establish total work across
evicted entries or prove complete query-level rankings.

The four real PostgreSQL repository cases in `plan-cache-runtime-storage.trx`
cover migrated private storage, parent/child scope, administrative retention,
per-entry comparison against the domain calculator, all six falling counters,
incarnation/epoch/plan/entry changes, quiet and precision differences, stale
captures, limited/unqualified coverage, exact/divergent replay, 21 invalid
frame variants, and the exact seven-day versus one-microsecond-expired boundary
across daily partitions. `plan-cache-runtime-partition-scope.trx` rechecks the
affected storage case after strengthening child-policy validation, including
direct-child access and an intentionally altered policy. Six domain retention
cases and the forward-migration preamble check pass in
`plan-cache-retention-and-preambles.trx`. Existing applied migration bytes and
checksums remain unchanged.

These proofs exercise the private comparison transaction with fixture run IDs;
they are not canonical ingestion or production evidence. No production code
calls this writer yet. The typed source/output codec is now implemented as
described below. The next required step is a canonical wrapper that validates
the active target revision, run,
schedule, immutable collector contract, source accounting and live lease both
before and after writing, and atomic outcome/gap/schedule publication. Public
query observations, protected content links, canonical replay, run-retention
dependencies, bounded collector orchestration, rollout and native end-to-end/
fleet qualification remain required before activation.


## Typed cache payload and aged retention qualification — staged

`PlanCacheRuntimeOutputBatch` carries the exact target revision, source epoch,
effective candidate limit, all source accounting and selected statement tuples
with known live before/after database identities. It enforces the count partition,
unique cache entries, at most 256 database brackets, and one consistent bracket
per database. Unknown or omitted statements remain counts and never become zero
observations. Source coverage and qualified coverage are derived separately;
a capped sample can contain complete, usable statement tuples.

The source decoder now preserves its effective candidate limit. The assembler
checks the frame's accounting and coverage metadata before binding identities;
its output bridge retains the source counts and only qualified observations.
`PlanCacheRuntimeCommitPayload` serializes the existing closed 15-field header
and 19-field items, sorted by database, query and entry for deterministic replay.
It emits opaque identities and six unrounded Int64 counters within four MiB.
A maximum 2,000-row frame with every counter at Int64.MaxValue fits both the
serialized limit and the conservative payload size estimate.

All original UTC capture bounds and identity brackets are checked before
truncating timestamps to PostgreSQL's microsecond resolution. The immutable
batch, its watermarks and its serialized evidence then share those normalized
timestamps, so in-memory comparisons of published batches use the same time
resolution as persistence. Counter precision is unchanged. Normalized windows
are checked again; conversion cannot hide an invalid original overlap.

Twenty-eight domain cases pass in `plan-cache-typed-output.trx`. The 45 source
frame/identity binding checks in `plan-cache-typed-source-bridge.trx` exercise the
bridge together with the existing native-frame decoder. The affected decoder
limit assertions and database-bracket mismatch case are recorded separately in
`plan-cache-typed-boundary-final.trx` and `plan-cache-typed-limit-final.trx`.
Two codec checks in `plan-cache-typed-codec-postgresql.trx` prove maximum sizing,
closed field counts, exact microsecond persistence, one-microsecond counter
differences, partial receipts, and replay without duplicate writes after row
reordering. The actual SQL Server 2022 bracketing/replacement/passivity case in
`plan-cache-typed-native-bridge.trx` also converts real frames into typed batches
without changing any source counters or identity scope.

`plan-cache-aged-retention.trx` exercises populated 40-day-old partitions in all
three private cache parents through the real PostgreSQL retention worker. A
restored pg_dump archive reproduces the exact watermark, delta and receipt
contents before attestation. Missing recovery attestation, active readers,
pending backfill and the minimum partition floor block retirement. A stale
worker fence is rejected before both detach and drop. Detach preserves rows
during grace, a disabled policy reattaches its partition, and active readers
continue to block drop after grace. Final retirement drops the aged relations,
keeps current data, records eight successful lifecycle audit entries, and the
same archive restores the complete original cache contents after deletion.
Historical rows are seeded by the private migration role; the source writer's
five-minute clock bound is never relaxed.

These remain private storage and source proofs. The payload is deliberately
not attached to `CollectorPayload` or routed to an existing collector commit
until a versioned canonical path can account for every cache item and commit
its deltas, replay receipt, outcome, visibility gap and schedule under one live
lease. Query Store coverage, protected content links and existing query identity
semantics must survive that change. Target sweep orchestration, canonical
run-retention dependencies, public ranking/API integration, catalog rollout,
and deployment/native fleet qualification remain open.

## Fenced Query Store and cache commit — staged

The pinned `queries.performance.v2.json` manifest retains Query Store, protected
query text, execution plans and query waits, and adds the bounded cache source
and live database identity assets. Its exact schema and five-entry bundle index
pin both source paths. Version 2 remains disabled by default. Migration 0318
does not install its collector contract or grant execution to runtime roles;
existing version 1 scheduling and ingestion remain active.

`control.commit_query_performance_v2` commits Query Store observations and
selected cache tuples in one transaction with protected content links, wait
snapshots, the canonical outcome, visibility gap and next schedule. It checks
the target revision, run identity, schedule, immutable manifest and asset pins,
source counts, output counts, byte bounds and circuit transition. The worker
lease is checked before writes and again after outcome and schedule publication.
Cache tuples use the private delta writer and never enter the legacy cumulative
cache projection. Query Store still follows the existing observation-v3 writer
route. Public rows and selected cache tuples count once each; protected links
and mirrored rows do not add output items.

The stored completion digest binds every summary, circuit, source and sidecar
argument as well as the caller's digest. Exact replay produces no new writes;
changed completion arguments fail. Evidence without a canonical completion,
including an empty cache receipt, blocks the commit. A timeout emits no cache
observations, advances the failure circuit and records a gap with an unknown
loss count rather than presenting its required gap marker as an exact count.

Five focused checks cover the manifest and real PostgreSQL lifecycle. The
initial `plan-cache-canonical-v2.trx` passed the manifest, rollback and immutable
pin cases. Its other two cases exposed fixture setup omissions: the observation
v3 parity certificate and timeout source visibility. After correcting those
fixtures, both passed in `plan-cache-canonical-v2-routing-failure.trx`.
`plan-cache-canonical-v2-sidecars.trx` verifies successful protected text, plan
and wait links in the combined commit. The two affected cases pass again in
`plan-cache-canonical-v2-final-boundaries.trx`, adding exact replay through the
partitioned collection-run route, an inexact timeout gap and rejection of an
orphaned empty receipt. The rollback case covers eleven invalid inputs or late
failures, including a bad later cache row, missing plan content and lease release
after writes; all contributions and schedule changes roll back. Subsequent
valid deltas prove those failures did not advance the cache baseline.

The forward-migration preamble check passes in
`plan-cache-canonical-v2-preamble.trx`; all 318 indexed checksums match and
the existing 317-entry index is unchanged. These are private transaction proofs
using the real repository scheduler and fixture-only version 2 contract. No
production collector calls this function yet. Attaching the typed cache batch
to `CollectorPayload`, explicit versioned validation and repository routing,
bounded native orchestration, public cache ranking/API, canonical run-retention
dependencies, safe catalog rollout and native fleet qualification remain
required before activation.

## Typed collector payload and version 2 repository routing — staged

`CollectorPayload.PlanCacheRuntime` now carries the immutable selected cache
batch and includes its tuples and conservative frame size in output accounting.
Output contracts deny this member by default, including an empty frame. Only
the matching `queries.performance` version 2/output schema 2 protocol accepts
it. Both collector validation and the repository boundary check the exact
target revision, aggregate source accounting, response bounds and explicit
loss for capped or unqualified evidence. Public observations in this protocol
remain Query Store interval observations; legacy cumulative cache observations
are rejected. Wait sidecars occur once per plan so their logical byte accounting
matches their persisted representation. Multiple intervals for a plan remain
valid when the wait snapshot is attached once, as the native reader does.

The C# repository dispatches version 2 to the private fenced atomic commit in
its own transaction with transaction-local target scope. It serializes cache
evidence, text and plan references and wait snapshots into the same call. The
SQL completion digest binds the cache frame independently of the reused Query
Store envelope digest. Output rejected by the execution engine contributes no
persistable observations; its retained item count and byte count are carried
as loss evidence and recorded as rejected output. Lease assertion failures
roll back the transaction and return `LeaseLost`. Version 1 keeps its existing
commit route, and unsupported or mismatched versions are rejected before I/O.

Protected-content replacement previously rebuilt only the original payload
members. A failing regression exposed loss of the new cache batch during that
operation. The replacement now preserves the complete payload alongside opaque
text and plan references, keeping its count, bytes and wait evidence unchanged.
The failing case is recorded in `plan-cache-v2-content-preservation-red.trx`
and passes with the two existing content-writer cases in
`plan-cache-v2-content-preservation.trx`.

Seventeen new unit cases cover the versioned output boundary. Fourteen pass in
`plan-cache-v2-output-contract.trx`; the additional content, wait accounting
and version cases are recorded in `plan-cache-v2-content-preservation.trx`,
`plan-cache-v2-wait-accounting.trx` and `plan-cache-v2-version-boundary.trx`.
Two real PostgreSQL C# route cases pass in `plan-cache-v2-repository-port.trx`,
covering successful text/plan/wait links, changed-cache replay rejection,
missing-plan rollback, lease release after writes, valid subsequent six-counter
deltas, partial results, rejected output, timeout replay and Query Store-only
output. The collector role still cannot execute the staged function. The
pre-I/O version/scope/accounting matrix passes in
`plan-cache-v2-repository-boundary-final.trx`, including mismatched or unsupported
versions without cache frames. `plan-cache-v2-cache-only-final.trx` proves commit
and replay of selected cache tuples while Query Store is disabled: Query Store
status remains disabled/unavailable and no public Query Store rows are invented.
Its initial run committed successfully but failed an incorrect source assertion;
the corrected assertion preserves the existing Query Store metadata semantics.
Five existing version 1 text-link commit, rollback and v3 routing cases pass in
`plan-cache-v2-legacy-route.trx`.

These 28 focused checks qualify the payload boundary and C# transaction route,
using a fixture-only version 2 catalog and the private migration identity.
No new runtime grant or active source registration is installed. Bounded native
coordination of Query Store, cache and live identity reads, public cache ranking
and API integration, canonical run-retention dependencies, safe catalog rollout
and native end-to-end/fleet qualification remain required before activation.

## Bounded native cache target sweep — staged

`PlanCacheRuntimeTargetSource` now performs a bounded native inventory, live
identity reads, one pinned cache capture and final live identity reads. It
allows at most eight database connections concurrently and at most 256
inventory databases, with a 257th look-ahead entry left unopened. Inventory
and each identity phase have five-second budgets; cache capture has a
20-second budget and the entire cache sweep shares a 30-second deadline
bounded by the original execution request. After reads are attempted only for
databases with completed before evidence. The source never retries a cache
capture to find more identities.

Identity phase expiration retains completed evidence and leaves missing cache
statements explicitly unqualified. Caller cancellation takes precedence over
a faulted sibling. Capture or overall deadline expiration stops the sweep;
malformed or foreign inventory, identity and cache frames are rejected. The
existing assembler still requires matching live incarnation and epoch evidence
within the original SQL capture windows before creating typed tuples. Cache
candidates and returned identity frames contribute once to source accounting;
response accounting retains the cache header, emitted native rows and identity
frames without rounding any counters.

The native port inventories online, multi-user databases whose catalog
collation is present. It rechecks ID, name and that eligibility before changing
database context, avoiding a known closed AUTO_CLOSE database. An inaccessible
database or identity command timeout yields unavailable identity evidence.
Transient target failures and invalid source frames remain failures. Every
connection and reader is disposed before the next phase.

Eighteen focused orchestration checks pass in
`plan-cache-target-orchestration.trx`. They cover concurrency, one capture,
missing/unknown/replaced/changed-epoch identities, partial identity phase
expiration, inventory bounds and shape, foreign frames, an effective capped
sample, capture cancellation and caller cancellation against a faulted sibling.
Two additional phase-expiration cases verify that a provider fault after
cancellation remains a timeout, with the affected cancellation cases passing
in `plan-cache-target-deadline-boundaries.trx`.
The real SQL Server 2022 restricted-login case passes in
`plan-cache-target-native-open-fixture.trx`. It reads two disposable active
databases, qualifies only the accessible database, records the denied database's
cache rows as unqualified, keeps a third AUTO_CLOSE database closed, verifies
connection cleanup and repeats with a one-candidate cap. Initial fixture runs
exposed a procedure batch error and inherited AUTO_CLOSE settings; the fixture
now declares its intended open and closed states explicitly.

This qualifies the cache phase of native orchestration, not the combined
version 2 collector. Connecting this sweep to the Query Store reader under one
overall deadline and response budget, while preserving text/plan/wait capture
and Query Store preference, remains the next source integration step. Public
cache ranking/API, canonical run-retention dependencies, versioned catalog
rollout and combined native end-to-end/fleet qualification remain open. No
collector registration, catalog version or runtime grant is activated here.


### Combined native v2 producer and shared bounds (2026-10-01)

The staged `SqlServerQueryPerformanceV2Collector` loads the approved v2 manifest
and exact schema, reviewed Query Store assets, cache counter bundle and live
identity source. Its embedded catalog verifies the index and every nested pin.
It does not install a production catalog version or register a collector.

The cache capture and Query Store sweep share the original request deadline,
capped at 120 seconds. Each phase receives the remaining time. The Query Store
reader retains text, execution-plan and wait capture, explicitly disables legacy
cumulative-cache fallback, and uses the real v2 identity. Its native inventory
and context changes avoid known closed AUTO_CLOSE databases. Private cache tuples
remain independent from public Query Store intervals; no cumulative totals are
relabelled as intervals or added to public query rankings.

Source frames, selected tuples and normalized output bytes are counted
separately. Normalization can expand output without inventing source response
bytes; registered source and output limits apply independently. Query Store
receives the response and output budget remaining after cache capture. Its
retained prefix also fits all five canonical PostgreSQL JSON inputs: Query Store
metadata, cache metadata, text links, plan links and wait snapshots. The cache
codec is shared with the private repository writer. Protected payloads are
measured with their eventual fixed-width opaque references. Withheld observations
update database statuses and expose inexact loss.

Two failing regression checks demonstrated the former source/output byte
coupling and the serializer rejecting wait-bearing observations; both pass now.
Thirteen focused source checks cover asset pins, exact combined accounting,
Query Store-only permission behavior, disabled Query Store with cache evidence,
partial cache coverage, global failures, one deadline, caller cancellation,
a 20,000 Query Store / 2,000 cache sample with wide integer counters, and
independent serialized/output limits. Results are in
`combined-native-orchestration.trx`, `combined-native-deadline-and-v1.trx` and
`combined-native-loss-byte-bound-red.trx` (the wide-counter check passed; that
filename does not denote a failing result).

The restricted-login SQL Server 2022 fixture passes with native Query Store
intervals and qualified cache tuples, protected text and execution plans under
a disposable in-memory key, denied cache rows represented as unqualified,
connection cleanup and a closed AUTO_CLOSE database remaining closed
(`combined-native-protected-sidecars.trx`). The private PostgreSQL fixture
confirms the shared byte measurement equals actual JSONB text size and preserves
the atomic commit, replay and rollback checks
(`combined-native-postgresql-budget.trx`). Three affected v1 serialization checks
and 27 output, protected-content and execution-engine checks pass. Across these
artifacts, 45 unique focused checks pass. These use local disposable fixtures,
not the SSH VM or a production fleet.

A native-producer-to-fenced-repository round trip, public cache ranking/API,
canonical run-retention dependencies, production catalog rollout, SQL Server
2019/2025 and fleet qualification remain open. The lab key qualifies capture
behavior, not deployment key-store configuration. No production registration
or runtime grant is activated here.


### Native producer through the fenced execution path (2026-10-01)

`NativeCombinedProducerCommitsBaselinesDeltasProtectedLinksReplayAndRollback`
now drives two real SQL Server 2022 captures through native capability discovery,
fenced capability recording, `CollectorExecutionEngine`, the output validator,
protected-content writes and the private PostgreSQL v2 canonical commit port.
Two disposable databases supply Query Store-enabled and Query Store-disabled
workloads under an impersonated login with server/database performance-state
permissions; the effective source identity is not sysadmin. The fixture uses
its local lab connection policy and a disposable in-memory protection key.
The v2 catalog is seeded only in the disposable PostgreSQL qualification database;
its runtime grant remains absent and production registration is unchanged.

This exposed a real source defect: SQL Server reports disabled Query Store as
`OFF`, while the shared reader mapped only `DISABLED`. It then tried to construct
a disabled database status with an unsupported source state, causing the whole
capture to fail. The reader now maps both values to Disabled. The initial native
round-trip artifacts fail at this boundary; the corrected fixture passes in
`combined-native-fenced-roundtrip-metrics.trx`.

The first capture persists the cache baseline as unavailable deltas, with all
six delta counters NULL. Three further executions produce a comparable tuple;
all six persisted microsecond/count differences equal the two native samples,
and the execution delta is exactly three. Every persisted Query Store interval
retains its six metric values. Outcome source rows, response bytes, selected
items and normalized bytes match the engine result. Query Store remains disabled
for the cache-only database, with no invented public interval rows. Protected
text/plan links and wait-snapshot counts survive the commit; the protected-content
reference replacement preserves the combined serialization measurement.

Both captures replay exactly without changing the snapshot. A changed completion
summary returns 40001. A nonexistent plan reference fails after source writes,
and a trigger expiring the lease during outcome insertion returns LeaseLost with
zero inserted items/bytes. Snapshot equality proves rollback of run, Query Store,
cache, link, lease and schedule changes; removing the trigger allows the same
native result to commit. Ciphertext remains a separate, earlier fenced write,
consistent with the existing protected-content protocol.

This qualifies the staged native producer-to-repository path on the local SQL
Server 2022 and PostgreSQL fixtures. Canonical run-retention dependencies, public
cache ranking/API, production catalog activation, SQL Server 2019/2025 and fleet
qualification remain open. It does not qualify production TLS/key configuration,
end-to-end HTTP/MCP projection, or the broader revamp as complete.


### Cache dependencies on canonical runs (2026-10-01)

Forward migration 0319 anchors each private cache watermark, delta and receipt
(including empty receipts) to `telemetry.collection_run_identity` with a validated,
non-cascading foreign key and a run-leading index. Native canonical run headers
already populate that bridge. The standalone storage fixtures now seed real run
anchors; their synthetic v1 headers do not qualify v2 leases or activation.

The query-performance graph retirement preview includes exact target/run cache
holds. Attached evidence reports watermark/delta/receipt holds. Detached evidence
remains a hold throughout partition grace, including after reattachment and a
second detachment. Dropping all expired cache partitions releases those holds.
Unrelated run IDs do not acquire a time-window approximation hold. Missing or
unverified detached relations fail closed, and the helper remains private to the
migration role with an exact target scope.

The populated migration test exposed PostgreSQL foreign-key validation overlooking
rows hidden by forced RLS. The migration locks the private cache relations, exposes
SELECT only to the migration owner within its transaction, and explicitly checks
all existing attached and registered detached evidence. Temporary policies are
removed before commit. Valid evidence is preserved; orphan evidence aborts the
upgrade without deleting rows, fabricating headers, or recording migration 0319.
Existing detached grace tables also receive their own run index and foreign key.

Focused PostgreSQL checks cover catalog constraints, unknown-run rollback, runtime
role denial, wrong target scope, valid/orphan upgrade adoption, detached grace,
reattachment, expiry and restore. Evidence is recorded under
`artifacts/plan-cache-deltas/plan-cache-retention-*`. Eight focused PostgreSQL
cases pass across the final boundary and upgrade runs. The real native SQL Server
2022-to-PostgreSQL fenced round trip also passes with 0319 applied, recorded in
`combined-native-run-dependencies.trx`; the migration preamble check passes.
This adds cache dependency protection to the existing graph-retirement gate. It does not implement broader
canonical run/identity cleanup, public cache ranking/API, production catalog
activation, SQL Server 2019/2025 or fleet qualification.


### Public cache interval ranking (2026-10-01)

Forward migration 0320 adds the target-scoped server projection consumed by
`GET /api/v1/observation-targets/{instanceId}/query-performance/cache/top`.
It accepts one of six ranking metrics, UTC bounds at whole microseconds, at most
seven days and 100 results;
the Queries screen requests the top 25 through the shared fetch client. The
application service requires Viewer, Operator or TargetAdministrator on that
exact target before repository access. Runtime identities retain no direct
SELECT privilege on the private cache tables.

The new **Cache interval deltas** view uses only committed comparable increments
whose entire interval fits the selected window. It never clips a cumulative
counter difference to a smaller window. First samples, resets, stale reads and
expired comparisons remain unavailable. Coverage reports captured frames,
incomplete frames, comparable rows, unavailable rows and excluded boundary
crossings; an empty result does not assert zero activity. The ranking groups
query/plan entries within their database incarnation, preserving recreation
boundaries. CPU and duration retain microsecond differences converted to decimal
milliseconds. All six totals use numeric aggregation and exact decimal strings;
the browser compares them with fixed-scale BigInt instead of rounded Numbers.
A lookahead exposes when additional plans were observed beyond the top N.

The screen follows the global time window, supports all six metrics, formats
UTC/local observation time through the shared preference, and aborts obsolete
reads. Cumulative legacy observations stay on their existing evidence view,
with no manufactured history or protected-content link for cache ranking rows.

Evidence: the initial PostgreSQL regression fails because the projection is
absent (`cache-interval-read-red.trx`). Focused real PostgreSQL tests exercise all
six typed reads, baseline/comparable/reset coverage, exact scope and current
revision, interval boundaries, database recreation, Int64 maximum counters and
top-N lookahead (`cache-interval-read-typed.trx`,
`cache-interval-read-scopes-topn.trx`). The native SQL Server 2022 round trip now
reads through the server-role public port and compares all six metrics with the
two actual native captures (`cache-interval-native-public-read.trx`). Service
tests cover mixed grants, large
exact totals and invalid repository output; seven real-pipeline HTTP cases cover
role scopes, no-store and invalid bounds. The runtime route inventory and migration
preamble checks pass. Five browser-client tests cover exact
ordering and contract drift. Two focused Chromium checks verify rendered exact
values, coverage, metric/window binding, obsolete target reads and an axe scan
of the new panel. TypeScript and the production web build pass.

The native v2 producer remains staged. This read path does not activate its
catalog or grant ingestion, replace legacy cumulative API/MCP history, supply a
cache history/content drill-down, or establish SQL Server 2019/2025/fleet proof.
Those are remaining parts of the revamp, alongside canonical run cleanup and
other requirements in the original review.


### Shareable query views and rankings (2026-10-01)

Queries now reads its selected Top, Regressed or Cache interval view and its
ranking metric from the hash URL (`queryView`, `queryMetric`). The same metric
selection drives the interval-cache and legacy ranking controls. Reload and
browser history restore those controls. Rebuilding a URL for the global Rewind
window, changing the selected server or selecting a Query Store query/plan
preserves the view and metric; unrelated screens do not inherit query parameters.
Unknown view/metric values fall back independently to Top/CPU, and default values
are omitted from generated URLs.

Three focused model tests cover every view/metric combination, preservation of
server/window/query/plan fields and invalid/default values. The affected dashboard
route tests also pass. Three focused Chromium cases now cover rendered ranking,
server changes during pending evidence, and shared-URL reload/Rewind/Back behavior.
TypeScript, the production build and web asset verification pass. No database or
native-source rerun was needed for this frontend state change. Cache history,
collector activation and the broader revamp requirements remain open.


### Cache query interval drilldown (revamp branch, 2026-10-01)

The cache delta ranking now opens a separate per-entry history view. Its URL retains
server, database ID and incarnation, query, plan, metric and the shared time window.
Changing servers clears the cache selection; changing the time window re-reads it.
Each row preserves its actual comparison interval, source epoch, cache entry and
capture completeness. Baselines, resets, stale samples, expired comparisons and
intervals crossing the selected boundary retain null counters. The six native
counter differences are exact strings; CPU and duration convert microseconds to
milliseconds without rounding. This view does not aggregate different entry intervals
or supply protected query text/plan XML through the legacy Query Store routes.

The authenticated `GET /api/v1/observation-targets/{instanceId}/query-performance/cache/databases/{databaseId}/history/{queryFingerprint}/plans/{planFingerprint}`
requires `databaseIncarnation`, explicit `fromUtc`/`toUtc` ending in `Z`, whole
microsecond precision, at most seven days and `limit` from 1 to 100. Viewer,
Operator or TargetAdministrator must cover that exact target. Replies are no-store.
The database server role can execute the scoped projection but cannot select the
private cache tables or sequence.

Forward migration 0321 stamps the staged cache receipt parent with a repository
sequence. It rewrites existing receipts once, under the five-second migration lock
budget. Page one allocates a fence under the same target transaction lock as the
cache writer; this can briefly wait for an in-flight commit within the five-second
request deadline. Future writes receive higher sequence values, even when their
source timestamps precede the selected window's end. The sequence and its value
survive pg_dump/restore. Every later page uses a signed, purpose-bound cursor tied
to target, incarnation, query, plan, UTC window, limit, target revision, sequence
fence and deterministic last-row key. Default signer limits for existing cursor
purposes remain unchanged; this codec allows a 512-byte payload and a 728-character
token. Continuations expire after 30 minutes. Revision drift or retirement of any
matching snapshot row invalidates continuation rather than silently skipping data.
The browser loads 100 samples per page and stops at 1,000; narrowing the time range
is required beyond that bound.

Focused verification covers populated upgrade, actual backup/restore, an in-flight
writer, late commits, exact row ties without duplicates, database incarnation scope,
null and boundary comparisons, retired evidence, expired cursors, typed server-role
reads, mixed-role HTTP denial and signed cursor tampering. Client and browser checks
cover exact large counters, reloadable selection, paging, server changes and axe
accessibility. Collector activation, supported SQL Server version qualification,
fleet rollout and cache query content remain separate gates.

The native SQL Server 2022/PostgreSQL roundtrip also reads the new history through
the actual server-role port: it matches the probe's three added executions and
exact CPU difference, and preserves its first baseline and comparison timestamps.
One run lost the fixture's warmed five-execution cache entry before that assertion;
a focused repeat passed. This reinforces the existing cache eviction qualification
gate; it does not establish stable fleet coverage or SQL Server 2019/2025 parity.


### Native cache lifecycle and database recreation qualification (2026-10-01)

Two additional focused SQL Server 2022/PostgreSQL tests extend the combined native
roundtrip through its actual fenced runtime and public server-role readers. They
retain the existing protected-content, exact replay, late-reference rollback and
lease-loss checks. Each additional capture commits atomically and replays without
changing the repository snapshot. SQL mutations belong only to owned disposable
fixture databases; the production collector remains passive and staged.

The cache lifecycle case removes only the fixture procedure using `sp_recompile`,
then captures the resulting absence before another execution. PostgreSQL preserves
the previous two observations and three measured executions without inserting a
zero counter or a synthetic history row for that absent capture. After ten new
executions, the native reader returns a distinct cache-entry identity with the
same query, database incarnation and server epoch. This new allocation has a
higher execution total than the previous eight-execution allocation: nevertheless
its fenced comparison is a baseline with all six counters and interval bounds
null. The public ranking stays at three measured executions. Three further
executions resume a comparison at exactly the new allocation's baseline timestamp;
the public history returns three and the ranking totals six across the two valid
entry intervals, excluding the replacement allocation's initial ten executions.

The recreation case drops and recreates the owned cache-only database under the
same name, asserts reuse of its numeric database ID, and reinstalls the lab's
least-privilege database user and probe. Query and server epoch stay stable, but
live identity brackets report a different database incarnation. Its initial twenty
executions become a fresh unavailable baseline and contribute no ranking total.
Three further executions create a measured comparison. Public reads retain six
executions in the original incarnation and three in the new incarnation as
separate ranking rows. Each incarnation's history excludes the other's cache
entries. The connection tracker returns to zero; owned databases and login are
removed by the existing cleanup path.

Both focused runs pass: `artifacts/plan-cache-deltas/cache-native-lifecycle.trx`
and `cache-native-database-recreation.trx`. No broad suite, frontend checks or
migration rerun was needed because this change adds qualification evidence to
existing behavior. This closes these native lifecycle cases on the local SQL
Server 2022 fixture. Supported-version proof on SQL Server 2019/2025, fleet scale,
production TLS/key configuration and catalog rollout remain open.


### Measured cache interval rate chart (2026-10-01)

Cache history now plots the selected CPU, duration, executions, reads, writes or
rows metric as a rate over each full measured comparison interval. Counter and
interval arithmetic stays in integers at microsecond precision until the final
six-significant-digit chart value is produced. CPU/duration rates use ms/s; the
other counters use their units per second. Tiny nonzero rates retain scientific
notation. Exact counter strings and comparison timestamps remain in the table.
Rates beyond the chart's safe numeric range are omitted with an explicit count.

Each cache-entry/source-epoch pair has its own series. Every measured interval is
a separate horizontal segment; adjacent intervals are not interpolated, and
baselines, resets, stale samples, expired comparisons and boundary comparisons
do not become zero values. The chart uses the newest 300 loaded samples and up
to ten entries, with omissions stated beside the chart. These bounds do not
remove table evidence. Earlier pages can extend loaded evidence within those
chart bounds. Switching metrics reuses the loaded history snapshot. Dragging
on the chart updates the shared time window and retains the selected cache
query, database incarnation and metric in its shareable URL.

Six focused rate-model tests pass, alongside the two shared chart-model tests.
The focused Chromium cache-drilldown case passes with paging, metric switching,
actual pointer drag, URL preservation and axe accessibility checks. TypeScript,
the production build and generated web asset verification pass. No database or
native collector tests were repeated for this frontend-only change. Protected
cache content, supported-version qualification and production collector rollout
remain separate gates.


### Bounded native cache content source — staged (2026-10-01)

A separate checksum-pinned source bundle now reads protected content alongside
its native cache counters. It retains the existing bounded 2,000+1 candidate
capture and expands text/plans only for an explicitly requested prefix of up to
eight original candidates. Excluded candidates in that prefix do not cause
additional candidates to be opened. Existing counter-only SQL stays unchanged.
SQL Server 15/16/17 assets share bundle digest
`82409fe67c91f9e9b6ffc31b31516306f494b1a0328773bfb6e4133cdc302cd8`.

The source extracts a statement's UTF-16 byte range using a non-SC binary
collation and requests the statement plan with `dm_exec_text_query_plan`. It
rechecks handle, query/plan hash, offsets, creation time and generation against
live cache stats after extraction. Removed/recompiled entries have unavailable
content. Handles, plaintext and XML never enter the returned source object.
No usable protection key sets the content prefix to zero before executing SQL.

The closed reader binds each content row to the exact captured database, query,
plan and cache-entry identity; it rejects duplicates, extra results and forged
source states. Query text is limited to 16 KiB UTF-8 and plans to 1 MiB, without
truncation. The SQL result has a 2 MiB aggregate UTF-16 content budget, separate
from the existing counter/header budget; protected UTF-8 content has a separate
2 MiB aggregate budget. Oversize, missing, byte-limit and protection failure
states stay explicit. Strict encoding, cleared character/byte buffers and an
independent 20-second source/protection deadline bound the optional read. XML
is encrypted as inert content, without parsing it in production.

Thirty focused content/counter reader cases pass. One real SQL Server 2022
fixture passes with a non-sysadmin server-state reader, an astral character
before the statement, exact statement text and statement-plan identity,
AES-GCM protection, no-key capture, recompilation absence and permission denial.
Only owned disposable databases/login are changed. Evidence is in
`artifacts/plan-cache-deltas/cache-content-reader.trx` and
`cache-native-protected-source.trx`; the final combined 31-case check is
`cache-content-source-final.trx`. SQL Server 2019/2025 assets are pinned but
not yet qualified by these runs. The native test parses the decrypted fixture
XML with DTD/network resolution disabled; SQL Server's internal aggregate
operators are allowed and statement identity is verified through `StatementText`.

This is source evidence, not a persisted cache-content link. Live database
incarnation brackets must still qualify this whole read; a bounded domain
envelope, fenced canonical links, scoped retention, audited QueryTextReader
retrieval and inert cache-history rendering remain next steps. The staged
bundle has no production collector registration or API content exposure.


### Qualified cache content envelope and leased payload references (2026-10-01)

The optional content path now uses the target sweep's existing bounded inventory
and before/after live database identities around one combined counter/content
capture. It keeps the 256-database, eight-connection and 5/20/5-second phase
bounds. There is no second cache counter query. Content is retained only when
its complete statement tuple survives the same target, revision, epoch and
database-incarnation qualification as the runtime counters. Replaced, unknown
or unreadable database identities drop their content and increment an explicit
unqualified-content count. Malformed content still fails the frame even when
its database would have been excluded. Source row/byte accounting includes
content result rows and bytes, including evidence later excluded by qualification.
The existing counter-only target read and staged v2 collector retain their path.

A separate bounded domain envelope holds at most eight exact qualified statement
observations and two MiB of ciphertext. It preserves the three capture states
`enabled`, `disabled` and `protection_unavailable`, independently of per-kind
availability. Each text/plan value has exactly one protected payload or leased
reference when available; unavailable values have neither. Duplicate entries,
foreign incarnations/scopes, changed counters/timestamps, unsupported payload
kinds and inconsistent byte/accounting claims are rejected. Source capture
resolution is normalized to the same repository microseconds as the counter
frame. The collector helper deduplicates by kind/fingerprint under the supplied
lease, rejects a foreign scheduled target before writes, checks returned
reference fingerprints and preserves cancellation. Ciphertext writes remain
separate from the future canonical link transaction and can leave unreferenced
rows if that transaction never succeeds, as in the existing protection protocol.

Opaque persistence JSON is deterministic under row reordering and changes when
a reference changes. It contains exact database/incarnation/query/plan/entry
identities, capture time, safe state/count metadata and protected payload IDs;
it contains no plaintext, ciphertext or key metadata. Serialization is refused
until every available payload has a repository reference and is bounded to
32 KiB. These IDs are private writer input, not summary/API/MCP projections.

Focused evidence: twelve domain/leased-reference cases and 48 source cases pass,
including the extended real SQL Server 2022 fixture. Its non-sysadmin reader
now has a fixture database user to establish live identity brackets; protected
statement text and plan survive qualification, no-key capture retains its reason
and counters, and all connections close. Results are in
`cache-content-qualified-envelope.trx` and `cache-content-qualified-source.trx`
under `artifacts/plan-cache-deltas`. No PostgreSQL, frontend or broad suite was
run for this layer. A forward canonical cache-content link migration, versioned
collector payload/commit wiring, retention integration, audited retrieval and
inert rendering remain required before activation.

### Private cache-content storage and retention (2026-10-01)

Forward migration 0322 adds partitioned, append-only content receipts and
text/plan links with forced target-scoped RLS. Runtime collector, server and
auditor roles cannot read or write these tables or call the private writer.
Available links require the protected payload's exact target, kind, fingerprint
and ciphertext length. Missing values retain their reason without a reference.
The writer checks an unfinished v3 run, current worker lease, active target
revision, and the exact counter receipt and qualified statement tuple, including
database incarnation and cache-entry identity. It rejects altered replay,
unknown fields, duplicate entries, non-microsecond timestamps and inconsistent
source/ciphertext budgets. A late invalid link rolls back every preceding link
and its receipt; an enclosing transaction can also roll back the counter frame.

Content links and receipts have independent 30-day retention floors and share
the bounded daily partition maintenance path. They retain canonical run
identities while attached or in detached grace. Orphan text and plan cleanup
now checks both attached links and registered detached link partitions under
registry locks, preserving ciphertext until the link partition is dropped.
Unverified registered grace data conservatively prevents payload cleanup.
No cross-partition foreign key ties content retirement to counter retirement;
the private writer enforces that relationship at insertion. Future retrieval
must require surviving counter evidence as well as the content link.

Five distinct PostgreSQL 18 checks pass: exact-link and rollback boundaries,
private catalog and policy floors, content backup/restore and the real leased
detach/grace/drop/orphan lifecycle, plus the existing counter partition and
retention regressions affected by the shared functions. Eleven focused domain
policy and migration-preamble checks pass. Results are
`cache-content-storage.trx`, `cache-content-shared-regressions.trx` and
`cache-content-retention-contracts.trx` in `artifacts/plan-cache-deltas`.
The fixture clones a synthetic v3 contract only in its disposable database;
it does not qualify or activate a production v3 collector. Versioned canonical
commit/output wiring, audited retrieval, inert rendering and rollout remain
required. No frontend, SQL Server or full suite was rerun for this storage step.

### Versioned canonical cache-content commit (2026-10-01)

The staged `queries.performance` v3 manifest pins the existing Query Store
resources, counter identity protocol and combined cache counter/content bundle.
Its exact schema uses manifest format 9 and output format 3. Query Store and
cache retain the shared two-minute deadline and eight-MiB source/output limit;
the content phase retains the preceding eight-entry and two-MiB bounds.
Logical output accounting now includes up to eight qualified content pairs,
raising the combined item ceiling to 22,008 and source ceiling to 514,777.
Every collected counter frame carries an explicit content receipt, including
disabled and protection-unavailable frames with zero content. Query Store-only
and failed runs carry neither counter nor content frames.

The collector payload, registered output contract, scheduler protection helper
and repository dispatch support v3. Protected content becomes leased opaque
references before repository commit; unlinked content is rejected before
repository I/O. Producer budgeting measures all six JSON inputs with fixed-width
placeholder IDs, and the content measurement agrees with PostgreSQL JSONB bytes.
Content does not cause a second cache capture or reset the overall deadline.

Forward migration 0323 adds a private versioned canonical writer. It binds the
immutable v3 manifest/bundle pins, run identity, target revision, schedule and
lease. Query Store observations and sidecars, cache deltas and watermarks,
content links and receipt, visibility gap, run outcome and next schedule commit
in one transaction. The replay digest includes content and every existing
summary/circuit/source input. A late bad content or Query Store plan reference,
or lease expiry while writing the outcome, rolls the whole transaction back.
Failure and rejected-output accounting retain the enlarged bound without
publishing earlier content. Applied v1/v2 manifests and SQL migrations are
unchanged; the existing v2 producer still uses its counter-only path.

Forty-five distinct focused checks pass: twenty shared producer checks, fifteen
domain/protection/migration checks, two existing v2 repository regressions and
eight final v3 checks. The final v3 group includes an actual SQL Server 2022 to
PostgreSQL 18 round trip through capability discovery, engine execution, leased
AES-GCM payload writes, canonical replay, late-reference failure and lease-loss
rollback. Its disposable non-sysadmin SQL reader produces qualified protected
cache text/plans; all connections close. Evidence is in
`cache-content-v3-source.trx`, `cache-content-v3-contracts.trx`,
`cache-content-v3-boundary-regressions.trx` and
`cache-content-v3-qualified-final.trx` under `artifacts/plan-cache-deltas`.

No v3 production catalog, runtime grant or schedule activation is installed.
Audited exact-run/incarnation/entry content retrieval, inert history rendering,
SQL Server 2019/2025 qualification and qualified rollout remain required.
No frontend or full suite was run for this commit step.

### Audited cache-content reads and inert history rendering (2026-10-01)

Forward migration 0324 exposes a Server-only content lookup for an exact target,
run, database ID/incarnation, query, plan, cache entry and payload kind. It requires
transaction-local target scope and content-reader context. The application
requires both an operational read role and QueryTextReader on that same target.
Mixed grants cannot supply either role from another server.

The lookup requires a retained cache delta and receipt, current target revision,
canonical run header and succeeded/partial outcome. Available content additionally
requires its matching content receipt/link and bounded protected payload. There
is no payload-ID or latest-query fallback. Retiring counter evidence makes
still-retained content inaccessible. Older counter-only captures report missing
content; disabled capture, absent selection, source loss, size limits and key
failures retain distinct reasons. Raw content tables remain private.

The service records denied, unavailable or opened access in the existing
append-only audit envelope using opaque observation identities. It never audits
plaintext, key material or payload IDs. Plaintext leaves the service only after
the opened audit commits. Decryption uses strict UTF-8 and the capture size
bounds; decrypted byte buffers are cleared on success, audit failure and
cancellation. Read and audit scope settings are transaction-local.

Cache history now offers explicit text and plan-XML buttons to authorized readers.
Content renders as an inert React text node in a scrollable preformatted element,
with no XML parsing, HTML insertion or execution. Each opening uses no-store
transport, validates every returned observation identity and checks UTF-8 content
size. The wire bound accommodates JSON escaping of a one-MiB plan. Closing,
changing the server/window/refresh context or losing reader access unmounts the
content view and cancels pending reads. Content selection is not saved in URLs or
browser storage. Missing content explains its cause and the next useful action.

Sixty-eight distinct focused checks pass: twenty-three service checks, nine real
HTTP-pipeline checks, ten PostgreSQL content-read checks, the extended native
SQL Server 2022/PostgreSQL 18 round trip, four content-client checks, three
Chromium interaction/accessibility checks and eighteen existing query/history
client regressions. The native case reads actual collected ciphertext through
the new Server repository and audited service, comparing its plaintext with the
original captured bytes. The web production build and asset verification pass.
Database/HTTP/service evidence is in `cache-content-read-final.trx`,
`cache-content-audited-native.trx`, `cache-content-read-http.trx` and
`cache-content-read-service.trx` under `artifacts/plan-cache-deltas`; browser
evidence is under `artifacts/web-browser`.

This step does not activate v3 collector catalog grants or schedules. SQL Server
2019/2025 qualification, production key/TLS qualification and qualified rollout
remain. No full suite or SSH-lab source deployment was performed.

### Production provider composition and native Windows-key qualification (2026-10-01)

Production dependency injection now supplies the staged v3 producer with the
configured QuerySensitiveContentProtector. The public producer entry point uses
the normal Windows-integrated SQL connection factory; the connection override
remains an internal native-test seam. Registry/catalog selection stays on query
v1 until the remaining version qualification and rollout gates are satisfied.

The extended native case creates an isolated restricted key directory and a
32-byte LocalMachine DPAPI key. It resolves the actual collector/key-provider
composition, replacing only the source connection opener with the owned
non-sysadmin SQL fixture. SQL Server 2022 capture, protection, leased PostgreSQL
storage, canonical replay and late-failure/lease-loss rollback run with the
production provider. A distinct reader instance loads the same key and retrieves
the exact captured text and plans through the audited Server port. Another valid
key cannot open the content and records one unavailable audit without plaintext
or key identifiers. Temporary key material, fixture databases and login are
cleaned up.

Native fixture preparation now recreates only its owned procedure's cache
baseline immediately before collection. One diagnostic attempt could not find
the earlier five-execution allocation after repository/host setup; the test
retains bounded counter diagnostics without dumping content. The final selection
passes both the production-key v3 case and the existing counter-only v2 case.
The production-composition check also passes, including default v1 registry
selection and an unavailable key when no key path is configured. Evidence:
`cache-content-production-qualified-final.trx` (two native checks) and
`cache-content-production-composition.trx` (one composition check) under
`artifacts/plan-cache-deltas`.

This qualifies the real key provider on one Windows machine. Distinct service
identities, production certificate trust, SQL Server 2019/2025 and catalog/runtime
activation remain unqualified. No frontend or full suite was rerun for this step.
