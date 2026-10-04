# Replication completion-window evidence

Health checks → Replication uses retained canonical `replication.health` collection attempts. The shared half-open seven-day window selects **repository completion**, including failed, partial and empty attempts. The newest completed attempt is the default; a failed or missing attempt never falls back to an older success. An exact `runId` and `sampleTargetRevision` can be read independently of the visible receipt page.

The current target revision and a same-target Viewer, Operator or TargetAdministrator grant authorize each read. Historical sample revisions identify the source; they do not replace current authorization. Both authenticated routes use private/no-store responses:

- `GET /api/v1/observation-targets/{target}/replication/history`
- `GET /api/v1/observation-targets/{target}/replication/history/{runId}`

Both require `targetRevision`, `fromUtc`, `toUtc`; the exact route also requires `sampleTargetRevision`. UTC input ends in `Z`, with at most six fractional digits. Page size defaults to 25 and is bounded at 100; the dashboard requests 100. The native reader uses repeatable read, an explicit target scope and a five-second deadline. The response cap is 512 KiB.

## Source meaning

Retained `telemetry.replication_snapshot_v2` rows preserve monitor capture time, repository ingestion time, opaque row/subscription fingerprints, reported role/status, pending command counts, reported latency in seconds, and source visibility. Monitor capture time was stamped by the monitoring worker. It is not a SQL Server event timestamp. Repository completion is a third clock.

Reported legacy status is unqualified. The existing source combines distribution history with Agent activity that has not been admitted as a live execution/association clock. A reported `healthy` row does not certify current replication health; `disabled` remains the reported source value. Complete source accounting describes the retained receipt, not uninterrupted delivery or topology liveness.

Topology kind, delivery rate and last-success age were not retained by the legacy writer. The new contract returns them as unavailable rather than reconstructing them. Null queue/latency values remain unavailable rather than zero. Row identities do not certify a physical publication/subscription incarnation or continuity between collector versions.

The writer now derives a versioned row key from both already-protected publication and subscription identities plus topology/role. This prevents several subscriptions sharing a publication from colliding in one persisted run. Existing keys remain opaque legacy identities. Unbound source rows retain their existing visibility-gap identities.

## Receipt and retention boundaries

Receipt pages account for canonical completed attempts and retained source rows, with explicit outcome, loss kind, minimum lost items and retained/expected row counts. Failed, partial, missing, empty, incompatible and skewed evidence stays visible. Schedule or revision continuity gaps affect window coverage separately from each immutable attempt receipt. Exact reads do not claim whole-window coverage.

The window is bounded at 20,480 attempts and 250,000 retained rows, including separately counted unbound legacy source rows. Each exact attempt is bounded at 2,048 rows. An overflowing window refuses the read; an independent exact read does not scan the entire receipt window. Legacy rows that cannot join a canonical completion are counted using their actual ingestion-time bucket. They are not assigned fabricated completions or reported as no-events health.

Forward migration 0363 adds a canonical-run dependency for the partitioned replication payload and registers it in the reviewed retirement inventory. New writes must reference canonical identities. Constraint validation is attempted only for a bounded, orphan-free retained census; upgraded large/orphaned fleets retain the existing conservative canonical-cleanup hold until reviewed validation. No unbounded identity backfill is performed. The private immutable M10 replay ledger is unchanged; this feature does not qualify replay-ledger retirement or redesign retention scheduling.

Authenticated continuations bind their purpose, target/current revision, source run/sample revision, exact microsecond window, page size, original cutoff, native evidence digest and keyset anchor. Their original 15-minute expiry does not renew. Changed source, retirement, schedule/policy context or expired continuations refuse unread pages. Already received evidence remains visibly historical; a continuation is not a frozen repository snapshot.

## Workspace behavior

`replicationRun` and `replicationRevision` restore the exact receipt in the owning Health checks tab. Invalid/repeated pins remain explicit until cleared. Equivalent microsecond windows and unrelated database/compare controls preserve the selection; a real target/window change retires it. Investigating Live evidence fixes the actual received window in the shared URL so polling cannot move the investigation.

List and exact evidence are independent sources. Ordinary errors and cancellation preserve received pages, while retry retains the exact attempted continuation. Access denial purges both sources, aborts siblings and requires explicit retry. Target, revision, authority, window, selection and mount generations reject stale/ABA replies and retired controls.

Wide tables scroll locally behind disclosures. The screen uses existing dark-theme tokens, visible focus and at least 44px controls. Automated browser evidence uses synthetic loopback fixtures; it does not establish native SQL Server capture or manual screen-reader/device certification. Qualification evidence is recorded in `artifacts/dashboard-replication-history-2026-10-04/`.
