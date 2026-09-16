# Replication, host reporting and activity history validation

Date: 2026-09-05. Environment: disposable WIN-QNGOV5GDM24, SQL Server 2025 Standard Developer and PostgreSQL 18. This report follows the earlier activity/host validation and the separate Overview redesign implementation.

## Delivered and deployed

The lab now runs a real transactional replication publisher, distributor and continuous push subscriber. The collector reports pending commands in commands and delivery latency in seconds. Stopped distribution jobs report `disabled` (displayed as **Stopped**) with unavailable latency instead of a false healthy/zero-latency result. Queue values are summed across article status rows; command counts are no longer projected as byte counts.

The redesigned Overview from the other UI thread is deployed. Its server selection, time windows, comparison controls and chart layout are preserved. Host CPU and replication time series now load through the shared metric endpoint. Dimensionless replication series use the worst subscription latency and total pending commands per collection; the Overview subsequently averages observations into chart buckets. The dropdown names the worst-subscription latency explicitly.

The replication evidence table uses UTC observation time, agent state, pending commands, latency in seconds, coverage and expandable evidence. Historical missing-binding rows retain their unavailable values and a history-specific explanation. Old observations from the initially incorrect collector remain immutable; the corrected pause scenario begins at 15:49 UTC.

Target rediscovery now advances revision without historical M10 foreign keys blocking the update. The lab is at target revision 2, with the existing explicitly identified host and distribution database rebound to that revision. Earlier host observations remain stored; current scoped host and replication reads use the current revision. Activity history still displays earlier blocking evidence.

Live application: `https://win-qngov5gdm24:5443/`; target `dfb2b72c-4ad4-4765-9523-528c798482f0`.

Deployed directories under `C:\SqlObserverLab`:

- `server-replication-overview-v3-20260905`
- `collector-replication-overview-v2-20260905`
- `web-replication-overview-v3-20260905`

Server, collector, SQL Server and SQL Server Agent services were all running at final readback. Existing service configuration and credentials remain external to these bundles. The monitoring login remains non-sysadmin.

## Database migrations

Applied migration history is preserved. The combined deployment includes migration 35 from the Overview thread and these forward migrations:

| Migration | Purpose |
| --- | --- |
| 36 | Durable target revision identities for historical M10 foreign keys |
| 37 | Activate the corrected replication collector asset bundle |
| 38 | Add replication metrics to the shared metric series function |
| 39 | Preserve collection-time snapshot fencing and omit incomplete aggregate values |
| 40 | Grant schema USAGE so existing analytics function EXECUTE grants are reachable |

Migration 40 applied at 16:47:36 UTC, checksum `13da02314d3d7c5d6f5bf38097f23d21addd5368dae161c2a5576aa5e0114df5`. Schema USAGE grants no direct table access or object creation. Integration tests verify collector base-table denial and scoped rollup reads. Migration 39 followed a failing snapshot-fence regression check; migration 38 was not rewritten after application.

## Real replication validation

Fixture databases are `SqlObserverLabPublisher`, `SqlObserverLabDistribution` and `SqlObserverLabSubscriber`. Publication `SqlObserverLabOrders` replicates `dbo.OrderEvents`. SQL Server replication components were installed from the existing SQL 2025 media. SQL Server Agent runs the replication jobs; the observer has the specific distribution/msdb reads required by the passive collector.

The automated validation pauses only the fixture distribution job, adds 1,000 orders, records SQL and API evidence, then restarts the job in a `finally` block and checks convergence.

| Stage | Publisher rows | Subscriber rows | Pending commands | Observed state |
| --- | ---: | ---: | ---: | --- |
| Before corrected scenario | 2,000 | 2,000 | 0 | Healthy |
| Paused for 150 seconds | 3,000 | 2,000 | 1,000 | Stopped; latency unavailable |
| Recovered | 3,000 | 3,000 | 0 | Healthy |

The API captured the stopped state at 15:49:53, 15:50:53 and 15:51:54 UTC, and healthy recovery by 15:52:54 UTC. Publisher/subscriber data comparison returned zero differences. The approximately 155-second delivery latency immediately after recovery describes the delayed delivered batch. A subsequent bounded workload updated 100 rows four times; the browser captured a normal delivery latency of 5.006 seconds at 16:36:43 UTC. Latency is the replication agent's observed delivery statistic, not a continuously increasing queue-age measurement; idle observations can retain prior values or report zero.

Final SQL readback again returned 3,000 rows on each side, zero differences, zero pending commands and collector sysadmin membership 0.

Scripts: `tools/lab/setup-replication.ps1`, `setup-replication.sql`, `validate-replication.ps1`, and `replication-traffic.sql`. Usage and prerequisites are in `tools/lab/README.md`. Existing Sales/Warehouse dummy databases are preserved.

## Validation evidence

- PostgreSQL 18 container integration suite: all 16 M10/M11 tests passed, including real restricted-role rollup commit/replay, current revision fencing, host/replication series, and collection snapshot fencing. Test fixtures were corrected to use a single target creation timestamp, UTC dates, administrator-only inspection connections and an explicit forecast dimension hash.
- Browser validation against the VM: compact replication history shows stopped/backlog/recovered observations; host CPU and replication latency render in the redesigned Overview. Activity's 24-hour history retains the nine 11:31–11:33 UTC blocking observations and identifies them as resolved, while current collection succeeds.
- Canonical Local validation passed after migration 40: 1,086 backend tests, 107 frontend tests, type checking, production build and asset verification. Results are recorded in `TestResults/replication-phase/validate-complete.txt`. External certification lanes are excluded; the VM SQL/API/browser checks above were performed separately.
- `git diff --check` passed.

Evidence directory: `TestResults/replication-phase`. Corrected workload JSON is under `validated-run/`; the VM originals are under `C:\SqlObserverLab\Workloads\replication-20260905-154937`. Browser DOM captures include `browser-replication.txt`, `browser-activity.txt` and `browser-overview-final.txt`; screenshots accompany replication and Overview captures. Integration output is `m10-m11-integration.txt`.

## Remaining project work

This establishes functioning lab replication and host reporting, not complete product release certification. Separate-host replication, network failures, multiple subscribers per publication, additional SQL versions, availability groups and sustained production-scale load remain unvalidated. In particular, publication-only topology identity needs review before claiming multiple-subscriber support. This single-instance fixture cannot validate availability-group failover.

Activity reads remain bounded to 25 rows and need deeper pagination for larger investigations. Installer, signing, supply-chain and remaining external certification lanes are still separate work. The existing NTLM discovery degradation remains visible. Historical rows produced before fixes are retained as evidence and should not be mistaken for results from the corrected collector.

## Subsequent history pagination

The 25-row history navigation limitation is now addressed by [Activity history pagination](SqlObserver-Activity-History-Pagination-2026-09-05.md), deployed and browser-validated against 38 observations across two pages. Existing history window limits remain.
