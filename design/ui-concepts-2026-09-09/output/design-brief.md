# SQL Observer UI direction
September 9, 2026

These are proposed interfaces based on the local project, not screenshots of implemented changes. Server names, measurements and events are illustrative. The application remains pre-release.

## Project intent
SqlObserver helps DBAs understand Microsoft SQL Server health and performance using bounded, historical evidence. Monitoring is remote and agentless by default. Passive collection does not modify monitored SQL Server instances. PostgreSQL is the application's repository, not a monitored engine.

The project combines a React/TypeScript UI with .NET services for collection, APIs, analysis and read-only MCP access. This review inspected the current working files, including ongoing work, rather than assuming older milestone summaries describe the latest implementation.

## Capabilities found
- Fleet overview: scoped workload trends, ranked issues, contention, resource evidence and collection coverage.
- Server health: discovered capabilities, core metrics, database inventory, file usage and per-collector freshness.
- Activity: sessions, requests, waits, blocking, live refresh every 10 seconds, historical snapshots and protected query inspection.
- Query performance: Query Store and plan-cache rankings, query history and plan fingerprints. The query-performance projection marks SQL text and plan XML unavailable.
- Deadlocks: captured events, victim identities and lock relationships from system_health.
- Alerts: target-scoped active alerts, acknowledgement, state timestamps and notification-suppression evidence.
- Operations: backups, SQL Agent failures, TempDB and availability groups. Analytics also exposes host and replication evidence.
- Analytics: rollups, comparisons, baselines, forecasts, incidents, diagnostic search and evidence packets.
- Reports: the current UI exposes instance-health, performance-window, incident-evidence and capacity/readiness reports, with printable HTML and section CSV. Release qualification remains incomplete.
- MCP: an authorized, audited, allowlisted, read-only diagnostic surface. The concepts do not imply an in-product AI chat or autonomous remediation.

## Main usability finding
The existing UI exposes useful data, but many surfaces lead with raw evidence, long filters or internal status detail. Users repeatedly choose a target and navigate among separate feature views. The redesigned hierarchy emphasizes actionable exceptions, consistent scope and a readable path into evidence.

## Proposed screens
1. Fleet overview: ranked exceptions, coverage and a compact server list.
2. Server summary: workload, database health and collection visibility together. This absorbs the separate Health destination into the server workspace.
3. Live activity: a blocking chain with the selected session beside the table. A snapshot picker and preserved detail reduce disruption from refresh.
4. Query performance: rankings beside history and plan fingerprints, with provenance and unavailable content explicit.
5. Deadlock investigation: a directed lock cycle, victim highlight and the corresponding relationship table.
6. Operational health: category tabs for daily checks, with backups and failed jobs prominent. Replication moves here from Analytics.
7. Alert triage: a target-scoped queue with acknowledgement, event chronology and diagnostic links.

Analytics and Reports remain in navigation. They are capabilities of the broader product, while these seven images focus on everyday diagnosis.

## Interaction and presentation rules
Keep the selected server, database and time window when changing views. Every chart needs explicit units and aggregation semantics. Tables use aligned numeric columns and restrained highlighting. Teal indicates selection or current evidence; red and amber emphasize labeled exceptions. Gray distinguishes stale, unavailable, unsupported and unconfigured sources.

Show observation times near the data. A refresh timestamp cannot substitute for a source timestamp. Missing observations must not become zero or healthy. Treat acknowledgment as review, not resolution. Keep target-changing commands out of passive diagnostic workflows.

Use readable defaults with advanced filters in a secondary control. Preserve keyboard focus, visible focus styles, semantic headings and table headers when implementing. Status always needs a word or icon in addition to color.

## Implementation boundaries
These designs need frontend work. Shared scope, server search, cross-view evidence links, a blocking-chain filter, the lock-cycle diagram and consolidated tabs are proposals. Some summary and display fields require small joins or API enrichments. Friendly alert labels and severity need rule metadata. Backup age needs an explicit policy. SQL Agent labels need an authorized name or safe identifier.

The UI images are raster artwork. Slide titles, captions and explanatory copy are editable, while the depicted controls are not interactive. Preserve the images as visual direction, not pixel-perfect implementation specifications.

## Source map
- README.md: product scope, passive monitoring, architecture and pre-release boundary.
- web/src/App.tsx and dashboardModel.ts: navigation and target selection.
- web/src/features/overview/OverviewPage.tsx and overviewTypes.ts: fleet evidence and scope.
- web/src/features/health/TargetHealthPanel.tsx and healthTypes.ts: server health and collector freshness.
- web/src/features/activity/LiveSessionsPanel.tsx and liveActivityApi.ts: live/history behavior and query-detail handling.
- web/src/features/queries/queryPerformanceTypes.ts: metric/provenance contracts and unavailable text/plan content.
- web/src/features/deadlocks/deadlockTypes.ts: victims and lock relationships.
- web/src/features/alerts/TargetAlertsPanel.tsx and alertTypes.ts: acknowledgement and alert states.
- web/src/features/operations/operationsTypes.ts: operational projections.
- web/src/features/analytics/analyticsTypes.ts and AnalyticsSurfacePanel.tsx: analytics and replication.
- web/src/features/reports/ReportsPanel.tsx: report types and exports.

Sources above are repository-relative. Individual slides also include absolute source paths in their speaker notes.
