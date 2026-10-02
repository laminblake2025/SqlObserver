# Dashboard refactor checkpoint and next-work handoff

This checkpoint implements the dashboard frontend within the sources already available. It does **not** complete the full repository review and plan, or every feature in its interface direction. The latest human instruction confines implementation to the dashboard refactor. Backend and collector work below requires a scope decision before implementation.

Product checkpoint: `4482d348a5679240f2b3a8bd2aaf047bbd4cbc04` on `SQL-Diagnostic-Manager-Revamp`. This handoff is a documentation-only follow-up; the product validation below belongs to that immutable checkpoint.

The original [review and plan](SqlObserver-Review-and-Plan-2026-09-23.md) and [execution record](SqlObserver-Revamp-Execution.md) remain the full objective and historical record. They are local, untracked documents at this checkpoint. This file records current dashboard evidence without changing their historical claims.

## Delivery and evidence

Independent lanes owned theme/diagnostic work, interaction tests and accessibility/responsive checks. Later independent reviews owned current-inbox alert markers, retained-capture URLs and native-evidence fixtures. The lead integrated changes and resolved conflicts and review findings. Final runs used unchanged production assets and synthetic loopback fixtures.

| Requested deliverable | Evidence at the product checkpoint |
|---|---|
| Dark theme | Final browser coverage exercises controls, semantic states, keyboard focus and contrast. |
| Diagnostic-screen review | Source review reports identify the existing capabilities and missing dependencies. The final repair adds actual alert-fired markers, retained Waits capture URLs, strict shared UTC parsing and aggregate PLE recovery guidance. |
| Interaction tests | 123 passing browser cases, plus 17 existing browser regressions. |
| Accessibility/responsive checks | 146 passing browser cases across 320, 390, 768 and 1440 pixels. Tested states pass axe WCAG 2 A/AA, 2.1 AA and best-practice checks, keyboard controls and document-overflow checks. This is automated coverage, not manual assistive-technology certification. |
| Integration validation | TypeScript, Vite production build, authoritative permission templates and asset manifest pass. 540 frontend tests and 286 browser checks pass, with no skips or flaky cases in the final selected suites. Four offline permission scripts match canonical PowerShell output byte for byte. |
| Screenshots and requirement summary | [Gallery and detailed requirement table](../../artifacts/dashboard-refactor/screenshots/index.html): 212 fresh screenshots in 53 groups at four widths. [Machine-readable completion evidence](../../artifacts/dashboard-refactor/completion.json) binds the product commit, final assets and screenshot hashes. |

The evidence is local and ignored by Git. It must be retained or transferred through an authorized artifact channel for another machine to inspect it. No connected database tests were needed for the frontend changes. This checkpoint does not establish SQL Server fleet behavior, collector performance or packaged release certification.

### Mounted-component follow-up

The follow-up adds isolated [chart, health and alert harnesses](../../web/component-tests/README.md), using existing React, Vite, Playwright and axe dependencies. The combined strict TypeScript check, **24/24 mounted component cases** and **11/11 contract checks (10 runtime, one explicit lint)** pass. Chart tests exercise real props/callbacks, gaps, accessible values, shared crosshair, drag/cancel and local/UTC display. Health tests exercise collector loss/completeness, exact large counters, paging/context, sanitized failures and delayed responses. Alert tests exercise permission/state rendering, retry identity, bodyless 204, hidden/missing selections, paging and cancelled reads/actions. Chart and Health responsive cases check axe, focus and overflow at 320/1440 pixels, producing four fresh captures: chart [narrow](../../artifacts/dashboard-components/screenshots/observation-chart-320.png)/[wide](../../artifacts/dashboard-components/screenshots/observation-chart-1440.png) and health [narrow](../../artifacts/dashboard-components/screenshots/health-panel-320.png)/[wide](../../artifacts/dashboard-components/screenshots/health-panel-1440.png).

