# Staged backup v2 source checks

The `backups.status` v2 assets are embedded under `M9BackupV2Assets` and verified
as an exact five-file SHA-256 bundle. The separate closed manifest schema v7
pins source limits, conditional AG permissions and output version 2. The active
M9 registry, v1 bundle, schedules and database contracts still select v1.

The SQL source emits one inventory row and up to five latest classified backup
slots per visible database except TempDB. Inventory remains when history is
absent or identity is unknown. History joins the current database GUID, family
and last recovery fork, not the database name. Duplicate database GUIDs are
explicitly ambiguous and do not acquire history. Copy-only full and log slots
remain separate; copy-only differential or unknown-copy markers disclose an
unclassified-record flag. Unresolved finish times sort ahead of known times in
their slot, so an older known timestamp cannot hide uncertainty.

Both streams retain recovery model, catalog state, AG logical identity, local
replica and role, and nullable preference. Any AG identity marker keeps visibility
local-only even if the group-database identity is incomplete. Standalone is
asserted only when the source observed a non-AG database; unknown HADR context
remains unknown. Nothing aggregates history from another replica in this stage.

## Source parameters and shape

Commands have a five-second timeout. The future collector must use a shared
five-second deadline for connection, query and parsing; passing these SQL tests
does not implement that runtime deadline. Production bounds are 512 databases,
3072 rows and 4 MiB. Supply `@maximum_databases=513` and `@maximum_rows=3073` to
disclose one lookahead for each limit. SQL rejects values outside 1–513 and
1–3073. Retain the 35-day history lower bound and propagate either lookahead as
loss rather than treating the prefix as complete.

The pinned result has 29 columns in the order below. Inventory is `row_kind=0`
and has NULL backup fields. Backup rows are `row_kind=1`, ordered after their
inventory row by kind and copy-only flag. Overall order is catalog database ID,
row kind, backup kind, copy-only. The catalog ID is an ephemeral association key,
not a durable policy identity.

| Ordinal | Fields | SQL type / semantics |
|---|---|---|
| 0–1 | row_kind, catalog_database_id | int |
| 2–7 | database, family, recovery-fork, group, AG-database, local-replica fingerprints | nullable binary(32); source digests, not published keyed identities |
| 8 | database_identity_known | bit; false for NULL or duplicate GUID |
| 9–10 | recovery_model_desc, catalog_database_state_desc | nullable nvarchar(60); catalog state is not an AG health verdict |
| 11 | visibility_scope | smallint: 0 unknown, 1 standalone, 2 AG local-only |
| 12–13 | local_replica_role_desc, local_replica_preferred | nullable nvarchar(60), bit |
| 14–15 | backup_kind, copy_only | nullable int, bit; full=1, differential=2, log=3 |
| 16–17 | source_local_start, source_local_finish | nullable datetime; offset-free source clock |
| 18 | recorded_start_offset_minutes | nullable smallint; valid backupset offset × 15, unknown stays NULL |
| 19–22 | size_bytes, has_checksum, is_damaged, metadata_complete | nullable bigint, bit, bit, bit |
| 23–24 | backup_first_recovery_fork_fingerprint, backup_last_recovery_fork_fingerprint | nullable binary(32) |
| 25 | source_observed_utc | datetime2(6); SQL source's UTC clock, not receipt time |
| 26 | source_time_zone_id | nullable nvarchar(128); current zone ID on majors 16/17, NULL on 15 |
| 27 | history_lower_bound_local | datetime; source-local lookback bound |
| 28 | unclassified_backup_seen | bit; invalid/unknown slot metadata requires degraded coverage |

The parser must re-key source digests by identity class, target and revision,
validate lineage and all closed tokens, retain unknown identities without name
fallback, preserve source accounting, and withhold invalid output. It must not
pass local-only AG history as all-replica evidence to the freshness evaluator.

## Typed parser and historical time resolution

`SqlServerBackupV2Parser` now reads all 29 fields in ordinal order, checking
the inventory/sample association, unique ordered slots, closed metadata tokens,
matching recovery lineage and one shared source clock/lookback/zone context.
Published fingerprints use the configured identity key, distinct identity
classes, target and target revision. Ephemeral catalog IDs and unkeyed source
digests do not leave this boundary. An unknown or duplicated database GUID
retains an inventory entry with no policy fingerprint or freshness assessment
input; it does not borrow another identity or fall back to a name.

Incomplete inventory, offline state, unknown recovery model, unclassified
backups and incomplete AG context retain partial coverage. A complete local
secondary scan remains `AvailabilityGroupLocalOnly`, so the domain evaluator
cannot issue an all-replica verdict from it. Missing metadata and known
checksum/damage faults remain present in the recorded samples. Source UTC
observation time and monitor UTC receipt time are separate fields.

