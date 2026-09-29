# Single-host Windows lab deployment

## Status and intended use

This guide prepares one **non-production Windows Server lab** to host Microsoft
SQL Server, PostgreSQL, and the SqlObserver development processes. It is a
developer/operator handoff, not an installer, supported-release procedure, or
production security policy.

SqlObserver's M12 deployment, lifecycle, identity, signing, and platform cases
remain pending. In particular, this repository does not yet provide:

- a signed MSI/Burn installer or supported Windows service registration flow;
- a signed installer or supported production PostgreSQL migration executable
  (the repository includes only a source-based, lab-loopback migration host);
- an accepted secret-store, service-account, certificate, or TLS policy;
- a supported production reverse proxy/static-web deployment (the source-based
  lab host can serve a bounded `web/dist` snapshot on the authenticated API
  origin); or
- live release evidence for the single-host topology.

Do not describe a successful lab build as a supported deployment. Do not point
the lab at production SQL Server or PostgreSQL data.

## Lab topology

The smallest useful lab has these boundaries on one Windows Server 2022 or
2025 x64 host:

```text
Browser -> SqlObserver web preview
Browser/API client -> SqlObserver.Server (ASP.NET Core)
SqlObserver.Server -> PostgreSQL 18.x repository
SqlObserver.Collector -> PostgreSQL 18.x repository
SqlObserver.Collector -> local SQL Server 2019/2022/2025 target
```

PostgreSQL is SqlObserver's application repository. It is not an observation
target. Co-location is acceptable only for a lab: it removes failure and trust
boundaries that a production design must preserve.

Recommended lab names:

- host: `sqlo-lab-01.example.test`;
- PostgreSQL database: `sqlobserver`;
- PostgreSQL runtime logins: separate Server and Collector logins;
- Windows identities: separate Server and Collector lab service accounts;
- SQL Server observation target key: `lab-sql-01`.

## 1. Prepare the host

Install and patch the following before copying application artifacts:

- Windows Server 2022 or 2025 x64;
- a Windows SQL Server 2019, 2022, or 2025 Developer Edition instance;
- PostgreSQL 18.x (the automated repository suite pins PostgreSQL 18.4);
- .NET SDK/runtime 10.0.203 for a source-based lab deployment;
- Node.js 22.22.0 and pnpm 11.19.0;
- PowerShell Core 7.5 or newer; and
- Git.

Use fixed DNS names and correct system time. Keep SQL Server and PostgreSQL
listening on loopback or a lab-only interface unless a firewall rule is
explicitly required. Do not expose PostgreSQL, Kestrel, Vite, or SQL Server to
the public Internet.

Create a checkpoint or backup before repository bootstrap. The current
uninstall design preserves PostgreSQL data; there is no supported purge path.

## 2. Create identities before granting access

Use different Windows identities for the Server and Collector even when both
processes run interactively in the lab. The Collector identity is the identity
used for Windows-integrated access to the monitored SQL Server. SqlObserver
does not accept or store a SQL login password in the observation-target form.

Create a Windows group for interactive SqlObserver administrators and record
its SID. Server authorization uses SIDs, not caller-supplied role headers.

For PostgreSQL, create separate LOGIN roles outside the migration files. The
bootstrap/migration identity must be able to create the closed group roles and
schemas used by migration `0001`. After migration:

- the Server login is a member only of `sqlobserver_server`;
- the Collector login is a member only of `sqlobserver_collector`;
- neither runtime login is a superuser, database owner, migrator, or auditor;
- `PUBLIC` has no access to SqlObserver schemas; and
- Server and Collector use different credentials.

Do not put connection strings, passwords, fingerprint keys, or certificate
passwords in Git, command history, URLs, issue text, or pull-request logs.

## 3. Check out and verify the source

From an operator PowerShell session:

```powershell
git clone https://github.com/laminblake2025/SqlObserver.git
Set-Location SqlObserver
git switch main
git pull --ff-only origin main
git rev-parse HEAD
git status --short --branch
dotnet --info
node --version
pnpm --version
pwsh --version
```

The checkout must be clean before building. Confirm that the `Validate`
workflow succeeded for the exact `main` commit reported by `git rev-parse HEAD`
before using it on the lab host.

Restore and build from the checked lock files:

```powershell
dotnet restore .\SqlObserver.slnx --locked-mode --configfile .\NuGet.Config
dotnet build .\SqlObserver.slnx --configuration Release --no-restore
pnpm --dir .\web install --frozen-lockfile
pnpm --dir .\web build
```

Do not replace locked dependencies, relax warnings, or use an unreviewed
package source to make the build pass.

## 4. PostgreSQL repository gate

