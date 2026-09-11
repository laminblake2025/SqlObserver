# Live sessions and 24-hour activity history

Activity now includes a server-scoped Live sessions panel. The application server reads PostgreSQL evidence; opening more browsers does not execute more SQL Server queries. The existing activity APIs, waits, blocking history and Overview timeout safeguards remain intact.

## Collection and evidence

- The collector samples up to ten active targets every ten seconds, with at most ten target operations and one collection per target in flight. Existing Windows-integrated, certificate-validated connections and repository worker leases are reused. Each SQL operation has a five-second deadline. Failures back off to 80 seconds and mark retained evidence stale; missing collection also becomes stale after 30 seconds.
- The versioned, checksum-verified SQL asset supports SQL Server major versions 15, 16 and 17. It refuses sysadmin identities and checks the applicable server-state permission. It reads supported DMVs into a connection-local temporary snapshot of at most 513 rows, retains at most 512, and marks truncation. The temporary table coordinates metadata and SQL-handle observation; it does not modify application databases.
- A lifetime identity includes engine startup, session login, request start, session ID and request ID. Multiple requests remain separate rows. Interval deltas require the same identity, increasing counters, two complete observations and at most two minutes between observations. CPU remains milliseconds, never a CPU percentage. SQL Server local startup/login/request timestamps are labelled separately from UTC observation timestamps.
- Active-request memory means granted query memory; idle-session memory means session memory. Read counters are the engine's reported counters, including its limitations for parallel requests. Database choices come from the visible database inventory, bounded at 1,024 entries.
- The latest successful capture remains readable when collection fails. First successful observations in each UTC minute are retained for 24 hours. Other snapshots remain briefly available for stable pagination and open details. History never interpolates missing minutes.

## Storage and maintenance

Forward migration 0041 creates the live evidence tables and restricted functions. Migration 0042 adds bounded cleanup progress and coordinates cleanup with commits. Earlier migration bytes and ledger entries are unchanged.

Live list filtering and sorting run in PostgreSQL before a 50-row page is selected. A lookahead row determines whether a continuation exists. Cursors contain a snapshot, offset and digest of the target/filter scope; the repository also checks target/snapshot ownership. Historical and query-detail reads reject evidence at or beyond 24 hours, independently of cleanup timing.

An independent maintenance loop runs every ten seconds with a five-second repository timeout. Each pass deletes at most 120 expired snapshots and examines at most 8,192 candidate protected payloads. An indexed, persisted cursor advances past still-referenced payloads. A repository advisory lock prevents concurrent maintenance and coordinates with shared commit locks so a new reference cannot race orphan deletion. Browsers do not take this maintenance lock. Physical reclamation is asynchronous; visibility expires immediately at the retention boundary.

At the maximum 512 rows per minute across ten servers, history contains up to 7,372,800 rows, plus the short live window. Actual bytes depend on metadata and query diversity. A lab sample averaged 754 bytes per metadata JSON value, which projects to about 5.56 GB of raw metadata at that row ceiling, before indexes, tuple overhead and protected SQL. Typical lower session counts consume proportionally less. Capacity planning must include those overheads and PostgreSQL vacuum behavior.

## Protected query text

- Capture retains only an active request's executing statement, up to 16 KiB of UTF-8 and 1 MiB of distinct captured text per cycle. Repeated statements are deduplicated. Truncated, omitted and unavailable capture are explicit states. Metadata remains usable if text capture or protection fails.
- AES-256-GCM protects payloads, with the target ID as authenticated associated data. Keyed fingerprints permit deduplication without exposing plaintext SQL hashes. List responses contain identifiers and availability only.
- The application configuration contains `SqlObserver:LiveActivity:ProtectedKeyPath`, never the plaintext key. `tools/lab/provision-live-activity-key.ps1` creates a machine-DPAPI-protected key file and restricts its directory/file ACL to SYSTEM, administrators and the exact server/collector service identities. Run provisioning on the Windows host that will use the key. The script refuses to replace an existing key.
- Query detail retrieval requires both ordinary target monitoring access and the separately assignable, target-scoped `QueryTextReader` role. Access is audited with target/snapshot identifiers, actor SID and outcome. Provider exception messages and SQL are not logged by the live pipeline. SQL is rendered as inert text, with no HTML interpretation.
- Retain the protection key while its evidence is needed. Moving a DPAPI-protected file to another host does not provision a usable key there. Replacing or losing a key makes older protected details unavailable; metadata remains readable.

SQL Server permission requirements are documented by Microsoft in [sys.dm_exec_requests](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-exec-requests-transact-sql?view=sql-server-ver17). Windows protection uses [DPAPI / ProtectedData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata).

## Validation evidence

The new unit and HTTP tests cover distinct request/session lifetimes, UTF-8 truncation, encryption round-trip and tamper rejection, target binding, independently scoped query-text permission, no SQL in list responses, filter binding, and no-store responses. PostgreSQL tests exercise filtering beyond the first page, final cursors, scope changes, counter resets, minute selection, missing minutes, exact expiry, restricted runtime privileges, stale collection, protected-payload cleanup, concurrent cleanup/commit, ten targets and 100 concurrent viewer reads. A 9,000-orphan burst verifies bounded cleanup and cursor progress. Refresh-loop tests cover one request in flight, cancellation, bounded error backoff, recovery and history mode.

The live SQL Server 2025 lab was exercised with three minutes of controlled read-only activity in `SqlObserverLabSales` and `SqlObserverLabWarehouse`. Browser checks confirmed both rows, database-specific filtering, executing SQL details, interval counters, pause/resume, exact minute selection, visible gaps, historical database filtering and preserved details after requests finished. A sample of 35 retained snapshots reported 14.69 ms average and 264.04 ms maximum observation-to-commit time, including initial warm-up. Three deduplicated protected payloads occupied 5,968 serialized bytes. No plaintext SQL fields appeared in metadata storage.

These SQL timings come from the single-server lab. The ten-target/concurrent-viewer test validates the repository path, not simultaneous collection against ten physical SQL Server instances. Sampled monitoring can miss requests that start and finish between observations.

Detailed test output, deployment scripts and disposable readback artifacts are under `TestResults/live-activity` and `TestResults/live-*-results.txt`. The final Local validation profile passed 1,096 .NET tests and 111 frontend tests, plus TypeScript checking, production build and asset verification. The separate live PostgreSQL integration test passed against a disposable PostgreSQL instance. These checks are not full release certification.

## Final lab deployment

Migrations 0041 and 0042 are installed. Matching binaries run from C:\SqlObserverLab\server-live-v2-20260907 and C:\SqlObserverLab\collector-live-v2-20260907, with web assets in C:\SqlObserverLab\web-live-v2-20260907. Both Windows services were verified running. The existing protected key was retained; a predeployment historical snapshot successfully returned its protected executing statement after the update. Paused database-filter changes were also verified against the final deployed UI.