The parser reads at most one row beyond its configured row/database ceiling.
On row, database or byte truncation it publishes no database prefix and records
all read rows as a conservative lower bound on withheld evidence. Malformed
input and cancellation return no snapshot. Output collections own their data.
The byte allowance covers the envelope, expanded keyed identities, escaped
zone text and derived assessment inputs. These limits are independent: a
sample-rich scan can hit the conservative byte ceiling before the row ceiling.

UTC finish resolution requires an explicit `BackupHistoricalZoneQualification`
for the exact target/revision, with a version and a half-open UTC interval
certifying historical zone configuration and rules. No production provider or
audited qualification command is installed in this stage. The current SQL
zone ID alone cannot create that qualification. A recorded start offset must
agree with the qualified zone at start, both endpoints must be unambiguous and
valid, and both UTC instants must lie inside the qualified interval. The
resolver uses the finish's own offset across a clock transition; unknown,
inconsistent or out-of-range values retain a closed reason and no UTC finish.
It never substitutes the host zone, clamps calendar overflow or fabricates a
receipt-time finish. See [.NET ambiguity handling](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isambiguoustime?view=net-10.0)
and [UTC conversion behavior](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.converttimetoutc?view=net-10.0).

Typed evidence and historical qualification models now live in the domain.
Their constructors enforce closed metadata, lineage, unique slots, source
accounting and the exact target/revision/run, including unknown-identity rows.
The SQL adapter retains only the parser result and conversion logic. An
application port supplies explicit historical qualifications without coupling
the repository or validator to the SQL adapter.

## Collector payload stage

`SqlServerBackupsV2Collector` now produces a version-2 operational-health payload
validated by the shared `CollectorOutputValidator`. Required capabilities,
transport, privilege, major/platform/edition and exact permission scopes are
checked before either the qualification port or the SQL source is called.
SQL Server 2019 additionally requires the AG metadata grant when the profile
observes AG support; the source's 51006 guard remains authoritative if topology
and capability evidence differ. Missing source bounds are invalid output, not
permission failures. Failed runs publish no source prefix.

One linked deadline covers qualification lookup, connection, query and parsing.
The production command supplies typed row and database lookahead parameters,
uses a sequential reader, and captures receipt UTC when the reader opens. The
default qualification port returns no qualification. It never invents rules
from the current zone ID. External cancellation propagates; deadline exhaustion
has its own outcome. Version-1 and version-2 payloads cannot cross validators.

The active registry still selects v1. The repository now dispatches v2 payloads
to a private fenced writer; production collector execution remains ungranted.
Retention is registered and maintained as a three-table group. Schedule and
compatible-worker rollout gates, qualified zone provisioning, policy commands
and assessment reads remain pending. No production
activation or native collector qualification is claimed here. Cross-replica
pairing remains explicit work; this collector does not discover peers or merge
histories.

## Time and metadata boundaries