The migration catalog contains the exact, contiguous `0001` through `0208`
sequence and `database/migrations/checksums.sha256`. The embedded
`PostgreSqlMigrationPort` verifies those bytes, requires PostgreSQL major 18,
holds an advisory lock, validates the existing ledger as an exact prefix, and
commits ordinary migrations and their ledger rows in one transaction. Migration
`0088` builds a query-performance index concurrently outside a transaction;
the runner checks the index and records the ledger only after a valid build.
Migration `0106` uses the same guarded concurrent-index mode for fleet alert
reads; `0107` adds the bounded projection function granted to the Server role.
Migration `0108` rebinds the pinned M5 activity bundle after the wait collector
stops returning wait types whose cumulative task count is zero. It does not
rewrite historical wait snapshots or collector schedules.
Migration `0109` binds the current backup and availability-group assets after
their earlier source update, preserving historical runs and schedules.
Migration `0089` adds a nullable query-observation target key and fills it on
new inserts. Migrations `0090`–`0091` add a target-ordered index and a bounded
database backfill operation for historical observations. The backfill is not
run automatically by the migration runner.
Migration `0092` adds a concurrent target/time index. Migration `0093` lets
top-query ranking and observation RLS use the target key for populated rows,
while historical NULL rows retain exact query-ownership checks during backfill.
Migration `0094` applies the same keyed/legacy split to query history.
Migration `0095` adds a migrator-only fleet backfill call. It visits at most ten
incomplete targets per transaction and updates at most 1,000 historical rows
per target. It does not run automatically.
Migration `0096` adds a `NOT VALID` target-identity check. It rejects new NULL
observations without scanning historical rows during the migration. A later
cutover must validate the check and set the column `NOT NULL` only after fleet
backfill reports completion; neither step runs automatically here.
Migration `0097` keeps live-activity selection in ten-target batches but
prioritizes targets with no current-revision capture, then the oldest captures.
This rotates sampling across the fleet instead of repeatedly selecting the
same first ten servers.
Migration `0098` adds a collector-only, fenced live-activity claim. It records
the next due time with lease acquisition, so a completed capture releases its
worker slot immediately. Failed captures use repository-backed retry delays of
10, 20, 40, then 80 seconds; a successful cadence capture clears the backoff.
Migration `0099` claims one due collector schedule with `SKIP LOCKED` and a
fenced worker lease in the same transaction. The Collector fills free execution
slots continuously rather than waiting for a 16-item batch. Set
`SqlObserver__Collector__MaxConcurrency` to 1–64 to tune the global limit;
the default is 16.
Migration `0100` limits M10's scheduled partition creation to the four
currently written v2 streams and the diagnostic-event parent. Existing
partitions remain in place; inactive v2 parents are available for a future
explicit write cutover.
Migration `0101` records the attached canonical activity, wait, and blocking
partitions in `system.partition_registry` and registers future partitions as
the existing M5 maintenance functions create them.
Migration `0102` limits the analytics dependency hold in retention preview,
detach, and drop to jobs whose source windows overlap the partition. Jobs
without a usable window continue to hold retention until resolved.
Migration `0103` maps M5 activity and blocking policies to their written
partitioned parents and adds policies for request and wait partitions. It
refuses an upgrade if either legacy M5 policy has been configured; disable it
and clear its retention duration before upgrading so the target change is
reviewed explicitly. The new policies remain disabled until configured.
Migration `0104` permits a failed retention drop to retry after its recorded
one-hour backoff. It rechecks the policy revision, detached partition identity,
reader leases, analytics dependencies, and recovery attestation before dropping.
After 20 failed attempts the execution enters `failed` and requires operator
review; it does not retry indefinitely.
Migration `0105` adds a collector-only retention step with a fixed
`retention/maintenance` lease key. The worker runs one eligible detach or due
drop per transaction, records `system` audit activity, and checks the lease
again before commit. The existing 24-hour detach grace, policy revision,
reader lease, analytics dependency, and recovery-attestation gates still apply.
At this migration stage, policies remain disabled until a
SecurityAdministrator configures each data class.

Migration `0156` enables default retention for the four written M5 parents:
30 days for session activity, 14 days for requests and server waits, and 90
days for blocking edges. It changes only untouched disabled policies;
operator-revised policies keep their settings. A SecurityAdministrator can
adjust or disable these defaults. The worker still requires a valid recovery
attestation and honors partition minimums, reader leases, analytics
dependencies, and the 24-hour detach grace before a drop. Review the defaults
and attest recovery before allowing old partitions to be removed.
Migration `0157` applies the same guarded default policy to the original
partitioned evidence streams: 30 days for raw metric samples and 90 days for
diagnostic events. Previously configured policies are preserved, and a
SecurityAdministrator can change either policy through the retention API.
The typed deadlock summary has a foreign key to its diagnostic-event envelope;
PostgreSQL rejects a detach-first attempt for a populated month with `23503`.
Migration `0160` instead keeps diagnostic-event months attached and readable
during a 24-hour grace period. After the grace, the fenced worker rechecks the
policy revision, reader leases, analytics dependencies, minimum partition
count, and recovery attestation. It removes at most 10,000 typed deadlock rows,
then detaches and drops that exact event month in one transaction. A failed
step rolls back the row removal and leaves the month readable; after 20 failed
attempts the execution needs operator review. Policy changes cancel attached
grace without hiding data. See
[ADR-0021](../adr/ADR-0021-canonical-retention-graph.md).
Migration `0161` makes a SecurityAdministrator's `detach` request for a
diagnostic-event month start that same attached grace. The preview reports
`grace_in_progress` while it is pending, or `retention_failed` after the
bounded retry limit. The fenced worker performs the final drop; a manual
`drop` request for an attached-grace execution returns blocked. Other data
classes retain their detach-first path.
Migration `0158` enables defaults on the four M10 v2 streams that receive
writes: 30 days for host metrics and replication snapshots, and 180 days for
rollups and evidence packets. Operator-revised policies are preserved. The
other unused v2 parents remain disabled pending an explicit cutover or cleanup.
Migration `0162` enables guarded defaults on the actively written M9 daily
parents: 90 days for backup status and Agent failure occurrences, and 30 days
for Agent scan coverage and availability-group snapshots. Existing operator
revisions remain intact. The unpartitioned Agent failure identity remains
disabled. The TempDB snapshot/file pair has an incoming file foreign key and
needs a coordinated retirement step. A populated PostgreSQL
test confirms parent-only detach fails with `23503`; the child-first drop and
parent drop succeed together in one transaction. All enabled classes
still require recovery attestation, reader/dependency checks, a minimum
partition count, and a 24-hour detach grace before drop.
Migration `0163` installs the fenced paired TempDB worker, which keeps both
daily partitions attached through grace and drops the file partition before
the snapshot partition in one transaction. It excludes both from the generic
detach worker. Migration `0164` routes SecurityAdministrator snapshot requests
into that same attached grace, checks the matching file partition in preview,
and enables a guarded 30-day snapshot default. The subordinate file policy
remains disabled and direct file detach remains blocked. Both partitions
retire together after the grace period.