These results are separate from the product checkpoint's historical 540/286 counts and 212 screenshots. Health/alert API requests are fulfilled from registered local synthetic DTOs; unexpected API/external requests are rejected before reaching a proxy/backend. Product source and `dist` remain unchanged; three legacy test files change. [The follow-up record](../../artifacts/dashboard-components/completion.json) binds its commit and test/harness/screenshot hashes; [the report](../../artifacts/dashboard-components/report/index.html) contains final cases and axe attachments. The first chart run's 7/8 and expanded run's 22/24 results were dropdown-selector issues repaired with exact combobox roles, without weakening assertions. Final permitted subprocess cleanup exited normally; the earlier sandboxed chart run required stopping only its verified test server after workers exited.

| Replaced source-check bundle | Behavioral replacement |
|---|---|
| Health bounded evidence/UI fields | Native aggregation retains collector DTOs and exact Int64 strings; mounted collector states/loss, completeness guards, native row counters and bounded database/file context are exercised. |
| Health route/error/abort expressions | Native same-origin route/limit/headers, safe HTTP/malformed-response errors and pre-cancellation tests; mounted retry, failed page preservation and old-server abort/late-response cases. |
| Alert target/state/UUID/time/page expressions | Production parser executes malformed target, 101-row, state, UUID, UTC, cursor and numeric-value cases. |
| Alert acknowledgement expression | Real panel gates the action by supplied permission flag and selected firing state; retry/204 and changed-selection cancellation exercise actual callbacks/transport. This does not establish server authorization. |
| Alert nullable value/terminal cursor expressions | Production parser demonstrates null versus numeric zero and normalized terminal cursor. Existing streamed-size and paging runtime cases remain. |
| Alert loading/error/load-more expressions | Held native response demonstrates pending-control disabling, snapshot-change refusal and accessible Retry recovery. |

The retained health app-wiring/plaintext/rendering source constraints are explicitly named architecture/security lint. They do not count as behavioral proof. Unrelated legacy UI test migration remains incomplete.

## Interface-plan accounting

These statuses describe the original interface requirements rather than treating current source limitations as completion of the full plan. Source references point to the inspected product checkpoint; line numbers can move.