The source deliberately does not publish a converted finish UTC instant.
Microsoft defines `backupset.time_zone` at backup start, not finish. It is
insufficient to resolve a backup spanning an offset transition. A current
`CURRENT_TIMEZONE_ID()` is additional context, not a certificate of the historical
zone configuration. The parser needs an explicit, qualified time-resolution
contract; ambiguous, invalid, unknown or inconsistent historical times must
remain indeterminate. See
[backupset](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/backupset-transact-sql?view=sql-server-ver17)
and [CURRENT_TIMEZONE_ID](https://learn.microsoft.com/en-us/sql/t-sql/functions/current-timezone-id-transact-sql?view=sql-server-ver17).

Metadata visibility is limited to visible databases. `VIEW ANY DATABASE` alone
does not enumerate every offline database. Microsoft also documents that
`sys.database_recovery_status` can try to start an unopened database. Qualify
offline, AutoClose and permission behavior in a disposable lab before production
activation, without adding database-alter privileges just to improve coverage.
See [sys.databases](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-databases-transact-sql?view=sql-server-ver17)
and [sys.database_recovery_status](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-recovery-status-transact-sql?view=sql-server-ver17).

## Verification recorded on 2026-09-30

Seven real JSON Schema checks pass for the approved manifest and rejection of
extra fields, nested drift, missing bounds, unbounded limits, wrong output
version and omitted AG permission. No PostgreSQL database is used by these
schema-only tests despite their project location.

The collector SQL inventory, passive-source and row-bound checks pass, along
with PowerShell syntax validation. Eight focused checks of the actual passive
validator confirm that fixed THROW messages are accepted while mutation and
dynamic execution remain rejected. This records the collector-specific checks,
not a full Local validation run. The source uses read-only CTEs; its test fixture
table variables are separate from the embedded collector assets.

Four SQL Server checks pass on the local engine: all three versioned SQL assets
execute against table-variable fixtures, and the installed-major source returns
the real catalog shape and rejects invalid source limits. Fixtures cover renamed
and recreated databases, duplicate and missing GUIDs, wrong recovery forks,
missing/purged history, five slots, equal-time tie selection, unknown offsets,
unresolved finish time, classified loss, local-only secondary context, incomplete
AG identity, and database/row limits. Fixtures modify table variables only; the
real source read does not issue backup, restore, grants or persistent DDL.

The same four SQL checks now drive the typed parser through a sequential
`SqlDataReader`, including the installed-major catalog read. Ninety-four
focused unit checks pass across historical time resolution, typed parsing and
the existing freshness assessment contracts. The initial resolver tests failed
against the unresolved implementation before the conversion logic was added.
Tests cover transition folds/gaps, crossing a transition, historical window
boundaries, calendar overflow, keyed identity/revision isolation, malformed
streams, nullable metadata, preserved faults, future source time and withheld
limit prefixes. No full Local test profile was run for this change.

The collector/domain/validator stage passes 171 targeted unit checks, including
the backup suites and related AG, replication and legacy operational-health
checks affected by the shared validator. Four local native source/parser checks
also pass after domain promotion. The production command and failure paths are
tested through injected ports; trusted TLS, non-admin execution and real AG
behavior still require the separate native qualification above.

The local run used Windows authentication and encrypted transport with the
existing Local-only certificate bypass for its self-signed lab certificate.
It does not qualify trusted production TLS, a non-admin monitoring principal,
native SQL Server 2019/2025, or real AG preference and topology. These remain
separate required lab checks. The production collector, fenced writer,
retention, policy commands and assessment UI are not activated by these tests.

The persistence stage adds forward migrations 0270/0271 and a canonical mapper.
`backup_v2_scan`, `backup_v2_database` and `backup_v2_sample` are typed, append-only,
forced-RLS parents partitioned by the monitor receipt clock. The target clock and
offset-free source times remain separate evidence. Unknown database identity
retains an ordinal inventory row without backup samples or a policy identity.
All inventory/sample rows, the scan, completion receipt, visibility gap and
schedule/circuit update commit together under the exact run, target revision,
schedule revision and worker lease. Identical canonical completion replay is
accepted after the schedule advances; changed completion is rejected.

Resolved finish times reference an immutable target/revision qualification
containing bounded serialized .NET zone rules, their checksum, expected source
zone and a half-open UTC window. The writer checks that reference, source-zone
agreement, recorded-start conversion, endpoint window/order and plausible
minute-aligned finish offset. It does not reinterpret Windows zone rules in
PostgreSQL. Historical transition conversion remains the typed resolver's
responsibility. An audited provisioning command and production provider are
still required; fixture qualification is not a deployed historical certificate.

Four focused PostgreSQL 18.4 tests pass, including direct malformed JSON and
qualification-boundary cases, discarded truncated prefixes, unknown identity,
nullable metadata, rejected item/byte accounting, future target clocks, RLS,
private grants, append-only protection, lease/schedule fences and rollback when
the final sample partition is missing. Two related existing backup/AG commit
regressions also pass. Fixture-only partitions and v2 catalog scheduling are
explicit: these tests do not activate production v2 or qualify native AG/TLS.

Forward migrations 0272/0273 add one `m9_backup_evidence` retention policy for
the scan, inventory and sample graph. Its default is 30 days with a floor of
three partitions for every parent. Operator v1 backup policies remain unchanged.
Catalog maintenance creates and registers only the current UTC D-1/D/D+1
partitions for each parent, verifies their identity/bounds/owner, and revokes
direct application-role access. The SQL wrapper checks the catalog lease
before and after the operation.

The scheduled retention worker starts a 24-hour attached grace period. All
three tables stay readable during grace; a policy revision change cancels it.
After grace, the worker checks the retention lease, every partition's physical
and registry identity, floors, reader/backfill leases, time-scoped analytics
dependencies, unreviewed inbound foreign keys and recovery attestation. It
detaches/drops samples before inventory before the scan in one transaction;
it never uses `CASCADE`. A blocked drop rolls back every preceding DDL action,
records an immutable retry, and leaves the graph attached. The separate
single-partition worker and manual detach endpoint cannot split this graph.
Policy edits remain available through the global retention API; manual backup
group retirement uses the scheduled route at this stage.

Two native backup retention tests and seven existing AG/TempDB retention
regressions pass on PostgreSQL 18.4. They exercise real linked rows, grace
readability/cancellation, child reader/backfill holds, floors, missing recovery
evidence, an unknown FK, a final-parent drop failure and subsequent retry,
preserved FK constraints, private partition maintenance, stale leases and
authorized policy mutation. The nine affected analytics client/parser checks
and web typecheck pass. These are focused checks; no full Local profile or
production collector activation is claimed.

```powershell
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupV2ManifestTests'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupV2PersistenceTests'
dotnet test tests/SqlObserver.UnitTests/SqlObserver.UnitTests.csproj --filter 'FullyQualifiedName~BackupHistoricalTimeResolverTests|FullyQualifiedName~BackupV2ParserTests|FullyQualifiedName~BackupFreshnessTests'
# Use the existing explicit Local or Release SQL Server lab contract.
dotnet test tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj --filter 'FullyQualifiedName~BackupV2SourceIntegrationTests'
```
