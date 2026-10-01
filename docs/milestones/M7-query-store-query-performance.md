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
watermark and emits no interval. A gap longer than the existing seven-day query
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
Failure, unknown incarnation and truncation must leave watermarks unchanged.
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
