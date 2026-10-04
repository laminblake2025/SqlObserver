# Supporting backup and availability receipts

Health checks keeps the primary backup assessment and per-replica availability evidence separate from the supporting legacy collection receipts. Supporting receipts select canonical completed attempts in the shared half-open window, using repository completion time. Failed, partial, empty and missing attempts stay visible. The newest attempt is the default; missing or failed evidence never substitutes an older success.

An exact `runId` and historical `sampleTargetRevision` can be restored independently of the visible receipt page. The current target revision and a same-target Viewer, Operator or TargetAdministrator grant authorize every read. The historical revision identifies the sample and does not grant access.

## Read boundaries

The supporting API exposes:

- `GET /api/v1/observation-targets/{target}/backups/history`
- `GET /api/v1/observation-targets/{target}/backups/history/{runId}`
- `GET /api/v1/observation-targets/{target}/availability-groups/history`
- `GET /api/v1/observation-targets/{target}/availability-groups/history/{runId}/replicas`
- `GET /api/v1/observation-targets/{target}/availability-groups/history/{runId}/databases`

Every route requires `targetRevision`, `fromUtc` and `toUtc`; exact reads also require `sampleTargetRevision`. UTC inputs end in `Z`, with at most six fractional digits. Page size defaults to 25 and is bounded at 100; the dashboard requests 100. Reads are limited to seven days, 20,480 completed attempts, and 250,000 retained rows per source window. An exact legacy backup run is bounded at 1,537 rows; an availability run at 2,048 rows across its replica and database streams. Native reads use a five-second deadline and private/no-store responses capped at 512 KiB.

Signed continuations bind purpose, source kind and row stream, target/current revision, historical run/revision, exact microsecond window, limit, cutoff, source digest, keyset anchor and original issuance. Expiry remains fifteen minutes from the first read. A changed or retired source refuses unread continuations; already received pages remain visibly historical. A receipt list and an exact read are independent sources. Replica and database pages refer to the same exact availability attempt and have separate continuation purposes. Their exact content epoch agrees while the retained source agrees; initial read cutoffs remain independent and do not imply one atomic source read.

Forward migration 0364 adds a bounded, target-scoped definer entry point and two private helpers. Only the server role can execute the entry point; runtime roles receive no direct replay-ledger access. Existing canonical source foreign keys and reviewed retention dependencies remain in force. The migration does not alter collectors or activate a source.

## Source meaning

The supporting contract labels its payload `legacy_v1`. Canonical attempts from another collector/schema version remain explicit incompatible attempts; they are not interpreted as v1 hashes or v2 identities. The primary v2 readers retain their richer qualification and assessment contracts.

Legacy snapshot observation time was stamped by the monitoring worker after the source read. It is distinct from canonical repository completion and is not a SQL Server event time. The legacy source does not retain a separate ingestion clock. An empty or absent payload exposes no invented observation timestamp.

Backup fingerprints remain opaque names from the original source. Backup-set identifiers and byte counts are decimal strings to preserve their full signed 64-bit range in JavaScript. Offset-free SQL local finish times stay explicitly local; unknown source time is not converted into a fabricated UTC clock, freshness verdict or RPO assessment. The primary backup assessment remains the place for policy and timestamp qualification.

Availability fingerprints and reported states remain legacy source facts. Visibility and state availability are retained per row. A complete receipt accounts for the retained rows; it does not certify physical continuity, current liveness, a failover, exact RPO, or atomic agreement with the primary v2 streams. Missing, incompatible, skewed and incomplete source evidence remains a visible gap.

## Workspace behavior

Supporting reads start when their disclosure is opened or an explicit URL pin is restored. `backupRun`/`backupRevision` and `availabilityRun`/`availabilityRevision` restore their respective sources. Invalid or repeated pins stay explicit until cleared. A real target/window change retires a pin; equivalent microsecond bounds and unrelated database/compare edits preserve server-wide investigations.

Investigating Live evidence fixes the actual received shared window in the URL. Workspace polling then preserves the investigation. List and row streams have independent paging, cancellation, retry and cached pages. Ordinary failures preserve received evidence; access denial aborts siblings, purges supporting evidence and requires explicit retry. Context, current revision, authority, selection, mount and route generations reject stale replies and retired controls.

The supporting panels use the established dark tokens, visible focus, 44px controls and local table scrolling. Automated screenshots and browser fixtures are synthetic; they do not qualify native SQL Server collection or physical failover. Final scoped validation and the requirement summary are recorded under `artifacts/dashboard-backup-availability-history-2026-10-04/`.
