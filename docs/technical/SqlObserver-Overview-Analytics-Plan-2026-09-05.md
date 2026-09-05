# Overview analytics redesign plan

Date: 2026-09-05. Status: proposed; planning only. Based on the current working tree, including existing uncommitted dashboard changes. Source and saved validation reports were inspected; the running application was not revalidated for this plan.

## Product direction

Make Overview the fleet analytics landing page. Within a few seconds, an operator should understand what needs attention, what changed, and where to investigate. Servers remains the inventory and server-selection page. The existing Analytics destination remains the detailed evidence workspace; its jobs, backfill, and diagnostic records should not dominate Overview.

Default to all authorized, actively monitored SQL instances and the last 24 hours. Support a single-server scope without requiring a selection to populate the page. Exclude disabled and retired targets from operational denominators by default and show the exclusion count. Newly registered targets without observations appear as pending/unknown coverage, not healthy.

## Findings in the existing implementation

| Finding | Consequence |
| --- | --- |
| `App.tsx` uses the same `fleet` render branch for Overview and Servers. Both show registration KPIs, the server table, and Needs attention. | The duplication is structural, not just similar styling. Split the route bodies into dedicated components. |
| Overview adds only a selected-server host CPU chart. | The landing page contains little analytics until a server is selected. |
| The four KPIs describe registrations, lifecycle, discovery, and visibility concerns. | They describe monitoring setup rather than SQL workload or operational impact. |
| `useFleetEvidence` reads health and five active alerts for each of up to 50 loaded registrations using four workers. | Counts and rankings cannot represent the full fleet. The browser can issue 100 evidence requests for one registration page. |
| The attention list is assembled in target order and mixes alerts, collection state, and discovery concerns. | It lacks a clear impact-based order and can repeat related problems. |
| Metric series, rollups, comparisons, baselines, and forecasts already have target-scoped endpoints. | Reuse the infrastructure, but add an authorized fleet read model rather than expanding browser fan-out. |
| `MetricCatalogV1` currently contains host and replication gauges. | Existing SQL core counters cannot simply be passed through the analytics pipeline without extending its catalog and source mappings. |
| SQL core collection includes cumulative batch/compilation counters, connections, page life expectancy, and memory values. File collection includes cumulative I/O counters. | Useful inputs exist, but rates, time-window totals, and latency require explicit derivation and history contracts. |
| Active alert rows expose state and rule identity, but no severity field. | A critical/warning breakdown requires defined severity metadata; firing and acknowledged are states, not severities. |
| The series endpoint labels any nonempty bounded result complete; the chart drops dimensions when mapping points. | Do not use this response to infer full-window coverage or chart several volumes as one series. |
| Saved lab reports include partial query coverage, unavailable query text, unresolved host/replication issues at that run, and unknown backup UTC finish times. | Treat live data readiness as a delivery gate. More recent fixes must be verified before assuming these limitations are resolved. |

## Page layout and content

Keep the graphite/mint shell, typography, and navigation. Use color primarily for status and selected chart series. Place analytics ahead of detail tables.

| Position | Content | Behavior |
| --- | --- | --- |
| Header | Overview; scope; 1h / 6h / 24h / 7d / custom range; previous-period comparison; refresh | Default 24h. Custom range initially capped at the existing 31-day analytics limit. Persist scope and time range in the route. |
| Coverage strip | Last refresh, source freshness, reporting versus expected targets, partial/unknown evidence | Always visible. Clearly distinguish current snapshot cards from the historical analysis window. |
| Summary row | Instances needing attention; active alerts; blocked sessions now; deadlocks in window | Each card has a unit, scope, freshness, and drill-down. Show trends only when comparable historical evidence exists. |
| Main row, wide panel | Workload trend | Batch requests/sec over time; secondary view for active requests or connections. Show previous-period comparison separately, with consistent units. |
| Main row, compact panel | Investigate first | Top five actionable issues: affected server/database, measured symptom, severity when defined, duration, change, and destination. “View all” opens the relevant filtered detail page. |
| Second row | Wait pressure and blocking/deadlock trend | Ranked wait categories with contributing servers; blocked-session count over time and deduplicated deadlock events. Charts use separate axes/panels where units differ. |
| Third row | Resource pressure | Compact host CPU, memory, and storage views emphasizing the worst affected hosts/files and time above policy thresholds. No single fleet average that hides an overloaded server. |
| Fourth row | Operational risks | Database state exceptions, backup policy breaches, failed SQL Agent jobs, TempDB pressure, AG/replication concerns. Show affected object counts and freshness. |
| Later addition | Query regressions and capacity outlook | Top five comparable query regressions; volumes approaching a configured limit with defensible forecasts. Keep below immediate performance and operational issues. |

