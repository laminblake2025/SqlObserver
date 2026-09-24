# Collector correctness and alert scalar boundaries

This change repairs local correctness defects from the September 23 repository
review. It does not implement the scheduler, retention, or interface redesigns.

## Blocking and availability groups

The SQL Server 2019, 2022, and 2025 blocking queries exclude waits whose blocking
session is the waiting session. The graph builder independently ignores those
edges before consuming graph capacity. Real cycles, cross-session blocking, and
special negative blocker identities retain their existing handling.

Availability-group state parsing now validates each field against its own SQL
Server state vocabulary. This preserves states including `SUSPECT`,
`RECOVERY_PENDING`, `DISCONNECTED`, and `FAILED_NO_QUORUM`, while unknown or
misplaced values remain `unknown`. The collector still reports the same replica
visibility; reading all replicas on the primary remains separate planned work.

The parser and graph tests reproduced 30 failures before the changes, with 27
controls already passing. All 57 cases pass afterward. The strict fake reader
also verifies sequential column access. Evidence:

- `artifacts/revamp-collector-tests/results/blocking-ag-red.trx`
- `artifacts/revamp-collector-tests/results/blocking-ag-green.trx`

## Collector failures and Query Store fallback

A shared numeric classifier makes eligible connection, network, deadlock, and
lock-timeout failures reach the existing bounded retry and circuit logic. SQL
command timeout `-2` remains a deadline result; authentication and unrecognized
SQL errors remain permanent. The classifier adds the reported errors `11001`,
`-1`, `26`, `258`, `121`, `10061`, `1205`, and `1222` without changing attempt
limits, deadlines, or circuit thresholds.

Query inventory and Query Store reads now propagate transient SQL errors and
caller cancellation instead of absorbing them into generic read failures.
Error `916` retains permission-denied state, including when an explicitly
permitted plan-cache fallback runs. This does not create database users or
change the permission generator.

The failure-injection suite exercised actual collector entry points and the
fallback coordinator: 118 failures and 79 controls before the fix, then 205
passes including eight existing execution-engine tests. An injected connection
failure reaches exactly the existing two attempts. Synthetic SqlExceptions are
constructed only in tests against the pinned SqlClient version. Evidence:

- `artifacts/revamp-collector-errors/results/collector-errors-red.trx`
- `artifacts/revamp-collector-errors/results/collector-errors-green.trx`

Independent review then identified a concurrent-read edge case: `Task.WhenAll`
can prefer one database's SQL fault over another database's cancellation. The
orchestrator now checks caller cancellation and its shared deadline before
propagating that fault. Six gated concurrency cases reproduced four failures
with two controls passing; all six now pass. The complete unit suite passes
670/670 and the nearby Query Performance suite passes 29/29. Evidence is in
`artifacts/revamp-query-cancellation/`. Retry budgets and database concurrency
are unchanged.

## Replication latency

The replication SQL preserves nonnegative `current_delivery_latency` values,
which are already integer milliseconds. The former clamp changed values above
2,147,483 milliseconds to `int.MaxValue`, causing the parser to reject ordinary
latencies of roughly 36 minutes or more. Missing, stopped, and not-yet-started
Agent evidence still produces unavailable latency. Rate scaling and the
parser's existing 24-hour acceptance bound are unchanged.

An executable local arithmetic check extracts the actual expressions from all
three SQL assets. It evaluates their CASE and NULL behavior using SQLite, with
a narrowly defined integer CONVERT shim: 12 failures and 27 controls before,
then 39/39 passes. This is not native T-SQL execution. Eleven .NET parser cases
also pass. Three native SQL test cases compile and remain pending; they require
the existing lab fixture and validated TLS and execute parameterized SELECTs
without creating target objects. Detailed evidence is in
`artifacts/revamp-replication-latency/evidence.md`.

## Deployment

New forward migration `0080_passive_collector_bundle_repairs.sql` updates exactly
four activity bundle pins and one replication bundle pin. Each update requires
the known previous digest; an unexpected or missing row fails the migration.
The migration runner's transaction restores the append-only trigger and rolls
back earlier updates if either guard fails. Historical migrations remain intact.

Deploy the matching collector binaries and repository migration together.
The application rejects stale bundle identities; old collector processes need
to be replaced as part of that deployment. A failed migration should be
diagnosed and retried with the expected registry, without weakening the guard
or changing historical digests. Existing schedules and collected history are
preserved.

Ten live PostgreSQL cases verify fresh application catalog reconciliation,
the 79-to-80 upgrade, history and schedule preservation, guard rollback, trigger
enforcement, and repeat migration-runner behavior. The complete PostgreSQL
selection then passed 216/216 with no skips. Evidence:
`artifacts/revamp-collector-final/test-results/collector-final-postgresql.trx`.

The pre-existing M10 SQL reconciliation function compares exact digests only
for its first thirteen entries. The application checks all fifteen; tests do
not claim that direct SQL rejects stale replication digests.

## Nullable PostgreSQL alert results

Seven alert repository scalar reads now distinguish SQL `NULL` (`DBNull`) from
typed values and use their existing conservative fallback. Unexpected non-null
types retain the original cast failure. This is a defensive boundary repair:
the current migrated functions were not shown to return SQL `NULL` during an
ordinary delivery workflow.

Tests replace function bodies only inside disposable PostgreSQL databases,
preserving signatures, identities, ownership, security settings, and ACLs.
All seven injected NULL cases failed before the repair; eight typed/provider
controls passed. Afterward, all 15 boundary cases and 24 existing alert workflow
cases pass, with no skips. Evidence:

- `artifacts/revamp-collector-tests/results/alert-scalar-red.trx`
- `artifacts/revamp-collector-tests/results/alert-scalar-green-and-workflows.trx`

## Verification limits

Final canonical Local validation passed after the cancellation correction:
1,689 .NET tests and 147 frontend tests, with no failures or skips in the
selected suites. Compilation, TypeScript, production web build, asset checks,
and contract/checksum verification passed. Command:

```powershell
pwsh ./tools/validate.ps1 -Profile Local -TestResultsDirectory artifacts/revamp-collector-reviewed/test-results
```

Log: `artifacts/revamp-collector-reviewed-local.log`. The independent review's
single cancellation finding was corrected and its follow-up found no additional
actionable issue in that correction. The separate full PostgreSQL selection
above exercises repository behavior omitted by the Local profile.

These results cover .NET behavior and disposable PostgreSQL. The changed SQL
Server assets still require live SQL Server qualification. They do not establish
production certification, repair backup age or Agent event times, or replace the
remaining analytics, MCP, and refactor work in the review plan.
