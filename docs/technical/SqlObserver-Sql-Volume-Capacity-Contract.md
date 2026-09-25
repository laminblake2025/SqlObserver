# SQL-reported volume capacity contract

Status: implementation design, 2026-09-25. The Resources screen currently
shows SQL database-file sizes and cumulative file I/O, but it cannot report
free space on the SQL Server host. The `host.volume.*` metrics describe the
collector host and must not be relabelled as target capacity.

Implementation checkpoint: a bounded observation envelope, checksum-pinned
SQL Server source parser, partitioned PostgreSQL evidence table, and fenced
repository writer exist. The query and its one-row look-ahead were executed on
local SQL Server 2022; the parser deduplicates shared volumes with a keyed
fingerprint and withholds incomplete reads. The table has forced target RLS, a
30-day retention policy, and daily partition upkeep under the existing lease.
A focused PostgreSQL test exercises exact 64-bit bytes, same/different digest
replay, and rollback after lease loss. The collector is not yet registered or
scheduled because the bounded read contract and Live UI cutover have not landed.

## Source evidence and scope

Microsoft documents `sys.dm_os_volume_stats(database_id, file_id)` as returning
`total_bytes` and `available_bytes` for the volume containing a database file.
It requires `VIEW SERVER STATE` on SQL Server 2019 and `VIEW SERVER PERFORMANCE
STATE` on SQL Server 2022 and later, matching the existing `database.files`
manifest's permission levels. The function can also return volume IDs and mount
points; neither should enter the public API, logs or reports.

The read-only `tools/lab/verify-sql-volume-capacity-local.ps1` probe succeeded
on local SQL Server 2022 Express: 10 visible database files had capacity, none
were unknown, and the minimum free value across those files was about 129 GB.
The probe rejects a one-file cap as incomplete. This proves the DMV shape on
that lab instance only, not SQL Server 2019/2025, remote permissions, failover,
or production cost. Multiple files on one volume repeated the same capacity;
**never sum per-file `available_bytes`**. This source covers volumes that hold
visible SQL database files, not every volume on the host.

- [Microsoft `sys.dm_os_volume_stats` reference](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-os-volume-stats-transact-sql)
- [Microsoft DMV permission matrix](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/system-dynamic-management-objects?view=sql-server-ver17)

## Collection and identity

Add a versioned, passive `storage.volume` collector with a slower default
cadence than `database.files`. Read a bounded, ordered set of `sys.master_files`
identities and `OUTER APPLY` the DMV. Read one look-ahead file to detect an
incomplete source cap; missing DMV rows are unknown, never zero free space.
Keep the existing connection, command, row and response-byte budgets. The
query must select only needed columns, not `SELECT *` or file paths.

The raw volume ID or mount point may be read transiently to collapse multiple
files on the same volume. Use the existing `IdentityFingerprintKey` facility to
derive a target-scoped opaque volume identity; do not persist raw identifiers.
The documented volume ID and mount point can be empty. In that case, retain
file-scoped unknown identity and do not infer a stable volume trend. A physical
SQL host change behind an AG listener must start a new identity epoch, so the
fingerprint input needs a keyed physical-node component. Validate this across
failover before enabling days-until-full forecasts.

Do not store the same capacity once for every database file. A volume's free
bytes can move during the scan: retain the minimum observed value for repeated
files as a conservative current reading. Conflicting total bytes or a mix of
known and unknown capacity for the same identity is inconsistent source
evidence and must not produce a complete snapshot. Persist one
deduplicated volume row per fenced run, with total/free bytes, number of mapped
files, observed time, target revision, run ID and identity quality. All byte
values are nonnegative 64-bit integers; `free <= total`, and unknowns remain
nullable. A source cap, permission failure or conflicting duplicate identity
must publish a visibility gap rather than an apparently complete snapshot.

## Repository and read cutover

Introduce forward-only migration(s) for a partitioned volume snapshot and
target-scoped RLS. The ingestion function must compare the existing fenced
lease and commit run outcome, digest and volume rows atomically. A same-digest
replay is idempotent; a divergent replay is rejected. Define retention before
fleet rollout because the current M4 file snapshot table is unpartitioned and
has no adequate default retention.

Expose a bounded, target-authorized `/api/v1/observation-targets/{id}/resources/volumes`
read with snapshot/revision-bound pagination. Return opaque volume identity,
total/free byte strings, observation time and freshness/partial-coverage
evidence. Do not expose mount point, physical path or the keyed fingerprint
input. The Resources page should call this only in Live, label it as
SQL-reported target-host capacity, and distinguish it from collector-host
metrics. Rewind and forecasts require a separately bounded historical series;
do not imply them from one current snapshot.

## Focused proofs before enabling the feature

1. Run the read-only DMV probe on SQL Server 2019, 2022 and 2025 with the
   least-privilege collector login; verify a shared volume is deduplicated,
   unknown/empty identifiers stay unknown, and file-cap truncation is visible.
2. Test fenced PostgreSQL commit, same/different digest replay, lease loss,
   target RLS, nullable capacity, retention and migration upgrade/rollback.
3. Test API target/cursor binding and zero/large integer serialization; test
   Live/Rewind rendering without any physical path or collector-host conflation.
4. Measure source query and target-first history plans at a realistic file and
   fleet count before changing the default collection cadence.
