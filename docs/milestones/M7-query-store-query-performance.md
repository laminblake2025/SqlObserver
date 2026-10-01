# M7 — Query Store and query performance

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

## Plan-cache delta refactor — comparison foundation

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

## Bounded native cache source — staged

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
The source page deliberately has no database-incarnation watermark binding yet.
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
and database-replacement races, live incarnation binding, lease-fenced atomic
storage/replay/retention, and the catalog/API/UI cutover remain required.
