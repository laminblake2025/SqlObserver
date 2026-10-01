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