Migration `0165` begins the original M7 observation cutover with an
`interval_end`-partitioned shadow table. New observations mirror atomically;
daily partitions are registered and maintained, including older days reached
by a bounded source window. This temporarily increases M7 storage. Reads and
retention still use the original table until historical backfill and parity
checks support a separate cutover.

Migration `0166` provides `control.backfill_query_observation_v3(target, limit)`
for an explicit historical copy. Connect with a migrator login, set the exact
`sqlobserver.target_scope` for one target in the same transaction, and call it
with a limit from 1 to 1,000. Commit each batch and repeat until `complete` is
true. It reports rows checked and newly copied; already-mirrored rows count as
checked. A divergent row aborts the batch without advancing its cursor. The
copy does not change the read source or enable M7 retention. Full source and
successor count/digest parity is still required before a read cutover.
Migration `0167` adds
`control.verify_query_observation_v3_window(target, from_utc, to_utc, max_rows)`
for that check. Use the same migrator login and target scope after the backfill
reports completion. Verify every interval-end window across the target's
history; each call accepts at most one day and caps either side at 100,000
rows. Split denser days into smaller windows. Record both counts and SHA-256
digests, and investigate any `parity = false`. The function is read-only and
does not itself authorize a read cutover or an M7 retention policy.
Migration `0168` adds migrator-only shadow top-query and query-history
projections for old/new read comparison. The API still calls the original
functions. These shadow functions are not an operator-facing alternative read
path until every target and time window has passed parity and replay checks.
Migration `0169` adds a similarly private plan-metadata shadow reader. It
preserves the protected-plan-link availability check; the API still calls the
original plan-metadata function.
Migration `0170` adds a migrator-only parity sweep ledger. Call
`control.query_observation_v3_parity_coverage(target)` to get the UTC bounds
and first uncovered point, then record each day or smaller piece with
`control.record_query_observation_v3_parity_window(target, from_utc, to_utc,
max_rows)`. Each call stores counts and digests; a live insert makes its UTC
day's earlier checks stale. `all_covered` means the currently recorded,
generation-matched passing windows cover the observed bounds. Keep the API on
the original path until replay, fencing, and read cutover are reviewed.
Migration `0171` lists coverage for every registered target, including empty
targets, in pages of at most 100:

```sql
SELECT * FROM control.list_query_observation_v3_parity_coverage(NULL,100);
-- Pass the last instance_id as the first argument for the next page.
```

Use a migrator login. Complete `0166` backfill even for empty targets; otherwise
their fleet coverage is false. Migration `0173` can choose and record the next
bounded window for one target. Set that target's scope in the same transaction,
commit each passing window, and repeat until the function returns no row:

```sql
BEGIN;
SELECT set_config('sqlobserver.target_scope','<target-uuid>',true);
SELECT * FROM control.record_next_query_observation_v3_parity_window(
  '<target-uuid>'::uuid,100000);
COMMIT;
```

Investigate `parity = false` before retrying. SQLSTATE `54000` means more than
the row cap shares one `interval_end`; a time-only window cannot split that
point. Migration `0174` adds a bounded key-chunk verifier for that exact
timestamp. Use the timestamp returned as the uncovered point, and repeat this
transaction until `complete = true` and `parity = true`:

```sql
BEGIN;
SELECT set_config('sqlobserver.target_scope','<target-uuid>',true);
SELECT * FROM control.record_query_observation_v3_dense_point(
  '<target-uuid>'::uuid,'<dense-interval-end-utc>'::timestamptz,100000);
COMMIT;
```

The first incomplete result has a null `parity`. A false final result means
the old and successor rows differ; investigate it before cutover. The chunk
cursor and rolling SHA-256 digest survive between calls, while a new successor
or original-table insert invalidates the checkpoint for that UTC day. The
source-generation trigger is independent of the mirror, and activation
refuses to proceed if it is disabled. After a passing dense
point, resume `record_next_query_observation_v3_parity_window`. Migration
`0175` adds its original-table target/time/key index concurrently; allow time
for the index build on a populated repository. Live writes can stale the
current UTC day's windows, so rerun coverage immediately before activation.
Migration `0172` installs an M7 read route that remains on the original table
until a migrator calls `control.set_query_observation_v3_read_route(true)`.
That call locks target registration and observation writes, checks every
registered target and required triggers/RLS, and flips the three public
projections in the same transaction. `false` restores original reads while
the original table is intact. Neither command moves canonical writes or
enables M7 retention.
Migration `0176` adds a separate M7 writer gate, initially off. After v3
reads are active and every target still has passing parity coverage, a
migrator can run:

```sql
SELECT control.activate_query_observation_v3_writer();
```