| Plan requirement | Current delivery | Remaining scope or evidence |
|---|---|---|
| Global time, Live/Rewind, local/UTC and window selection | Shared frame, exact UTC bounds, fixed investigation URLs, timeline/keyboard selection. See [App](../../web/src/App.tsx), [window model](../../web/src/features/overview/overviewModel.ts) and [chart](../../web/src/components/ObservationChart.tsx). | Historical panels must keep declaring latest-only sources rather than claiming they follow Rewind historically. |
| Answer before evidence | Baseline KPIs, investigate-first findings, blocking evidence and scoped links. See [server dashboard](../../web/src/features/health/ServerDashboard.tsx). | Correlations are observations, not established causation. Additional sources are required for more complete diagnoses. |
| Timeline events | Deadlocks, native current-inbox alert-fired times, Agent failure history, configuration/option observations and plan comparisons. | Complete alert lifecycle, restart and failover event coverage is incomplete; see source handoff below. |
| Quiet evidence and recovery guidance | Named source dots, compact footer and expandable reference details; visible partial/missing/failed states. | Do not collapse errors or turn lack of evidence into a healthy verdict. |
| Role-aware UI | `/me` target grants gate actions and protected query text/plans; late content is fenced after context or role changes. See [access model](../../web/src/features/targets/meApi.ts). | New mutations and assistant tools need matching target authorization and audit semantics. |
| Fleet grid and pinned strip | Environment/tag grouping, observed-exception ordering, scoped signal links and authorized compact navigation. See [fleet grid](../../web/src/features/overview/FleetServerGrid.tsx). | Grid evidence is bounded to the loaded Overview response; it is not a transactional fleet snapshot. |
| Fleet alert inbox | Grouped current inbox, acknowledgement with retry identity, exact alert selection and investigation links. | Snooze and assignment remain incomplete. |
| Server dashboard | Shared charts, baseline/reference context, event selection, investigate-first and live blocking tree. | Full event/source parity remains incomplete. |
| Queries | Ranking/trends, regression comparisons, protected text/plan inspection, history and selected-plan hourly waits. | Source/collection qualification is separate from frontend fixture validation. |
| Waits | Category trends, native deltas, retained captured-session links and exact capture restoration. | Captured sessions do not prove who caused a historical aggregate wait bucket. |
| Sessions/blocking/deadlocks | Exact request and chain identities, retained snapshots and deadlock restoration, inert role-gated protected content. | Collection completeness and retention limit available historical evidence. |
| Resources | SQL scheduler CPU, SQL memory, aggregate/NUMA PLE, file latency, selected-file allocation/autogrowth and SQL-reported volume capacity. | Target-host CPU comparison and historical volume comparison need additional source contracts. |
| Health checks | Existing backup/RPO, Agent, HA, TempDB and configuration/database-option surfaces with scoped navigation. | Source completeness, long-running-job parity and live platform qualification must be assessed in collector work; frontend checks cannot prove them. |
| Administration | Target rules, destinations, server grouping, one-off maintenance calendar/create/edit/cancel and offline permission generator. | Recurrence and rule-template deployment by group remain incomplete. |
| Ctrl+K | Authorized server, database, query-hash and current-alert destinations. See [palette](../../web/src/components/CommandPalette.tsx). | Lookup/list caps and missing selections remain explicit. |
| Live updates with pause | One workspace clock with source cadences, pause, hidden-window suppression and wake refresh; no refresh-key remount of diagnostic surfaces. See [clock](../../web/src/liveRefreshSchedule.ts). | SignalR is not required by the plan when shared polling is used. |
| Shareable URLs | Target/window and supported alert, query, resource, request, chain, deadlock, admin, timeline and retained Waits selections survive reload/history. | Invalid or unavailable exact identities remain visible; no substituted newest selection. |
| Chart replacement | Date/time axes, multiple series, reference band/threshold and pointer/keyboard inspection with shared window selection. | A chart does not infer comparable evidence where a source has no matching data. |
| Incidents | Shared workspace saves fixed window, existing evidence and notes with retry identity and thread-linked inspection/reporting. See [incident workspace](../../web/src/features/incidents/IncidentWorkspace.tsx). | Incident evidence is bounded to the existing saved source contracts. |
| Optional assistant | No assistant panel is delivered. | Product/transport/model/authentication decisions and read-only tool integration remain open. |
| Frontend infrastructure/testing | Shared bounded fetch, request coalescing/cancellation, server-approved public caching, mounted Playwright interactions, axe/model tests and isolated chart/health/alert components. Six legacy bundles are replaced with behavior. See [fetch client](../../web/src/apiClient.ts). | Retained wiring/security checks remain lint. Migration of unrelated legacy UI source assertions is **not** complete. Private/no-store telemetry and identity responses deliberately bypass completed-response caching. |

## Remaining dashboard dependencies

The following work is a handoff, not an implementation authorization. Keep each feature cohesive across contract, repository, service, collection where needed, and UI. Apply database changes only through new forward migrations. Write a targeted regression when behavior changes; run live PostgreSQL/SQL Server checks when the affected contract requires them, rather than rerunning unrelated suites.

### Alert inbox actions

The inspected [route table and response projection](../../src/SqlObserver.Server/AlertEndpoints.cs) (lines 14–47), [frontend DTO](../../web/src/features/alerts/alertTypes.ts) and [action client](../../web/src/features/alerts/alertApi.ts) provide current-alert reads and acknowledgement. They do not provide snooze or assignment fields/actions. Current active rows omit episode ID, revision, assignee, snooze expiry and action history. The acknowledgement backend already accepts optional expected episode/revision and a request digest, while the frontend sends a stable operation ID. [Acknowledgement retries](../../web/src/features/alerts/acknowledgementAttempts.ts) and [migration 0047](../../database/migrations/0047_alert_admin_first_operation.sql) are precedents, not permission to edit an applied migration.

- **Snooze:** define whether it suppresses delivery, changes inbox visibility, or affects evaluation; never imply resolution. Supply target/alert/episode identity, revision, server-owned expiry, actor/reason and history. Require same-target Operator/Target Administrator access, stable operation plus payload digest, stale-episode/revision conflicts, and authoritative readback. Prove bounded UTC expiry, half-open end behavior, unsnooze and new-episode policy.
- **Assignment:** define eligible assignee identities and assign/reassign/unassign semantics. Persist current owner and actor/time/change history. Enforce target role and assignee eligibility, revision/episode fences, retry identity and same-operation/different-payload conflicts. Define owner behavior after resolution and new firing cycles.