On narrow screens, keep the coverage strip and summary first, then Investigate first, then the charts in one column. Avoid a full-width registration table on Overview. A short ranked list of problem servers is useful because it explains a metric; the inventory belongs on Servers.

## Metric definitions and guardrails

| Signal | Definition / aggregation | Initial availability and work |
| --- | --- | --- |
| Instances needing attention | Distinct authorized instances with an actionable alert or defined operational exception at the snapshot. Count monitoring gaps separately. | New fleet aggregation and explicit exception policy. Never interpret `health.state=current` as proof that SQL service is healthy. |
| Active alerts | Exact distinct alert count with separate firing, pending, and acknowledged states; severity breakdown only with severity metadata. | Existing target source; add full-scope aggregate instead of counting the five displayed rows. |
| Blocked sessions now | Distinct blocked user sessions per instance in its latest fresh, coherent blocking snapshot. | Existing edges need complete snapshot aggregation. Preserve affected target count and oldest wait. Missing snapshots are unknown. |
| Deadlocks in window | Unique event identities within the selected window, deduplicated across repeated ingestion. | Existing deadlock evidence; add fleet/time-bucket aggregation. Partial event coverage produces an observed count, not an exact total. |
| Workload throughput | Per-instance `delta(engine.batch_requests_total) / elapsed seconds`, then sum aligned valid instance rates. | Extend SQL counter history/source mapping and analytics catalog. Do not label batch requests as query executions. |
| Wait pressure | Comparable nonnegative wait deltas, grouped by a versioned category mapping; background/idle waits excluded by a documented policy with a details view. | Existing wait deltas help, but selected-window histories, baseline times, and fleet grouping need verification/extension. Sum wait time is not elapsed wall time or CPU utilization. |
| CPU | Host CPU series; worst hosts, distribution, and sustained threshold duration. Deduplicate instances sharing one host. | Existing host metric; verify live host persistence. Host CPU must be labelled as host CPU, not SQL process CPU. |
| Memory | Host available memory and SQL memory trends, with explicit units. Pressure requires a policy using appropriate evidence. | Host memory gauges and SQL memory observations exist separately. Large SQL physical memory or committed/target ratio alone is not a critical condition. |
| Storage | Per-volume free bytes/percentage and observed host read/write latency; later SQL file latency from paired cumulative counters. | Existing host volume metrics retain volume identity. File API currently combines I/O stall; separate read/write counter histories must be exposed for separate SQL read/write latency. |
| Operational risks | Policy-based backup age, Agent failure events, TempDB usage, database-state exceptions, and relevant AG/replication status. | Reuse current evidence. Backup-age evaluation needs reliable UTC conversion and recovery-model/backup-type policy. Unsupported features are N/A; failed collection is unknown. |
| Query regressions | Same target/database/fingerprint/source/semantics across equal windows; compare weighted duration or CPU per execution and rank by added workload impact. | Add comparable window aggregates and minimum-execution/coverage rules. Never merge Query Store intervals with plan-cache lifetime totals. Query text remains unavailable where the source contract says so. |
| Capacity outlook | Forecast time to an explicit free-space threshold per volume, with confidence, source window, and bounds. | Forecast contracts exist; validate dimensions, sufficient history, and persisted results before showing estimates. Show observed growth when forecasting is unavailable. |

All thresholds must come from versioned policies or explicit product configuration. Do not invent a 0–100 fleet health score in this release. A list of measured exceptions is more explainable.

