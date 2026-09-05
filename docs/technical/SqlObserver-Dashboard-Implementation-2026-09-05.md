# Dashboard implementation and lab validation — 2026-09-05

Implemented the dashboard refactor in the existing React/TypeScript/Vite application and repaired report failures found through live testing. The September 4 technical overview guided project context; the supplied prompt and images guided presentation. Screenshot values were never treated as authoritative monitoring data.

The initial checkout was `ed85a2b`; its tree matches the handoff's `5387e8e` merge commit (`git diff ed85a2b 5387e8e` is empty). Existing user files and bundles are preserved. Changes remain in the working tree.

## Implemented behavior

- Shared graphite/mint shell, persistent sidebar, overview landing page, shared target selection and hash routes. Direct links resolve an authorized target beyond the currently loaded registration page. Browser navigation mounts one diagnostic surface at a time.
- Searchable, name-sorted registration table with lifecycle filtering and bounded paging. Four workers load health and five alert records per target; no background polling, speculative totals or automatic pagination.
- Accessible Add server dialog with native focus containment, Escape and focus return. Closing preserves the form and registration ID; failed requests can be retried without inventing a new identity. Existing Windows identity and validated TLS registration semantics remain.
- Semantic activity/operational tables with labelled columns; bounded wait-delta bars exclude missing baselines and resets. Existing health, deadlock, alert, operations and report workflows remain reachable.
- Query investigation includes source/database filtering, metric ranking selection, bounded history charts, observation KPIs, fingerprints and explicit missing-content states. Each ranking request uses one metric/snapshot. Matching history can fill selected KPIs only for the exact collection run, observation identity, database, fingerprint, source and semantics. No cross-snapshot metric merging or cumulative/interval aggregation.
- Analytics exposes the existing evidence surfaces with named table columns, window/snapshot provenance and bounded paging. Selected-server host CPU uses actual numeric observations. Plots do not interpolate gaps or create predictions.
- Abort guards protect initial loads, target changes, query/plan selection and report changes. Query plan metadata clears immediately before a new selection, including selection without a plan.

## Widget mapping and intentional differences from the concepts

| Widget | Existing source | Bound/meaning |
| --- | --- | --- |
| Registration KPIs | `/api/v1/observation-targets?limit=50`, lifecycle and capability status | Current registration page only; active lifecycle is not collection health |
| Connections / SQL physical memory | Target `/health`, dimensionless `engine.user_connections` / `engine.process_physical_memory_bytes` | Latest available value and timestamp; unavailable is not zero; memory shown in GiB |
| Collection state | Target `/health`, state and repository time | Separate from discovery capability |
| Attention | Target `/alerts/active?limit=5`, health and discovery reasons | Loaded alert records and explicit gaps; no fleet-wide alert total |
| Host CPU history | Target `/analytics/series`, `host.cpu.percent` | Selected target only; supported API's bounded window and partial state |
| Query ranking | Target `/query-performance/top`, one allowlisted metric | Up to 200 observations per page; each response retains its own snapshot and window |
| Selected query KPIs/history | Target query history and plan metadata endpoints | Exact observation match for KPIs; chart filters source and semantics; content remains unavailable |
| Wait bars | Activity waits, `waitTimeMillisecondsDelta` | Largest ten comparable deltas within the loaded 25-row page; no rate or all-server total |
| Analytics / operations | Existing validated API projections | Human-readable column headings, inert details, explicit provenance |
| Reports | Existing report creation and HTML/CSV endpoints | Four fixed report definitions, existing windows, scope, export and audit limits |

The concepts' broad utilization totals, forecasts, query text and executable plans are deliberately absent where the available contracts cannot support them. The lab currently reports unsupported/no-activity query evidence; populated query screenshots use the clearly labelled isolated fixture. Empty performance/incident/capacity reports are successful exports of empty evidence, not proof that those collectors have populated data.

## Backend repairs

- Canonical UTC JSON serialization preserves precision while producing `Z` timestamps expected by strict clients, including the operational health serializer.
- The query API accepts canonical `logical_reads`. Window comparison permits equivalent fractional-second formatting while still rejecting even a distinct 100 ns instant.
- Migration 0024 qualifies ambiguous PL/pgSQL column references in report materialization. It was applied with the canonical migrator; earlier migration bytes are unchanged.
- Migration 0025 adds `reporting.read_report_run(uuid,uuid)` for one unexpired, target-scoped run. The repository now calls that function instead of a table it cannot read. PUBLIC execution and runtime table access remain restricted. Closed migration, installer and certification inventories were updated to the exact new bytes.

