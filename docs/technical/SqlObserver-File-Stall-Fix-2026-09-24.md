# Separate read and write file stalls

File I/O observations now preserve SQL Server's separate cumulative read and write stall counters through collection, PostgreSQL, HTTP, MCP, and the health panel. The existing combined counter remains available. These are accumulated milliseconds, not per-operation latency.

Historical observations retain null directional counters and display “Not reported.” A known pair must be nonnegative and sum exactly to the total. New pairs add 16 bytes to payload accounting; total-only historical payloads keep their original accounting and replay digest.

## Upgrade

Forward migration 0084 adds nullable counters to the canonical snapshot table and its existing partitioned mirror, with paired-value constraints. The matching application uses versioned commit and read functions; existing entry points are preserved. New functions retain migrator ownership, a fixed search path, and separate collector/server execution grants.

Running backfill cursors are converted to the expanded row representation without changing their position. This matters because the mirror has no uniqueness constraint: restarting an interrupted backfill would duplicate rows. Historical snapshots, outcomes, and replay hashes are unchanged.

Deploy the migration with the matching application. SQL Server query assets and collector output schema versions are unchanged; the queries already returned both counters. Retention redesign remains separate work.

## Verification

- Ten focused domain/collector tests passed. The new mapping cases first failed against the old implementation.
- Ten distinct PostgreSQL tests passed across focused runs, covering known/zero/unknown splits, exact replay, altered-pair rejection, invalid raw inputs, legacy upgrade/replay, an interrupted equal-timestamp backfill, function permissions, and three existing collector lifecycle/paging cases. The first run exposed missing SQL byte accounting for the new pair; the affected cases passed after correction.
- Two focused HTTP tests passed, including exact counters beyond JavaScript's safe integer range and explicit null historical fields.
- Two MCP schema/mapper cases passed. The historical case first exposed omission of required null fields; explicit null emission corrected it.
- The web TypeScript check passed. Migration and dependent release asset pins were refreshed and verified.

No full Local validation, full PostgreSQL suite, broad CI run, or native SQL Server qualification was run for this slice.