These features need durable state and service/repository tests before UI controls can claim they work. Adding buttons or local-only state would not fulfill the plan.

### Recurring maintenance

[Maintenance routes](../../src/SqlObserver.Server/AlertEndpoints.cs) currently create/update/cancel one-off target windows. [The parser](../../web/src/features/alerts/alertAdminListsApi.ts) accepts exact one-off fields; [the calendar](../../web/src/features/alerts/TargetMaintenancePanel.tsx) explicitly states that recurrence is not supplied (line 179). [Domain windows](../../src/SqlObserver.Domain/Alerting/AlertContracts.cs) are microsecond-aligned UTC, positive, at most seven days and half-open. The existing editor rejects nonexistent local times and discloses its repeated-DST-time choice.

Next contract: durable series ID/revision, recurrence definition, named timezone, explicit DST gap/fold policy, bounded occurrence expansion and provenance. Define edit/cancel-one versus edit/cancel-future, preserve past/cancelled occurrences, and prevent cancellation from being undone by expansion. Keep Target Administrator access, digest-based retry identity and revision fences. Validate delivery suppression at start/end, overlaps and dispatch races while collection/evaluation continue. Do not infer a fleet-wide maintenance calendar from loaded target pages.

### Rule templates by group

[Rule mutation routes](../../src/SqlObserver.Server/AlertEndpoints.cs) and [frontend mutations](../../web/src/features/alerts/alertRuleAdminActions.ts) are per-target, revision-checked operations. The inspected contracts do not supply a deployment job, frozen membership, per-target result or inherited rule provenance. [Fleet grouping](../../web/src/features/overview/FleetServerGrid.tsx) is a loaded authorized display; multi-tag targets can appear more than once.

Next contract: a preview with deduplicated frozen target membership, per-target catalog compatibility, existing revisions and template/version provenance. Authorize every target; define bounded batch and partial-failure semantics; retain deployment/per-target operation IDs and payload digests. Return per-target success/conflict/failure, server timestamps and audit history. Prove retries do not duplicate rules, changed revisions are not overwritten and membership changes cannot silently expand a deployment. Define update/rollback semantics before implementation.

### Target-host and SQL CPU

[Resources](../../web/src/features/resources/TargetResourcesPanel.tsx) (line 219) and [Dashboard](../../web/src/features/health/ServerDashboard.tsx) (line 162) show SQL scheduler utilization, explicitly distinct from total process CPU or collector-host CPU. [The pinned engine query](../../collectors/sql/engine.core.sqlserver16-windows.v1.sql) supplies visible-scheduler counters; the existing rate derivation is epoch-sensitive. [The volume contract](SqlObserver-Sql-Volume-Capacity-Contract.md) identifies legacy `host.*` measurements as collector-host evidence.

Next decision: specify the intended comparison before collecting it. A paired chart needs verified physical-host identity, target revision, source time, denominator and restart/failover epoch rules for both measurements. Prove a separate collector and SQL host cannot be conflated, and preserve gaps and source labels in selected/comparison windows. Never relabel scheduler utilization as total SQL process CPU.

### Historical volume capacity

[The volume panel](../../web/src/features/resources/SqlVolumePanel.tsx) (line 97) provides Live SQL-reported capacity and a forecast derived from complete daily snapshots, while Rewind discloses unavailable historical capacity. [The API](../../web/src/features/resources/sqlVolumeApi.ts) binds a latest target-revision/snapshot page; it does not accept a selected history window. A forecast scalar cannot reconstruct historical/comparison observations.

Next contract: explicit selected window, source cutoff, target revision, opaque volume identity/epoch, timed capacity observations, loss/coverage and bounded continuation. [Existing identity rules](SqlObserver-Sql-Volume-Capacity-Contract.md) require an epoch change when the physical SQL host changes. Define comparison eligibility across failover, volume replacement and capacity changes. Verify target/window/revision mismatch refusal and missing-point gaps before enabling Rewind history.

### Historical wait attribution