These migrations repair observed functional failures; they do not manufacture data for screenshots.

## Validation

- Complete `tools/validate.ps1 -Profile Local`: **passed**, 1,060 .NET tests and 93 frontend tests at that run. All configured gates remained enabled. See `TestResults/dashboard/local-validation.txt`.
- Final frontend typecheck, **94 tests passed**, and verified production build: see `TestResults/dashboard/final-frontend-results.txt`. This uses the same `SQLOBSERVER_M12_TRUSTED_PWSH_PATH` binding as the repository validator. A plain standalone test invocation initially lacked that binding; its 13 license failures were environment setup failures.
- Focused route, timestamp and exact query-selection regression checks: **13 passed**. The final selection test was added after the full Local run.
- An existing host completion/disposal test intermittently exhausted its 100 ms scheduling allowance. Its test allowance is now one second; production cancellation limits and the timeout-failure tests are unchanged.
- Browser: populated, loading, empty, error and partial/stale fixture states inspected. Desktop widths 1440 and 1920 and narrow width 390 checked; document overflow was eliminated (390 viewport / 375 document pixels, including scrollbar space; 1920 / 1905). Dense tables scroll internally.
- Browser: Escape returns focus to Add server; reopening retains draft fields. Synthetic failed registration followed by retry produced two attempts with **one unique registration ID**. Delayed plan response followed by selection of a planless query retained “Metadata unavailable.”
- Live VM: activity rows render; query rankings show valid no-data/unsupported evidence instead of parser failures. Server runs with external configuration, original identity and normal HTTPS validation.
- Live VM: **all four reports returned HTTP 200 for HTML and CSV**. Exact run IDs and export sizes are recorded in `TestResults/dashboard/report-api-results.txt`.

Local validation and these lab checks are **not release certification**. Installer, signing, trusted-TLS matrix, sustained performance and other external release lanes remain subject to their existing qualification workflow.

## Screenshots

All populated fleet/query images below are synthetic fixtures, isolated from production API paths. The activity image is from the live VM.

- [Fleet overview, 1440](../../TestResults/dashboard/screenshots/overview-1440.jpg)
- [Query investigation, 1440](../../TestResults/dashboard/screenshots/query-1440.jpg)
- [Narrow overview](../../TestResults/dashboard/screenshots/overview-390.jpg)
- [Add server drawer](../../TestResults/dashboard/screenshots/add-server-1440.jpg)
- [Loading](../../TestResults/dashboard/screenshots/overview-loading.jpg), [empty](../../TestResults/dashboard/screenshots/overview-empty.jpg), [error](../../TestResults/dashboard/screenshots/overview-error.jpg)
- [Live activity](../../TestResults/dashboard/screenshots/live-activity-1440.jpg)

No dependencies, external fonts, CDN resources or new third-party visual assets were added. The existing system font fallback is used. Build hashes now use Rolldown's hexadecimal alphabet to satisfy the unchanged eight-character alphanumeric asset contract.

## VM deployment and remaining limitation

Live dashboard: `https://win-qngov5gdm24:5443/`. Server directory: `C:\SqlObserverLab\Server-dashboard-v4-20260905`; web directory: `C:\SqlObserverLab\Web-dashboard-v6-20260905`. Migrations 24 and 25 are applied. Previous service binaries and external configuration remain available. OpenSSH's SFTP subsystem was repaired to use its absolute executable path; authentication was not changed.

Logical-file collection is now restored. Following explicit user approval on September 5, `VIEW ANY DEFINITION` was granted to `WIN-QNGOV5GDM24\SqlObserverCollector`. Automatic approval review had previously blocked this change; that approval requirement is now resolved. The older full bootstrap plan stopped at its unrelated existing `msdb` principal check, so the approved metadata grant was applied separately without changing that principal.

End-to-end HTTPS validation at `2026-09-05T06:01:36.091623Z` reports `database.files` **current / succeeded**, circuit closed, **15 source rows, 15 output rows, 15 inserted rows**, zero rejected rows and **no visibility gap**. The health API returns all 15 logical files. Evidence: `TestResults/dashboard/logical-file-health.json`; applied SQL: `TestResults/dashboard/lab-tools/logical-file-grant.sql`. No application code, service identity, TLS settings or data-write permissions were changed in this follow-up.

Microsoft documents the metadata visibility requirement in [sys.master_files](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-master-files-transact-sql?view=sql-server-ver17).
