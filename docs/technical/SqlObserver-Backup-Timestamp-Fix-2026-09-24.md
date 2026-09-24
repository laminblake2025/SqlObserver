# Backup timestamps and regular full selection

The SQL Server 2019/2022/2025 backup queries now return the source time-zone offset instead of a constant NULL. They convert documented quarter-hour values to minutes; NULL, 127, and out-of-range values remain unknown. The mapper preserves local time when UTC cannot be established, and missing timestamps remain null instead of becoming year 1. Missing backup rows and backup sets with an unavailable finish time receive distinct existing coverage values.

Copy-only full backups are excluded before ranking the latest full backup. Differential and log selection are unchanged. The current 35-day history window and database-name grouping remain in place.

Microsoft documents the offset as the value at backup start. A backup spanning a daylight-saving transition can therefore still have uncertainty in its finish-time conversion. The collector does not guess a historical time zone from the machine's current offset. See [backupset](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/backupset-transact-sql?view=sql-server-ver17).

## Deployment and remaining scope

Forward migration 0085 updates the four collectors sharing the M9 asset bundle. It checks every prior binding, preserves run history and schedules, and restores the append-only protection. Deploy the matching application and assets with the migration; previously applied migrations remain unchanged.

This completes only the timestamp and copy-only portion of the backup fix. Enumerating databases without backup history and matching by database GUID remain open. The documented GUID catalog can attempt to open unopened databases, so its use needs qualification against the passive collection requirement before replacing the current grouping. See [sys.database_recovery_status](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-recovery-status-transact-sql?view=sql-server-ver17).

## Verification

Twelve production-mapper cases initially produced five failures and seven passing controls. After the fix, all 16 focused mapper/asset cases and two focused unit checks passed. These tests use synthetic rows and do not connect to SQL Server.

Nine focused PostgreSQL cases passed: the three new upgrade/rollback cases, the affected historical upgrade, and five actual application catalog configurations on fresh repositories. They verify unchanged history, schedules, and unrelated registry fields, rejection of stale bundles, and preservation of append-only protection after failure. Asset verification passed for 25 manifests, 250 direct pins, 37 nested contract pins, and 41 SBOM input pins.

Three native regression cases compile for the version-specific queries. They replace only the history source with fixed VALUES rows to check copy-only precedence, tie-breaking, the history window, and positive/negative/unknown offsets. They have not run; native lab access remains pending. No native SQL Server qualification is claimed.

No full Local validation, full PostgreSQL suite, or broad CI run was requested for this slice.