[Waits](../../web/src/features/activity/TargetWaitsPanel.tsx) shows aggregate server deltas and independent Query Store evidence. [Captured sessions](../../web/src/features/activity/WaitCapturedSessionsPanel.tsx) (line 102) show native waits at one retained capture and explicitly disclaim historical attribution. The capture source is bounded by 24-hour retention, at most 100 offered capture records and 11 pages/512 session rows; an exact pin can be restored independently of the offered list.

Next decision: distinguish correlated capture evidence from measured attribution. The latter requires bucket/category/target/source epoch, durable request identity, attributed duration and accounting/coverage. Disclose unattributed waits and sampling/truncation. Prove session-ID reuse cannot substitute another request and expired/out-of-window selections cannot become current sessions. Temporal coincidence alone is insufficient to claim responsibility for an aggregate bucket.

### Durable timeline events

[Timeline sources](../../web/src/features/health/useDashboardTimeline.ts) retain observation/detection timing and native bounds. [Current-alert loading](../../web/src/features/health/useDashboardAlerts.ts) is bounded to five pages/500 rows. [The event model](../../web/src/components/dashboardTimelineModel.ts) includes fired markers only for current firing/acknowledged inbox rows with native `firedUtc`; it does not reconstruct resolved cycles. [The Dashboard disclosure](../../web/src/features/health/ServerDashboard.tsx) (line 99) names absent restart/failover and complete historical transitions and limits display to the latest 120 loaded events. Plan comparison observation time is not a measured plan-switch instant.

Next contract: durable restart/failover/alert-cycle projections with stable event/cycle identity, target/revision, affected host/replica, transition state, explicit occurrence/detection/observation basis, window-bound paging, retention and coverage. Prove multiple transitions and resolved episodes remain retrievable. An empty/failed source must not imply all-clear. Then extend adapters, timeline kinds and exact selection URLs together.

### Optional assistant

The inspected [dashboard destination registry](../../web/src/dashboardModel.ts) and [server dashboard](../../web/src/features/health/ServerDashboard.tsx) do not provide an assistant panel. This is a statement about those UI surfaces, not repository-wide absence of infrastructure.

Next decision: approve the service/transport, model, authentication, target authorization, read-only tools and diagnostic-data sharing boundary. Define a context/response contract with frozen target/revision/database/window/evidence cutoff, evidence citations, unavailable/truncated status and cancellation/error behavior. Clear late responses after context change; keep protected query content subject to its existing role and audit policy. Implement the fixture-backed panel only after these choices, then validate its authorized service separately.

### Component-test migration

The browser suites exercise mounted components and interaction behavior, but the plan explicitly asks for component tests and replacement of regex-over-source assertions. The isolated chart/health/alert harness and native contract cases replace six legacy bundles. Existing model tests remain useful; there was no corresponding chart source assertion to remove. Remaining source assertions are lint, not behavioral proof. Next, migrate another high-value dashboard component with meaningful state transitions, establish equivalence to its specific checks and retire only those redundant assertions. Do not claim a complete migration after these components or remove architectural/security lint solely to improve counts.

## Gates outside the current frontend slice

- Full Step 0/1/2 completion remains unproven. This dashboard checkpoint does not supersede the original test setup, security/correctness, MCP, storage, scheduler, API, migration or platform requirements.
- Packaged release certification is incomplete: three SBOM input hashes require reviewed manifest and chained certification-pin maintenance. Frontend checks exclude M12 release-generator contracts; no pin or assertion was weakened.
- GitHub publication and any remote package upload must satisfy the existing approval boundary. Automatic approval review previously rejected code upload for missing explicit authorization. No push/upload was retried for this checkpoint.
- Connected lab qualification is separate. SSH access alone does not establish that the correct native multi-version tests ran or passed.

## Resume instructions

1. Inspect the current branch and this checkpoint before relying on earlier counts or file-line references. Preserve existing untracked design, plan and execution files.
2. Resolve whether the next authorized scope remains frontend-only or includes one of the backend/collector dependencies below. Do not silently expand the latest dashboard scope or replace the full revamp objective with a smaller definition of completion.
3. For an approved feature, implement one complete vertical slice with exact target/source/time identity and truthful unavailable states. Reuse existing authorization, idempotency, audit and URL patterns.
4. Validate the changed behavior against final code; keep screenshots/results bound to that commit and clearly distinguish fixture, connected database and release evidence.
