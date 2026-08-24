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