Activation drains in-flight original-table writers and rechecks fleet
coverage under the same locks. Subsequent collector commits retain the
existing validation, accounting, protected text/plan links, and replay
contract. Their new observation rows are mirrored to v3, checked against
the committed count, and removed from the original table before commit.
The read route cannot return to the original table after this switch.
Treat writer activation as irreversible; verify v3 readers and backups
first. The writer switch alone does not retire pre-switch rows. The
original-to-v3 parity ledger ceases to describe new writes after activation.
Migration `0177` redirects the M7 retention policy from the unused v2
scaffold to the written v3 parent. On an untouched policy, writer activation
also enables a 90-day default. The existing fenced worker then detaches
expired v3 days and drops them after its 24-hour grace, subject to reader
leases, recovery attestation, policy revision and analytics holds. A
SecurityAdministrator can change the duration after activation; the minimum
enabled duration is eight days because accepted source windows can span seven.
The policy cannot be enabled before the writer switch. A policy previously
customized for v2 stays disabled after migration until reviewed and enabled
for v3. Expiring v3 days leaves run/query/plan and protected-content links
in place; their separate graph cleanup is still required.
Migration `0178` provides a scoped, migrator-only preview of holds for one
run. Set the target scope in the same transaction and inspect all returned
codes:

```sql
BEGIN;
SELECT set_config('sqlobserver.target_scope','<target-uuid>',true);
SELECT * FROM system.query_performance_run_retention_holds(
  '<target-uuid>'::uuid,'<run-uuid>'::uuid);
COMMIT;
```

