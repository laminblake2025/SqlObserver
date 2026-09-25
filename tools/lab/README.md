# Disposable SQL Server workload

`verify-sql-volume-capacity-local.ps1` performs a bounded, read-only probe of
`sys.dm_os_volume_stats` for files visible on a SQL Server 2019/2022/2025
instance. It prints aggregate coverage and the minimum free capacity across
files, without paths or volume IDs. Repeated files on one volume are not
summed. For example:

```powershell
pwsh ./tools/lab/verify-sql-volume-capacity-local.ps1 -SqlInstance '.\SQLEXPRESS'
```

The [volume-capacity contract](../../docs/technical/SqlObserver-Sql-Volume-Capacity-Contract.md)
defines the separate collector, identity and repository cutover needed before
these values can appear in Resources.

For a local SQL Server 2022 instance, `verify-query-store-text-local.ps1`
creates a uniquely named database, runs a small Query Store workload through
the pinned SQL Server 16 metadata, text, and plan assets, checks that the workload
row and bounded content are returned with an interval ending no later than collection,
then holds a five-second lock on its own table to confirm Query Store attributes a
positive lock-wait category to the blocked plan. It verifies the same quiet-plan
total is returned on a second read and reconciles that total to Query Store's
interval/execution-type groups. It drops the database in `finally`.
Run it only with an instance where the
current Windows login can create databases:

```powershell
pwsh ./tools/lab/verify-query-store-text-local.ps1 -SqlInstance '.\SQLEXPRESS'
```

## Controlled stress and Observer validation

`run-stress-test.ps1` is the bounded, operator-run stress harness. It adds staged
2/4/8/16-worker workloads, deterministic connection-count alert episodes, local
Event Log delivery checks, maintenance suppression, and recovery evidence. It
does not stop SQL Server or Observer, change collector intervals, or send external
notifications. See [the stress validation runbook](../../docs/technical/SqlObserver-Controlled-Stress-Test-2026-09-08.md)
for prerequisites, commands, acceptance criteria, and the current lab blocker.

Run `test-stress-harness.ps1` to exercise guards, process ownership/cancellation,
deadline handling, atomic journals, and retryable cleanup without SQL load.

Restricted by host guards to WIN-QNGOV5GDM24. These are operator-run fixtures; collectors remain passive. Use the VM administrator account and validated SQL Server TLS.

## Setup and run

Copy this directory to the VM, then run in PowerShell:

```powershell
$sqlcmd = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE'
& $sqlcmd -S tcp:WIN-QNGOV5GDM24,1433 -E -N -b -t 120 -i .\setup-synthetic-workload.sql
.\run-synthetic-workload.ps1 -Scenario All
```

Setup is repeatable and retains existing fixture tables. Sales contains 2,000 customers, 40,000 orders, 80,000 order lines and four lock rows. Warehouse contains 80,000 inventory rows. All names and transactions are synthetic. Sales enables Query Store with a 128 MB cap; Warehouse disables it to exercise plan-cache fallback. Both use SIMPLE recovery.

The runner takes about four minutes and has a 330-second outer deadline. Individual scenarios are Traffic, Blocking, Deadlock, Backups and Agent. It starts SQL Server Agent if necessary. Each run saves stdout, stderr and result.json beneath C:\SqlObserverLab\Workloads. A successful Agent scenario means the deliberately failing job was launched; confirm its expected error 51042 in job history or the dashboard.

Blocking and deadlock scenarios use transactions that roll back. The runner owns its SQLCMD children and terminates those children on failure. Disconnect rolls back their open transactions. The Agent job is named SqlObserver Synthetic Failure and has no schedule or notifications. No recurring workload is installed.

Backup runs create uniquely named COPY_ONLY compressed, checksum-verified files in the SQL Server default backup folder and execute RESTORE VERIFYONLY. This validates backup readability, not a full recovery drill. Repeated runs retain backups and history; monitor disk space and remove only explicitly identified synthetic backup files when no longer needed. Databases, job and evidence remain available after a run. Removing the two SqlObserverLab databases or the named job is optional operator cleanup, and removes their retained test data.

Allow at least one five-minute collection interval after a run. Validate Health, Activity, Query performance (both source filters), Deadlocks and Operations at https://win-qngov5gdm24:5443/. Availability Groups are unsupported on this single-instance fixture. These workloads are functional smoke tests, not production capacity or release certification.

## Real transactional replication

Install the SQL Server Replication feature from the VM's existing `C:\SQL2025\StdDev_ENU` media if absent, then restart SQLSERVERAGENT to load its replication subsystems. Run `setup-replication.ps1` as the lab administrator. It creates SqlObserverLabPublisher, SqlObserverLabSubscriber and SqlObserverLabDistribution, one OrderEvents article and a continuous push subscription on the same SQL instance. SQL Agent's service identity runs the replication jobs; the observer login receives only the explicit monitoring reads. The snapshot-directory ACL applies only to the replication job identity.

The application needs an explicit distribution database binding for the target's current revision and a capability refresh after permissions change. Host and replication bindings do not automatically carry across target revisions. Migration 0036 preserves historical revision identities so existing evidence no longer blocks rediscovery.

Run `validate-replication.ps1` after initial convergence. It stops only the named fixture's distribution agent, inserts 1,000 synthetic orders, waits 150 seconds for collection, and always restarts the agent in `finally`. It asserts a stopped agent, 1,000 pending commands and unavailable latency, then verifies an empty queue, healthy status, subscriber row count and data equality. Evidence is saved under `C:\SqlObserverLab\Workloads\replication-*`. Runtime is approximately five minutes, with bounded SQL timeouts and a three-minute recovery deadline.

`replication-traffic.sql` submits 400 bounded row updates over 20 seconds to observe normal delivery latency after a backlog test. The latency value describes observed delivery, not a continuously measured end-to-end lag. The dimensionless Overview series uses the worst subscription latency per run; buckets average those observations. Detailed status retains individual observations. This single-host topology does not validate network partitions, multiple subscribers, Availability Groups or production scale.

Setup follows Microsoft's [replication installation guidance](https://learn.microsoft.com/en-us/sql/database-engine/install-windows/install-sql-server-replication?view=sql-server-ver17) and [push subscription procedures](https://learn.microsoft.com/en-us/sql/relational-databases/replication/create-a-push-subscription?view=sql-server-ver17). Source metric units follow [MSdistribution_history](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/msdistribution-history-transact-sql?view=sql-server-ver17).