Rates require compatible target revision, object identity, source, timestamps, and counter epoch. Reset, restart, missing baseline, or inadequate coverage invalidates the interval; suppress it rather than clamp a negative delta to zero. A larger post-restart counter can still conceal a reset, so verify epoch evidence instead of relying only on negative-delta detection.

For SQL file read latency use `delta(read stall ms) / delta(read count)`; calculate write latency similarly. A zero-operation interval is N/A. Aggregate latency by summing stalls and operation counts before dividing, not by averaging per-file averages. Preserve integer precision for cumulative bigint counters through derivation.

SQL wait counters are cumulative and can reset, and per-second performance counters need interval sampling. These choices follow the source semantics in Microsoft's [wait statistics documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-os-wait-stats-transact-sql?view=sql-server-ver17), [performance counter documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-os-performance-counters-transact-sql?view=sql-server-ver17), and [file I/O statistics documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-io-virtual-file-stats-transact-sql?view=sql-server-ver17).

## API and aggregation design

Introduce proposed read-only endpoints under `/api/v1/overview`: `/summary`, `/trends`, and `/rankings`. Names are proposed, not existing APIs. They read persisted monitoring evidence through application/repository ports; opening or refreshing Overview must not execute new diagnostic queries against monitored SQL Servers.

- Accept explicit scope, `fromUtc`, `toUtc`, approved bucket interval, and allowlisted metric/ranking keys. Resolve “all servers” to the caller's authorized set on the server.
- Return a shared snapshot identifier/cutoff, requested and effective windows, units, aggregation semantics, policy/catalog version, and per-widget availability. Reuse the snapshot token for subsequent reads so panels describe a coherent refresh.
- Include expected, eligible, reporting, stale, unsupported, and missing target counts as appropriate. Exact totals must cover the complete authorized scope; top-N truncation is a separate display property. Permission filtering happens before counts, grouping, and cache lookup.
- Bind a snapshot to scope and relevant revisions; revalidate authorization for every request. Reject expired/mismatched snapshots with a restartable response. Do not expose unauthorized target names or counts through partial-state messages.
- Read full-scope totals through bounded set-based repository queries or persisted aggregates. Do not walk registration/alert pages in the browser. Size limits constrain returned buckets and rankings, not silently the fleet being measured.
- Preserve source cutoffs, generation changes, retention boundaries, per-dimension coverage, and incomplete current buckets. A nonempty or capped raw response does not establish completeness.
- Use 5-minute buckets for 24h and hourly buckets for 7d where supported; cap returned chart points at 1,000 per series and lists at five on the landing page. Mark or exclude partial boundary buckets from comparisons.
- Compare equal-duration, nonoverlapping windows for the same scope. Require a documented minimum coverage policy and identify cohort changes; suppress percentage change when the prior value is zero or comparison is invalid.
- Cache by authorization scope, time window, metric/policy version, and source generation. Build independent widget failures into the envelope so a host collector problem does not blank SQL activity panels.

Start with manual refresh plus an optional 60-second refresh setting for relative live windows. Pause automatic refresh in hidden tabs and during an active request; use cancellation and retry backoff. Fixed historical ranges stay fixed. This refresh interval is a UI proposal, not a promised collection cadence.

## Frontend implementation map

- Extract `ServersPage.tsx` under `web/src/features/targets/` from the shared branch. Keep registration search, lifecycle filtering, capability discovery, paging, and Add server there. Overview's empty state can link to Add server.
- Add `web/src/features/overview/OverviewPage.tsx`, `overviewApi.ts`, `overviewTypes.ts`, `overviewModel.ts`, and `useOverviewAnalytics.ts`. Separate summary, issues, trends, and operational-risk components around the proposed response sections.
- Update `App.tsx` to mount only the active route's body. Restrict `useFleetEvidence` to Servers; load Overview through its aggregate API. Separate Overview scope from the inventory page cursor and search.
- Extend `dashboardModel.ts` for time range and scope, preserving existing target links. Add route state for issue filters and a query fingerprint/database where drill-down requires it; browser Back must restore the Overview range and scope.
- Reuse `ObservationChart` for simple observations, but extend or wrap it for bucket ranges, units, comparison series, dimension selection, coverage gaps, and accessible summaries. Do not flatten different volumes into one line.
- Add styles to the existing system. Use visible labels and status icons in addition to color; provide keyboard-accessible drill-downs and a table alternative for chart values.
- Keep the existing Analytics page as detailed analysis/evidence. Link to its relevant surface where possible; avoid creating another inventory-style analytics page.

