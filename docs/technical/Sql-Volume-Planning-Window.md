# SQL volume capacity planning in a shared window

Resources follows the workspace Live/Rewind window for SQL-reported target-host capacity planning. The window selects repository completion times, including failed attempts. The default is the newest completed attempt in that window. Failed, missing and empty receipts do not select an older successful snapshot as a substitute.

The source is canonical `collection_run_v2` identity/outcome and existing retained `sql_volume_snapshot` payload. Forward migration `0362_sql_volume_planning_window.sql` adds the retained read contract. It does not enroll collectors or change native SQL permissions. The volume collector remains opt-in.

Current target revision authorizes the read. The exact source is identified by `runId` and `sampleTargetRevision`, independently of the currently loaded receipt page. Capacity facts remain canonical Int64 decimal strings through SQL, JSON and the UI. SQL observation time, ingestion time and repository completion remain separate facts.

## Contract

Both authenticated GET routes require a same-target Viewer, Operator or TargetAdministrator grant and return private, no-store output.

| Route | Required fields | Purpose |
|---|---|---|
| `/api/v1/observation-targets/{target}/resources/volume-planning/history` | `targetRevision`, `fromUtc`, `toUtc` | Completed attempts and whole-window accounting |
| Above plus `/{runId}` | Above plus `sampleTargetRevision` | Exact capacity snapshot and forecasts anchored to its completion |

UTC query bounds must end in `Z` and use at most six fractional digits. The shared window is half-open and at most 31 days. Optional `limit` defaults to 25 and is capped at 100; the screen uses 100. Unknown, repeated or malformed fields are rejected. Authenticated continuations bind kind, target, revisions, window, page size, original cutoff, source digest and pagination anchor. They expire 15 minutes after original issue; paging does not renew expiry. Changed source, target revision or continuation is refused. Source accounting and response size are bounded; overflow does not claim complete coverage.

Each manifest is capped at 10,000 attempts and 250,000 retained volume rows, with at most 1,000 volumes per receipt. Catalog overflow returns 422. An exact receipt is read independently of the catalog cohort; its lookback overflow withholds forecasting while retaining its raw snapshot. JSON responses are capped at 512 KiB. The private source manifest is not returned.

## Forecast interpretation

A forecast uses a separately disclosed 14-day completion-time lookback ending at the selected receipt, including in Rewind. It uses at most one qualified sample per UTC completion day. Integer deltas are centered before conversion to approximate regression arithmetic. A finite depletion estimate needs enough history, stable reported capacity, an eligible volume-ID source key and a sufficiently consistent decreasing trend. The displayed daily facts remain exact; charts and days-until-full estimates are approximate.

Failed attempts, missing payload, loss, source-clock skew, collection gaps, revision boundaries, changed capacity and non-comparable identities constrain forecasting. An overflowing lookback keeps the exact selected snapshot visible while refusing its forecasts. This is not proof of continuous physical-volume incarnation, future workload, disk health or an admitted native health verdict. A repeated opaque key and unchanged capacity alone cannot establish physical continuity.

## Investigation and accessibility

Shareable Resources URLs use `volumePlanningRun` and `volumePlanningRevision`. Selecting or browsing a Live source fixes its received shared window. Malformed or repeated pins stay explicit until cleared or replaced; unrelated resource selections remain independent. Database scope does not narrow server-wide volume evidence.

Received pages retain their exact source during browsing, retries and shared polling. Denial purges protected source state and fences pending siblings. Target, authority, window, selection and mount generations prevent obsolete responses from restoring evidence. A new explicit source read is required after denial.

Tables scroll locally behind disclosures; controls use dark workspace tokens, minimum 44-pixel targets and visible keyboard focus. Receipt source states and forecast refusal reasons are visible separately from capacity facts.

## Qualification

Final integrated checks and synthetic screenshots are recorded in `artifacts/dashboard-volume-planning-2026-10-04/`. Real PostgreSQL tests exercise production migration, canonical writer and HTTP read paths with test authentication. Browser fixtures and screenshots are synthetic. They do not certify a native SQL Server capture, physical identity, CPU comparison, SQL Agent activity or failover. The configured lab SSH route remains unavailable pending a corrected route or network connection.

The genuine five-day forecast test uses an isolated container clock, actual canonical writer commits and a daily cadence configured by its administrative fixture. This qualifies the read and estimate at that valid cadence; it does not qualify a cadence administration API. Separate synthetic canonical cohorts exercise native overflow guards. Partition detachment exercises retained source disappearance, rather than qualifying the retention worker.