It reports attached observations, detached v3 grace, reader leases,
incident evidence, analytics work, missing outcome or recovery evidence,
policy/age gates and unreviewed foreign keys. `no_known_hold` is a preview
result, not permission to delete: cleanup must recheck under its own fence.
Migration `0179` adds `system.retire_query_performance_run` for one expired
run once no attached or detached v3 evidence can depend on it. An operator
with the migrator role and a live `retention/maintenance` lease can call it
in a target-scoped transaction. It returns `held`, `oversize`, `retired`, or
`missing`; only `retired` removes the bounded graph. Check
`system.query_run_retirement` and the `retention.query_run.retire` audit
entry for the child counts, policy revision, recovery attestation and fence.
The function leaves collection-run outcomes and shared protected payloads
intact. Migration `0180` adds the run target/time index concurrently.
Migration `0181` makes the existing retention worker scan at most 32 targets
per lease cycle and retire at most one eligible M7 run graph. It stores the
last examined target/run and hold result in
`system.query_run_retention_scan_cursor`; a complete sweep revisits held
runs. `oversize` needs operator review because a run with more than 4,096
child rows is never deleted automatically. Inspect
`system.query_run_retention_blocked` for the durable target and run IDs.
Migration `0182` adds a daily partitioned collection-run successor. Existing
run inserts mirror into it atomically, including historical run dates, and
the collector's partition-maintenance step prepares the surrounding UTC days.
The original `telemetry.collection_run` remains the canonical run key for its
20 inbound foreign keys. Do not enable retention or switch readers to the
successor: historical backfill, parity, replay and every run-reference cutover
are still outstanding. A blocked or mismatched successor partition causes the
original insert to fail; inspect `system.partition_registry` and the matching
`telemetry.collection_run_v2_pYYYYMMDD` relation before retrying a failed run.
Migration `0183` builds the historical-run backfill index concurrently;
allow time for its guarded build on a populated repository. Migration `0184`
adds `control.backfill_collection_run_v2(target, limit)` for explicit
historical copy. Use the migrator role with
`sqlobserver.target_scope` set to that target, and call with a limit from 1
through 1,000 until it returns `complete=true`. Review
`system.collection_run_v2_backfill` for checked and copied counts. A completed
cursor covers the rows encountered by that scan; it does not establish
fleet-wide old/new parity or permit a reader or retention cutover.
Migration `0185` adds
`control.verify_collection_run_v2_window(target, collector, fromUtc, toUtc, maxRows)`.
Use the same migrator target scope after the backfill completes; each call
checks at most one day and 100,000 rows per side. Compare the `parity` result
and both counts and digests. A passing window alone is not a fleet or cutover
attestation; repeat for every target, collector and relevant window under a
final writer fence before changing readers or foreign keys.
Migration `0186` records verified windows with
`control.record_collection_run_v2_parity_window` and reports the first gap
through `control.collection_run_v2_parity_coverage`. Original and successor
inserts invalidate their target/day certificates. Sweep every target and
collector, revisit stale days, and perform a final writer-fenced verification;
the coverage report alone cannot authorize a cutover.
Migration `0187` adds
`control.list_collection_run_v2_parity_coverage(afterTarget, afterCollector, limit)`
for a migrator-only page of at most 100 target/collector pairs. Pass both
cursor fields as null for the first page, then use the last returned pair for
the next page. It includes registered targets with no runs and restores the
caller's target scope. Pages are a preliminary inventory, not a coherent
fleet certificate while registrations or writes continue.
Migration `0188` adds `control.verify_collection_run_v2_fleet_fenced(maxPairs)`.
Run it as migrator inside an explicit transaction after all target/collector
windows are covered. It locks the registration, writer and parity relations,
checks trigger/RLS/partition gates, and returns target, collector and pair
counts. The locks end at transaction commit. A later cutover must repeat this
check and change its routes and references before releasing those locks;
this preliminary function does not authorize retention or switch reads.
Migration `0189` adds `telemetry.collection_run_identity`, a global run-ID
bridge pointing to the partitioned successor. New successor inserts populate
it atomically and duplicate IDs across partitions fail. Existing successor
rows are not copied by the migration, and all 20 child foreign keys still
reference the original run table. Backfill and verify identity coverage
before changing any child reference; do not treat the new bridge as canonical
yet.
Migration `0190` adds `control.backfill_collection_run_identity(target, limit)`.
After the successor backfill completes for a target, call it under the
migrator role and matching target scope with a limit from 1 through 1,000
until `complete=true`. Review `system.collection_run_identity_backfill` for
checked and copied counts. Each batch verifies original/successor row equality
and identity timestamp; an error leaves its cursor unchanged. The cursor is
copy progress, not a fleet-wide identity or foreign-key cutover attestation.
Migration `0191` adds
`control.verify_collection_run_identity_window(target, collector, from, to, max_rows)`.
Call it under the migrator role and matching target scope after both backfills
complete. Each window must fit one UTC day and 100,000 successor rows. It
checks ordered run IDs and start times against the bridge under one snapshot;
a missing identity fails parity. This is a per-window check, not the fenced
fleet-wide gate needed before moving child foreign keys.
Migration `0192` extends the parity ledger and fenced fleet verifier to include
the identity bridge. Complete both target backfills and re-record every
target/collector window: older certificates have no identity parity and no
longer count as covered. The fleet check holds locks on the bridge, its
backfill state and the parity ledger, and rejects a disabled mirror or
append-only trigger, missing forced RLS, or an invalid bridge foreign key.
Keep its transaction open through any future child-FK cutover.
Migration `0193` adds
`control.stage_collection_run_identity_fk(child_regclass, max_pairs)` for an
operator to stage one of the 20 reviewed child tables per transaction. First
complete both backfills and identity-aware parity coverage for every target
and collector. The function repeats the fleet proof under a writer fence,
checks the original FK, then adds an identity-bridge FK as `NOT VALID`.
New child writes must satisfy both FKs; old FKs remain. Do not drop the old
constraints before validating every staged FK and repeating the final fleet
and reference-graph checks.
Migration `0194` adds
`control.validate_collection_run_identity_fk(child_regclass)`. Call it once
per staged child relation during a maintenance window; each call validates
historical rows and checks partition children. It returns `true` when it
newly validates the FK and `false` when that FK was already valid. This scan
does not hold the fleet writer fence, because the staged FK already enforces
new child writes. Keep every original FK until all 20 identity FKs are valid
and the final cutover repeats the fleet and graph checks under one fence.
Migration `0195` adds
`control.cutover_collection_run_identity_fks(max_pairs)`. Call it only after
all 20 identity FKs are staged and validated and every target/collector parity
window is current. It repeats the fleet proof, checks every old and new FK,
and removes all 20 old FKs atomically under the writer fence. A result of
`20` means this call completed the cutover; `0` means a recorded, intact
cutover was already present. Any partial graph raises an error and rolls back.
The original collection-run table remains the writer and reader after this
reference change; do not retire it yet.
Migration `0196` adds private M5 activity and blocking shadow readers on the
partitioned run successor. They are migrator-only comparison surfaces; public
routes still read the original run table. Verify populated page and cursor
parity before considering a coordinated reader cutover.
Migration `0197` adds the shared M5 reader route, initially set to the
original implementations. After backfill and current parity certificates
cover every target and collector, the migrator can call
`control.set_collection_run_v2_activity_read_route(true, max_pairs)` to
switch the four readers under the fleet writer fence. Passing `false` returns
them to the original readers while those rows remain available.
Migration `0198` adds private wait summary, wait history, and wait category
trend readers over the partitioned run table. Public wait routes remain on
the original readers pending a separate gated cutover.
Migration `0199` adds that separate wait-reader route. It remains on the
original implementations until the migrator calls
`control.set_collection_run_v2_wait_read_route(true, max_pairs)` after full
fleet backfill and current parity certificates. Passing `false` rolls back
the three wait readers while original rows are retained.
Migration `0200` adds private partitioned-run comparison views for M5
snapshot evidence and collector health. The public views remain unchanged;
collector health also feeds alerting, so check populated view parity and
dependent readers before any public view cutover.
Migration `0201` adds a default-original route inside the public collector
health view. After complete fleet backfill and parity certificates, the
migrator can call `control.set_collection_run_v2_health_read_route(true,
max_pairs)` to enable its partitioned source under the writer fence. Passing
`false` returns the public view to original runs while they remain available.
Migration `0202` adds a private partitioned-run M9 latest-header comparison
function. Public backup, Agent, TempDB, and availability-group headers still
read original runs until a later gated route is installed.
Migration `0203` adds that default-original M9 header route. After complete
fleet backfill and current parity certificates, the migrator can call
`control.set_collection_run_v2_m9_read_route(true, max_pairs)` under the
writer fence. Passing `false` restores the original header while its run
table remains available.
Migration `0204` makes the public M5 snapshot-evidence view follow the
activity-reader route. Its original source remains the default. Calling
`control.set_collection_run_v2_activity_read_route(true, max_pairs)` after the
fleet parity proof also switches the evidence view to partitioned runs;
passing `false` returns both to original runs.
Migration `0205` adds a private partitioned SQL volume reader for comparison
on populated first and cursor pages. The public volume reader remains on
original runs until its own guarded route is installed.
Migration `0206` adds the guarded public route, initially using original runs.
After full fleet backfill and parity certificates, the migrator can call
`control.set_collection_run_v2_sql_volume_read_route(true, max_pairs)` under
the writer fence. Passing `false` restores original-run reads.
Migration `0207` adds private partitioned-run overview workload and database
activity readers for populated parity checks. The public overview histories
still read original runs pending their own guarded route.
Migration `0208` adds one default-original route for both overview histories.
After complete fleet backfill and parity certificates, the migrator can call
`control.set_collection_run_v2_overview_read_route(true, max_pairs)` under the
writer fence. Passing `false` restores both original-run readers.

The focused production-writer replay test checks that a retry does not insert
another successor row or advance its day generation. A failed M7 commit leaves
both observation tables empty for that run.
The focused large-history ranking test compares both pages from the active
and partitioned top-query readers with 60,000 older observations; it does not
replace a production fleet performance check before cutover.