## Delivery sequence

1. **Define and verify the evidence contract.** Trace each first-release signal from persisted records to the proposed aggregate, verify live sample availability, settle denominators and policies, and document unavailable signals. Verify counter epochs, host identity, query semantics, and operational timestamps before deriving metrics. Agree API envelopes and create clearly labelled synthetic fixtures.
2. **Build the fleet read model and split the routes.** Implement authorization-scoped summary, trend, and ranking queries with snapshot consistency and coverage metadata. Add SQL workload history/derivation support where absent. Version catalogs and add forward migrations if needed; update affected manifest/checksum inventories without rewriting applied migrations.
3. **Deliver the first analytics Overview.** Ship scope/range controls, coverage strip, four summary cards, Investigate first, workload and contention trends, resource pressure, and operational risks. Prioritize workload and waits over cosmetic charts. Unsupported panels explain their missing source; absent evidence is never plotted as zero.
4. **Add deeper comparisons.** Ship query regressions, baseline bands, sustained-pressure rankings, and capacity outlook only after history and confidence gates pass. Additional collector measurements such as SQL process CPU, memory grants pending, or broader transaction-log usage are separate scoped enhancements if the evidence audit finds them necessary.

Phase 3 is the first complete replacement for the duplicate Overview. A route split alone is not completion of this redesign. Forecasts and query regressions can follow without delaying a useful operational analytics page.

## Acceptance and validation

- Overview provides meaningful fleet analytics without selecting a server and contains no full registration table or lifecycle/discovery KPI row.
- Summary totals and top-five rankings cover every authorized target in scope, including fleets larger than 50 targets and targets with more than five alerts. Unavailable coverage is explicit.
- A single-instance fleet remains useful; an empty fleet shows an onboarding path. Disabled, retired, pending, unauthorized, shared-host, partial, stale, unsupported, retention-blocked, and service-error cases render distinctly.
- Deterministic aggregation tests cover duplicate events, repeated query observations, counter resets/epochs, bigint precision, mismatched dimensions, time boundaries, missing samples, zero denominators, cohort changes, and weighted latency/query metrics.
- Contract/integration tests verify target-scope isolation, no leakage through counts or caches, snapshot expiry, bounded responses, truncation, and refresh cancellation. Rankings and totals are reconciled against complete source fixtures.
- UI checks verify every drill-down, preserved range/scope, browser Back, rapid filter changes, keyboard navigation, chart alternatives, and layouts at approximately 390, 1440, and 1920 pixels.
- Performance target: no per-server browser request fan-out; one summary and a bounded number of trend/ranking calls per refresh. Measure cold/warm API latency at representative fleet sizes; target a usable cached first view within two seconds and stay within established backend execution limits. Record the tested scale before claiming fleet scalability.
- Run frontend typecheck/tests/build and the repository's applicable contract, migration, and Local validation gates for implemented changes. Revalidate representative live SQL and host evidence; synthetic chart screenshots alone do not prove collector readiness.

## Inspected source references

- [Current route and shared page rendering](../../web/src/App.tsx)
- [Current fleet evidence loading](../../web/src/features/targets/useFleetEvidence.ts)
- [Analytics client](../../web/src/features/analytics/analyticsApi.ts) and [server endpoints](../../src/SqlObserver.Server/AnalyticsEndpoints.cs)
- [Metric catalog and analytics contracts](../../src/SqlObserver.Domain/Analytics/AnalyticsContracts.cs)
- [SQL core collector](../../collectors/sql/engine.core.sqlserver17-windows.v1.sql) and [file collector](../../collectors/sql/database.files.sqlserver17-windows.v1.sql)
- [Current chart component](../../web/src/components/ObservationChart.tsx)
- [Dashboard implementation report](SqlObserver-Dashboard-Implementation-2026-09-05.md) and [synthetic workload validation report](SqlObserver-Synthetic-Workload-Validation-2026-09-05.md)
