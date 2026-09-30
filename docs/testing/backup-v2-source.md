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

The local run used Windows authentication and encrypted transport with the
existing Local-only certificate bypass for its self-signed lab certificate.
It does not qualify trusted production TLS, a non-admin monitoring principal,
native SQL Server 2019/2025, or real AG preference and topology. These remain
separate required lab checks. The production collector, parser, fenced writer,
retention, policy commands and assessment UI are not activated by these tests.

```powershell
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupV2ManifestTests'
# Use the existing explicit Local or Release SQL Server lab contract.
dotnet test tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj --filter 'FullyQualifiedName~BackupV2SourceIntegrationTests'
```