Migration `0159` restores a detached partition if its retention policy is
disabled or its revision changes during the 24-hour grace period. The fenced
worker reattaches one partition per step and records an audit event. If the
attach is blocked, it retries hourly; inspect the execution's `last_error`
and the `retention.partition.reattach` audit event to resolve a missing or
conflicting partition. The collector can no longer call the old drop entry
point directly.

Migration `0135` adds target-scoped, bounded server-wait history. Migration
`0136` creates its target/time ordering index across the written daily wait
partitions. The runner creates a metadata-only parent index, builds each child
index concurrently, then attaches it. An interrupted upgrade retains completed
child builds and resumes missing ones on the next migration attempt. The ledger
advances only after the parent index is valid. Budget enough migration time for
the number and size of retained wait partitions; each build uses the runner's
five-second lock and five-minute statement limits and can be retried safely.

Migration `0137` records a versioned wait-category summary when a successful or
partial `waits.server` outcome is first committed. It uses the preceding
comparable run of the same target revision; missing baselines and counter resets
remain unknown. Existing outcomes retain a NULL summary after upgrade. The
summary is stored alongside the immutable outcome. The server-only
`reporting.list_server_wait_category_trend` function aggregates these outcomes
into five-minute buckets without scanning raw wait partitions. It returns NULL
for empty buckets or any bucket with missing, partial, or incomparable evidence;
a partial run's loss evidence remains authoritative.

After upgrading, a migration administrator can backfill the fleet in bounded
transactions. Repeat this transaction until `complete` is `true`:

```sql
BEGIN;
SET LOCAL ROLE sqlobserver_migrator;
SELECT * FROM control.backfill_query_observation_identity_fleet(5,1000);
COMMIT;
```

For one target, replace the UUID in both places and repeat until `complete` is
`true`:

```sql
BEGIN;
SET LOCAL ROLE sqlobserver_migrator;
SELECT set_config('sqlobserver.target_scope','<target-uuid>',true);
SELECT * FROM control.backfill_query_observation_identity('<target-uuid>'::uuid,1000);
COMMIT;
```

After the fleet backfill reports `complete = true`, run the identity cutover in
one transaction. The function validates every historical observation before
requiring `instance_id` and replacing the temporary RLS owner join. If any
NULL identity remains, validation fails and the policy changes roll back.

```sql
BEGIN;
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SELECT control.finalize_query_observation_identity();
COMMIT;
```

On a large repository, a timeout leaves the prior read path in place. Retry
the cutover during a maintenance window after confirming the backfill is done.

**Do not run the SQL files individually with `psql -f`.** Doing so bypasses the
runner-owned migration mode, advisory lock, checksum validation, and
`system.schema_migration` ledger contract.

The source-based `SqlObserver.Cli` exposes this port only through one closed lab
command. It accepts no connection string, host, SQL text, migration path,
password argument, password environment variable, or password file. The target
is fixed at `127.0.0.1:5432`, database `sqlobserver`, and bootstrap login
`sqlobserver_bootstrap`. Transport is deliberately limited to cleartext
loopback on the co-located non-production lab; it is not a production TLS
decision or a supported deployment interface.

Create the bootstrap login and empty database with PostgreSQL's own tools. The
`--pwprompt` option keeps the new password out of command history. These
commands also prompt for the existing PostgreSQL administrator password when
needed:

```powershell
$pgBin = 'C:\Program Files\PostgreSQL\18\bin'

& "$pgBin\createuser.exe" `
  --host 127.0.0.1 --port 5432 --username postgres `
  --pwprompt --createdb --createrole --no-superuser --no-replication `
  sqlobserver_bootstrap
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap role creation failed.' }

# This fixed NOLOGIN role owns only the forced-RLS report expiry function.
# PostgreSQL requires an administrator that already has BYPASSRLS to create it.
& "$pgBin\psql.exe" `
  --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
  --no-psqlrc --set ON_ERROR_STOP=1 `
  --command 'CREATE ROLE sqlobserver_report_expirer WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS;'
if ($LASTEXITCODE -ne 0) { throw 'Report expiry role creation failed.' }

# The protected-payload cleanup function needs a separate owner to inspect
# references across forced target RLS without giving the collector table access.
& "$pgBin\psql.exe" `
  --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
  --no-psqlrc --set ON_ERROR_STOP=1 `
  --command 'CREATE ROLE sqlobserver_payload_expirer WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS;'
if ($LASTEXITCODE -ne 0) { throw 'Payload expiry role creation failed.' }

& "$pgBin\createdb.exe" `
  --host 127.0.0.1 --port 5432 --username sqlobserver_bootstrap `
  --owner sqlobserver_bootstrap sqlobserver
