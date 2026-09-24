# Analytics correctness repairs — 2026-09-24

This batch repairs baseline persistence, abandoned analytics jobs, maximum-size
rollup pages, storage forecasts, and alert evaluation across targets. It builds
on collector checkpoint `0af7c517aab335d0a099b52afaaac6003de53bf8`.

## Baseline identity, job recovery, and pagination

Forward migration `0081_analytics_identity_reclaim_paging.sql` preserves each
baseline's hour of week in its uniqueness constraint and insert conflict target.
The former six-column primary key collapsed 168 computed hourly rows into one.
A seven-column `UNIQUE NULLS NOT DISTINCT` constraint retains both distinct
concrete hours and one legacy unknown-hour row. Existing rows are not rewritten,
and the append-only trigger remains enabled. Unexpected foreign-key or replication
identity dependencies cause the migration to fail and roll back.

Both production claim functions now recognize the owner and fencing token of
a running job's lease. Acquiring a replacement lease therefore permits recovery
of abandoned work. Existing attempt limits, terminal failures, cursor state,
target revision checks, permissions, and bounded claims remain in force. The
obsolete rollup-only claim function remains denied to runtime roles.

The rollup reader requests one extra internal row even at the public maximum
of 100,000. Its SQL function accepts the 100,001-row probe; public query and
writer limits are unchanged. Tests read all 100,001 rows across two pages and
check ordering, duplicates, omissions, snapshots, revision and generation fences.

The first behavioral PostgreSQL run had 10 failures and 7 passing controls.
After the repair, 22 new cases and 15 existing M10 workflow cases passed.
The upgrade-placeholder test was excluded from the initial run and bound to the
real migration before the passing run. Evidence:

- `artifacts/revamp-collector-tests/results/analytics-identity-reclaim-paging-red.trx`
- `artifacts/revamp-collector-tests/results/analytics-identity-reclaim-paging-green.trx`

Previously discarded baseline hours cannot be recovered by replaying the same
immutable operation. They require new derivation work. This does not redesign
retention or change the legacy read model's NULL-hour-to-zero behavior.

## Forecast history, horizon, and volume lookup

A large decrease in a gauge, such as free disk space, now remains in forecast
history. Magnitude-based reset detection applies only to counters; explicit
reset markers still start a new segment. Minimum history, confidence, capacity,
and dimension checks are unchanged. Both gauge regressions failed before the
fix; all six gauge/counter/reset cases pass afterward.

Forward migration `0082_scheduled_forecast_horizon.sql` gives newly scheduled
forecast jobs an explicit 30-day horizon from their source cutoff. Other job
kinds retain NULL horizons. The worker uses the same default for legacy queued
jobs, while preserving explicit horizons. Previously its one-day fallback ended
at today's midnight because the source cutoff was yesterday, making the result
immediately ineligible for the forecast reader.

The real scheduler-to-reader test exposed a second defect: the older forecast
reader hashed PostgreSQL's whitespace-containing JSON serialization, while the
writer stored a compact-JSON digest. The migration makes that reader compare
the stored JSON dimensions, matching the newer paginated reader. Named volumes
are now visible without mixing volumes or targets.

Evidence includes a genuine worker default failure with three explicit-horizon
controls, a missing scheduler horizon failure, and then an empty-reader failure
after the scheduler was corrected. The passing PostgreSQL test schedules and
claims a real job, reads 28 daily rollups and volume capacity, computes and stores
the forecast, and retrieves it through the server repository. Upgrade tests
preserve queued jobs, function ownership, ACLs, security settings, and signatures.
The focused PostgreSQL selection passes 3/3. Evidence:

- `artifacts/revamp-forecast-horizon/worker-horizon-red.trx`
- `artifacts/revamp-forecast-horizon/scheduler-horizon-red.trx`
- `artifacts/revamp-forecast-horizon/scheduler-horizon-first-green.trx` (reader regression)
- `artifacts/revamp-forecast-horizon/scheduler-horizon-upgrade-green.trx`

Test setup required compile-only fixture corrections before its executable
PostgreSQL RED run. Those are not counted as behavioral failures. Completed old
forecasts are preserved; newly scheduled daily work supplies the corrected horizon.

## Alert evaluation

Failures while evaluating one target now allow other targets in the claimed
batch to continue. A target-local timeout receives the same isolation. Caller
cancellation or lease loss stops subsequent targets and persistence, including
when an adapter returns after cancellation without throwing. Source-claim
failures remain cycle-wide, and exact lease release remains in the existing
cleanup path.

Tests cover failures in rule lookup, maintenance lookup, prior-state lookup,
and persistence; observation ordering and evolving state; scope, claim, and
fencing identity; caller cancellation; and real lease-renewal loss/failure.
The initial run had 9 failures and 5 passing controls; all 14 cases now pass.
Evidence is in `artifacts/revamp-forecast-alert-isolation/`.

Alert clear confirmation and hysteresis remain separate work. These tests do
not assert that a failed target's evaluations succeeded.

## Verification

The complete unit and security suites pass 676 and 130 cases respectively,
with no failures or skips. Full PostgreSQL validation passes 240/240 with no
skips. Evidence: `artifacts/revamp-analytics-reviewed-postgresql.log` and
`artifacts/revamp-analytics-reviewed/test-results/analytics-reviewed-postgresql.trx`.

The first full PostgreSQL run passed 239/240; its only failure was the older
79-to-80 upgrade test assuming there could be no later migrations. The fixture
now verifies the exact remaining catalog migrations, preserved collector history,
and then an empty repeat. The focused correction and full rerun both pass.
The first Local attempt also caught the installer assessment schema's missing
migration names. Its enum and checksum were corrected, and the focused static
checks pass 3/3. These failures and reruns are retained in the artifacts.

Independent review found no actionable defects in the production changes or
regressions, including the upgrade-fixture follow-up. Checksum closure verifies
all 82 migration names in both the manifest and assessment enum, 12 report
execution assets, and 41 SBOM inputs; eight ContractOnly cases pass.

Final canonical Local validation passes 1,713 .NET tests and 147 frontend tests,
with zero failures or skips in the selected suites. Build, TypeScript, production
web assets, and contract/checksum checks pass. Command and evidence:

```powershell
pwsh ./tools/validate.ps1 -Profile Local -TestResultsDirectory artifacts/revamp-analytics-reviewed/test-results
```

Log: `artifacts/revamp-analytics-reviewed-local.log`. The separate live PostgreSQL
run above covers repository behavior omitted from the Local profile. No live SQL
Server or production certification claim is made by this batch.