if ($LASTEXITCODE -ne 0) { throw 'Repository database creation failed.' }
```

Stop if any object already exists unexpectedly; inspect it rather than
changing or reusing it blindly. Then publish and run the exact lab host:

```powershell
$migrationHost = 'C:\SqlObserverLab\MigrationHost'
dotnet publish .\src\SqlObserver.Cli\SqlObserver.Cli.csproj `
  --configuration Release --no-restore --output $migrationHost
if ($LASTEXITCODE -ne 0) { throw 'Migration-host publish failed.' }

# PostgreSQL requires temporary ADMIN OPTION membership to transfer the
# forced-RLS functions to their fixed NOLOGIN owners. The migrations revoke
# these grants on success; this finally block also revokes them after failure.
try {
  & "$pgBin\psql.exe" `
    --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
    --no-psqlrc --set ON_ERROR_STOP=1 `
    --command 'GRANT sqlobserver_report_expirer TO sqlobserver_bootstrap WITH ADMIN OPTION;'
  if ($LASTEXITCODE -ne 0) { throw 'Temporary report expiry membership grant failed.' }
  & "$pgBin\psql.exe" `
    --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
    --no-psqlrc --set ON_ERROR_STOP=1 `
    --command 'GRANT sqlobserver_payload_expirer TO sqlobserver_bootstrap WITH ADMIN OPTION;'
  if ($LASTEXITCODE -ne 0) { throw 'Temporary payload expiry membership grant failed.' }

  & "$migrationHost\SqlObserver.Cli.exe" postgres migrate `
    --lab-loopback --allow-loopback-cleartext `
    --database sqlobserver --username sqlobserver_bootstrap
  if ($LASTEXITCODE -ne 0) { throw 'Repository migration failed.' }
}
finally {
  & "$pgBin\psql.exe" `
    --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
    --no-psqlrc --set ON_ERROR_STOP=1 `
    --command 'REVOKE sqlobserver_report_expirer FROM sqlobserver_bootstrap; REVOKE sqlobserver_payload_expirer FROM sqlobserver_bootstrap; DO $verify$ BEGIN IF EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles granted ON granted.oid=m.roleid JOIN pg_catalog.pg_roles member ON member.oid=m.member WHERE granted.rolname IN (''sqlobserver_report_expirer'',''sqlobserver_payload_expirer'') OR member.rolname IN (''sqlobserver_report_expirer'',''sqlobserver_payload_expirer'')) THEN RAISE EXCEPTION ''Expiry-role membership cleanup failed''; END IF; END $verify$;'
  if ($LASTEXITCODE -ne 0) { throw 'Expiry-role membership cleanup failed.' }
}
```

The executable prompts once with hidden input. A successful result is a single
secret-free JSON object with `status` equal to `succeeded`, zero failed
migrations, and the applied migration list. An already-current exact ledger is
also successful with an empty list. Any unknown, missing, reordered, or
checksum-mismatched ledger row fails closed. Save the JSON with the deployed
commit ID if lab evidence is required; it contains no credential or endpoint.

After migration, create distinct runtime logins with prompted passwords, then
grant each only its closed group role:

```powershell
& "$pgBin\createuser.exe" --host 127.0.0.1 --port 5432 --username postgres `
  --pwprompt --no-createdb --no-createrole --no-superuser --no-replication `
  sqlobserver_server_login
if ($LASTEXITCODE -ne 0) { throw 'Server login creation failed.' }

& "$pgBin\createuser.exe" --host 127.0.0.1 --port 5432 --username postgres `
  --pwprompt --no-createdb --no-createrole --no-superuser --no-replication `
  sqlobserver_collector_login
if ($LASTEXITCODE -ne 0) { throw 'Collector login creation failed.' }

& "$pgBin\psql.exe" --host 127.0.0.1 --port 5432 `
  --username sqlobserver_bootstrap --dbname sqlobserver `
  --no-psqlrc --set ON_ERROR_STOP=1 `
  --command 'GRANT sqlobserver_server TO sqlobserver_server_login; GRANT sqlobserver_collector TO sqlobserver_collector_login;'
if ($LASTEXITCODE -ne 0) { throw 'Runtime membership grant failed.' }
```

Do not grant either runtime login `sqlobserver_migrator`, CREATEROLE, CREATEDB,
superuser, replication, or bypass-row-security privileges. Keep the bootstrap
credential separate and use it only for reviewed migration runs.

The PostgreSQL integration suite may be run to validate the repository code in
an isolated disposable container, but its Testcontainers database is not the
lab application's persistent repository:

```powershell
dotnet test .\tests\SqlObserver.IntegrationTests.PostgreSql\SqlObserver.IntegrationTests.PostgreSql.csproj `
  --configuration Release --no-restore
```

This gate remains intentional for every environment except the explicitly
bounded single-host lab flow above. Do not synthesize ledger rows or grant
runtime accounts migration authority to work around it.

## 5. Build application artifacts

After the repository migration gate has an approved implementation, publish
the three processes into separate directories:

```powershell
$artifactRoot = 'C:\SqlObserverLab'
dotnet publish .\src\SqlObserver.Server\SqlObserver.Server.csproj `
  --configuration Release --no-restore --output "$artifactRoot\Server"
dotnet publish .\src\SqlObserver.Collector\SqlObserver.Collector.csproj `
  --configuration Release --no-restore --output "$artifactRoot\Collector"
dotnet publish .\src\SqlObserver.McpStdio\SqlObserver.McpStdio.csproj `
  --configuration Release --no-restore --output "$artifactRoot\McpStdio"
Copy-Item .\web\dist "$artifactRoot\Web" -Recurse
```

Keep the directories distinct. Do not give the Server access to the monitored
SQL Server, do not give MCP stdio a database credential, and do not give the
Collector the Server's PostgreSQL role.

## 6. Required runtime configuration

Server and Collector both require:

- `ConnectionStrings:SqlObserverRepository`, using their distinct PostgreSQL
  runtime logins; and
- `SqlObserver:IdentityFingerprintKey`, the same 32-byte secret represented as
  exactly 64 hexadecimal characters.

The Server additionally requires bounded SID-based authorization under
`SqlObserver:Authorization`. A lab administrator binding resembles this
configuration shape:

```json
{
  "SqlObserver": {
    "Authorization": {
      "Bindings": [
        {
          "GroupSid": "S-1-5-21-REPLACE-WITH-LAB-GROUP-SID",
          "Roles": ["TargetAdministrator"],
          "AllTargets": true,
          "TargetIds": []
        }
      ],
      "DisabledActorSids": []
    }
  }
}
```

Role names are case-sensitive application enum values. Keep configuration files
outside the repository, restrict their NTFS ACLs to the owning identity and
administrators, and never commit populated copies. The final secret store and
certificate policy are still owner decisions under ADR-0017; an ACL-restricted
lab file is not a production recommendation.

The Server requires authenticated HTTPS outside the contract-test environment.
Use a certificate whose subject/SAN matches the browser hostname and whose
chain is trusted by the lab client. Do not use certificate-validation bypasses,
plain HTTP fallbacks, or caller-supplied identity headers.

## 7. SQL Server least-privilege preparation

Create the Collector Windows login on the lab SQL Server, but do not grant
`sysadmin`. Generate the reviewed permission plan offline:

```powershell
pwsh .\tools\generate-permissions.ps1 `
  -SqlServerMajorVersion 16 `
  -Principal 'LAB\SqlObserverCollector' `
  -Operation Grant `
  -OutputPath .\artifacts\lab-sql-permissions.sql
```

Use major version `15` for SQL Server 2019, `16` for SQL Server 2022, and `17`
for SQL Server 2025. An authorized DBA must review and execute the generated SQL
independently. The script does not connect to SQL Server and does not create the
Windows login.

Only request the optional replication plan when replication is configured and
the distribution database has been explicitly identified.

## 8. Authenticated lab web interface

`pnpm --dir web build` produces deterministic static assets in `web/dist`.
Copy that completed build to a versioned, administrator-owned directory. The
Server loads one bounded immutable snapshot at startup and serves only
`index.html` and allowlisted files below `assets/`; source maps and unknown
paths are not served.

```powershell
$commit = (git rev-parse --short=7 HEAD).Trim()
$webRoot = "C:\SqlObserverLab\Web-$commit"
if (Test-Path -LiteralPath $webRoot) {
  throw "Refusing to replace existing web root: $webRoot"
}

Copy-Item -LiteralPath .\web\dist -Destination $webRoot -Recurse
```

Protect the copied directory from writes by the Server service account. Grant
that identity read and execute only; retain full control only for SYSTEM and
Administrators. Then append the absolute web-root setting to the Server service
command line while retaining its existing content root:

```powershell
$serverExecutable = "C:\SqlObserverLab\Server-$commit\SqlObserver.Server.exe"
$serverContentRoot = 'C:\SqlObserverLab\Config\Server'
$serverCommand =
  "`"$serverExecutable`" --contentRoot `"$serverContentRoot`" " +
  "--SqlObserver:WebRootPath `"$webRoot`""

sc.exe config SqlObserverServer binPath= $serverCommand
Restart-Service SqlObserverServer
```

The configured path must be absolute. Startup fails closed for a missing,
empty, oversized, hidden, system, or reparse-point build. The web endpoints use
the same HTTPS listener, Negotiate authentication, fallback authorization, and
origin as `/api`; no CORS or identity-header bypass is required. Verify from a
domain or explicitly authorized lab browser:

```powershell
$response = Invoke-WebRequest `
  -Uri 'https://SERVER-DNS-NAME:5443/' `
  -UseDefaultCredentials
if ($response.StatusCode -ne 200 -or $response.Content -notmatch '<title>SqlObserver</title>') {
  throw 'Authenticated web interface verification failed.'
}
```

If `SqlObserver:WebRootPath` is omitted, `/` continues to return the service
descriptor and the web interface is disabled. `/api/v1/service` always returns
that descriptor. Do not expose Vite, add an unauthenticated proxy, bypass the
certificate, or grant the Server write access to the copied web build.

With Windows authentication, `GET /health` returns `alive` when the Server
process is responding. `GET /ready` checks PostgreSQL compatibility through the
configured repository connection and returns `ready` (HTTP 200) or `not_ready`
(HTTP 503). Neither endpoint reports SQL Server target health. The readiness
response is not cached and does not expose repository version or connection
details.

## 9. Register a target after all gates pass

When PostgreSQL migrations, API hosting, authentication, HTTPS, and the
Collector identity are working, register the local SQL Server through the web
form:

- stable key: `lab-sql-01`;
- display name: a human-readable lab name;
- Windows host: the certificate-valid DNS name;
- exactly one of TCP port or named instance; and
- certificate host name when it differs from the connection hostname.

Registration records configuration and requests discovery. It does not grant
SQL permissions. The expected initial state is `Pending discovery`; the
Collector then records explicit supported, degraded, unsupported, unreachable,
authentication-failed, TLS-failed, or timed-out evidence.

## 10. Smoke checks and stop conditions

Before collecting data, verify:

- Server and Collector use different PostgreSQL roles;
- the Collector login is not SQL Server `sysadmin`;
- PostgreSQL and SQL Server are not publicly exposed;
- browser-to-Server traffic uses validated HTTPS and Windows authentication;
- the repository ledger is the exact checksum-verified migration prefix;
- no secret appears in logs, command history, Git status, or web responses;
- registration reaches the intended target and remains target-scoped; and
- stopping either process produces visible unavailable/degraded state rather
  than a fabricated healthy result.

Stop and investigate on any checksum mismatch, unexpected migration ledger row,
certificate error, identity ambiguity, authorization denial, unsupported
server version, target write requirement, or request to weaken TLS/permissions.

## 11. Current completion boundary

Host preparation, source verification, locked builds, isolated integration
tests, and offline SQL permission generation are available now. Persistent
repository migration, supported service installation, secrets/certificate
provisioning, and production web hosting remain explicit M12 deployment
blockers. The authenticated same-origin source-based web host is lab-only.
Resolve the remaining gaps through accepted ADRs and reviewed code;
do not fill the gaps with ad hoc production procedures.
