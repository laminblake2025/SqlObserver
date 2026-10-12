# Repository review and work plan

This is a handoff from a full repository review on 2026-09-23. The review compared SqlObserver with its stated goal: a SQL Server monitoring and diagnostics platform comparable to Idera SQL Diagnostic Manager. It covers what's wrong, what to fix first, what to leave for a refactor, and how the interface should change.

**Scope and limits.** The review was static only. The machine used had no .NET SDK, Node, pnpm, pwsh 7, Docker, PostgreSQL or SQL Server, so nothing was built, run or tested. Findings marked ✔ were checked directly in the source. The rest come from careful reading, and some from estimates (marked *inferred*). File:line references are for commit `55babbd`. Check each reference before changing code, because the lines may have moved.

## Summary

The foundations are well built: bounded passive collectors, checksum-pinned SQL, fenced leases, audit, forced row-level security and no hidden sample loss. It isn't yet a SQL DM-class product, for three reasons:

1. **Several health signals are wrong.** Backup age, blocking, Agent failures and AG health all have defects.
2. **It won't scale to a fleet.** The collector runtime has 4 hard-coded slots, storage grows about 1 GB per instance per day, and retention doesn't work.
3. **Coverage is a thin slice of SQL DM.** There's no query text or plans, no OS CPU/memory from SQL, no index or statistics health, no config drift and no error log. Alerting covers `engine.*` counters only.

The MCP layer is fully wired: all 25 tools reach real services and SQL. It has never been exercised end to end, and it breaks quickly in real use.

## Recommended sequence

1. **Step 0: test setup.** Nothing below can be verified without it.
2. **Fix batch.** Small, local, high-severity fixes, each with a regression test.
3. **Refactor, one feature area at a time.** Let the target interface and missing capabilities set the order.

Only fix now what survives any refactor. Anything that's really a redesign (retention, scheduler, query-performance schema, API contracts) belongs in the refactor. Fixing it now means doing the work twice.

---

## Step 0: test setup

- [ ] Install the toolchain on the dev machine:
  - .NET SDK `10.0.203` (see `global.json`)
  - Node 22.22+ and pnpm 11.19
  - pwsh 7.5+
  - Docker with Linux containers
  - PostgreSQL 18.4, or run it in Docker
  - a SQL Server 2019/2022/2025 lab instance
- [ ] Run `pwsh ./tools/validate.ps1 -Profile Local` and record the baseline result.
- [ ] Add a Linux CI job with a PostgreSQL 18 service container that runs the `RequiresPostgreSql` tests. The Local profile excludes them (`tools/validate.ps1:2263-2283`), so today CI never runs any repository or row-level-security logic.
- [ ] Replace the `tools/dev-up.ps1` and `tools/seed-lab.ps1` stubs with:
  - a docker-compose PostgreSQL;
  - migrations;
  - synthetic seed data;
  - a Development-only auth handler that the app refuses to load outside Development;
  - a Vite `/api` proxy.
- [ ] Add the fixture servers (`web/tools/dashboard-fixture-server.mjs`, `overview-fixture-server.mjs`) to `package.json` scripts.
- [ ] CI hygiene: cache NuGet and pnpm, publish test results, and run push builds on `main` only (pull request branches currently build twice).

Note that many "security", "performance" and "end-to-end" tests only check that source files contain certain text. For example, `M12ReportsCompositionTests` asserts that `Program.cs` contains "MapReportEndpoints". Treat them as lint, not behavior coverage.

---

## Step 1: fix batch

Each item is small. Write a failing test first, then fix. Database changes go in new forward migrations; never edit an applied migration.

### Security

- [ ] ✔ **HIGH: cross-target reads.** Several services check "caller has a read role *anywhere*" and "target is in the *union* of every role's scope" as separate checks. The union is built at [AuthorizationContext.cs:178](../../src/SqlObserver.Domain/Authorization/AuthorizationContext.cs). Result: a Viewer on server A who is also Auditor on server B can read B's deadlocks, query performance, alerts and operational health.
  - Affected: `DeadlockProjectionQueryService.cs:36`, `QueryPerformanceApiQueryService.cs:18`, `AlertServices.cs:15-16,23-24`, `OperationalHealthQueryService.cs:29-31`, `AnalyticsServices.cs:49-50`.
  - Correct pattern already in use: `HealthProjectionQueryService.cs:177`, `ActivityProjectionQueryService.cs:285`, `McpQueryServices.cs:46`, which all call `CanAccess(role, targetId)`.
  - Fix: add one `RequireAny(targetId, roles)` helper and remove union-scope checks from target decisions. Copy the mixed-grant test from `tests/SqlObserver.UnitTests/LiveActivityTests.cs:75` for each service. Add a route-inventory test that asserts authorization metadata on every endpoint.
  - Related: the Overview returns 403 for the whole fleet when a user's Auditor or SecurityAdministrator scope is wider than their Viewer scope (`OverviewQueryService.cs:30-42`, `ObservationTargetQueryService.cs:50-57`).
- [ ] **HIGH: unauthenticated audit writes.** For oversized or malformed report POSTs, `RequestBodyLimitMiddleware` (lines 196-279, anonymous fallback at 269) writes an `anonymous` audit row before authorization and rate limiting run. Middleware order is set in `Program.cs:142-146`. Fix: move report pre-validation after `UseAuthorization`, or drop the anonymous audit write.
- [ ] **MEDIUM: CSRF.** `ReadBoundedJsonAsync` (`AnalyticsEndpoints.cs:144-145`) doesn't check Content-Type, so a cross-site `text/plain` form can reach the analytics and retention mutations with the victim's Windows credentials. Retention attestation only checks the request digest when one is supplied (line 143). Fix: require `application/json`, reject unsafe methods whose `Origin`/`Sec-Fetch-Site` isn't same-origin, and add CSRF to the threat model.
- [ ] **MEDIUM: repository privileges.**
  - About 18 functions are still executable by PUBLIC, because the per-schema `ALTER DEFAULT PRIVILEGES` at `0001:136-139` has no effect.
  - Migrations 0036 and 0077 have no `SET LOCAL ROLE sqlobserver_migrator`, so their SECURITY DEFINER objects are owned by the bootstrap login.
  - Fix in a forward migration: re-own those objects, revoke PUBLIC EXECUTE globally, and add a catalog test.

### Collector correctness

References are to the `sqlserver16` variants; the 15 and 17 files are effectively identical.

- [ ] ✔ **Backup age never resolves.** `time_zone_offset_minutes` is hard-coded to NULL at [backups.status…sql:10](../../collectors/sql/backups.status.sqlserver16-windows.v1.sql). Use `backupset.time_zone * 15`, where 127 means unknown. While there:
  - exclude copy-only backups from the latest-full pick;
  - left-join `sys.databases` so databases with no backup appear;
  - match on `database_guid` rather than name.
- [ ] ✔ **Parallel queries show up as blocking cycles.** [blocking.current…sql:13-15](../../collectors/sql/blocking.current.sqlserver16-windows.v1.sql) doesn't exclude `blocking_session_id = session_id`. Exclude it, and consider excluding CXPACKET/CXCONSUMER. The C# cycle detection is at `SqlServerActivityCollectors.cs:785-791`.
- [ ] **Agent failures have no event time.** The query doesn't select `run_date`/`run_time`, and the C# stamps each row with collection time (`SqlServerM9AgentCollector.cs:17,31`). Step rows and job-outcome rows are both kept, so each failure counts twice.
- [ ] **AG state mapping.** `SafeState` maps SUSPECT, RECOVERY_PENDING, NOT_HEALTHY and others to `"unknown"` (`SqlServerM9AvailabilityGroupsCollector.cs:45-54`). The full fix, reading all replicas on the primary, is in the refactor list.
- [ ] **Replication latency clamp.** Latency above about 35.8 minutes is reported as `int.MaxValue` ms, about 24.8 days (`replication.health…sql:23-28`).
- [ ] **Query Store permission error.** Error 916 (no database user) is classified as a read failure instead of permission denied (`SqlServerQueryPerformanceCollector.cs:430,447,450`). `tools/generate-permissions.ps1` doesn't create users in user databases, so Query Store silently falls back to the plan cache.
- [ ] **Network errors counted as permanent.** Errors 11001, -1, 26, 258, 121, 10061, 1205 and 1222 are classified as permanent, so the circuit never opens and dead targets keep using collector slots (`SqlServerHealthCollectors.cs:219-222`, `CollectorExecutionEngine.cs:357-360`, `SqlServerOperationalHealthCollectors.cs:70-72`).
- [ ] **Read and write stalls summed.** File I/O adds read and write stalls into one value (`SqlServerHealthCollectors.cs:673`). Keep them separate.

### Analytics and alerting correctness

- [ ] ✔ **Baselines keep 1 of 168 hourly rows.** `hour_of_week` isn't in the `analytics.metric_baseline` primary key (migration `0014:379`, re-keyed at `:1236`), and the insert uses `ON CONFLICT DO NOTHING` (`:1462-1464`).
- [ ] **Orphaned analytics jobs are never reclaimed** (`0014:1715-1719`). One stuck job also blocks retention permanently; see ✔ `0014:1012`.
- [ ] **Alert evaluation.**
  - One out-of-order sample raises an error (`0052:40`) that stops evaluation for every target (`AlertEvaluationWorker.cs:40-58`). Isolate errors per target.
  - One normal sample clears an alert, and the shipped rules have zero hysteresis. Add a clear threshold, clear confirmation and sensible defaults.
- [ ] **Storage forecast horizon.** The horizon is never set, so the "forecast" is for today (`AnalyticsDerivationWorker.cs:167`). A drop of more than 50% in free space resets the history (`AnalyticsAlgorithms.cs:206`), which is backwards for capacity planning.
- [ ] **Npgsql NULL casts.** `ExecuteScalarAsync` results are cast with `?? default`, which throws on `DBNull` (`PostgreSqlAlertRepositoryPort.cs:103,298,311,322,334,349,407`).
- [ ] **Rollup paging.** The look-ahead row is dropped when the limit is 100,000, so `hasMore` is never true (`PostgreSqlAnalyticsRepositoryPort.cs:331`).

### MCP: make it work

- [ ] ✔ **Results with more rows than `limit` throw when no cursor key is set.** `SQLOBSERVER_MCP_CURSOR_KEY` is opt-in and its absence isn't logged ([McpComposition.cs:19](../../src/SqlObserver.Mcp/McpComposition.cs)). The throw is at [McpRuntime.cs:613](../../src/SqlObserver.Mcp/McpRuntime.cs). The default `limit` is the maximum, so ordinary calls fail.
  - Bind the key from configuration as well as the env var, and warn at startup when it's missing.
  - Without a signer, return the first page with `hasMore: true` instead of throwing.
- [ ] **Cursor length.** Query-performance cursors are estimated at about 1,080 characters against a 1,024 limit (*inferred*, `McpRuntime.cs:529,542`). Raise the limit and the schema `maxLength`, or make the envelope smaller.
- [ ] **Store the effective window in the cursor.** When `toUtc` is left to default to "now", page 2 fails because "now" has moved (`McpQueryServices.cs:104` and the equivalent deadlock and query-performance checks).
- [ ] ✔ **`get_backup_status` breaks its own schema.** `backupAtUtc` is marked required (`McpRuntime.cs:1043`) but is null for a never-backed-up database (`:749`). Add a real JSON Schema validator (e.g. JsonSchema.Net) to the mapper tests, with fixtures that contain many nulls. The hand-rolled checker in `McpBoundaryScaffoldTests.cs:137-155` ignores `required`.
- [ ] ✔ **Invalid JSON-RPC error.** The oversize-response error writes the string `"response_too_large"` as the JSON-RPC `error.code`, which must be an integer (`McpHttpAuditBoundary.cs:342`). Also, `WriteSafeFailureAsync` (`:348-374`) returns a body that isn't JSON-RPC.
- [ ] ✔ **Blocking output omits blocker fields.** `BlockerSessionId`, `RootBlockerSessionId`, `ChainDepth` and `ChainState` are left out (`McpRuntime.cs:721`), although the DTO carries them.
- [ ] **Mislabelled fields:**
  - the AG `role` for database rows is a fingerprint (`:754`);
  - `latencyMilliseconds` is cumulative stall time (`:763`);
  - `databaseName` and `jobName` are hashes or GUIDs (`:749-750`).
- [ ] **Stdio bridge ([McpStdioBridge.cs](../../src/SqlObserver.Mcp/McpStdioBridge.cs)):**
  - Its own stdio server has no `ServerInfo` or instructions (`:55`).
  - If the server is unreachable, it crashes with a stack trace. Print one clear stderr line and exit non-zero instead.
  - Suppress host status messages.
  - Consider starting the stdio host before connecting to the server.
- [ ] **Protocol check.** Verify the SDK 2.2.0 stateless flow for protocol `2026-07-28`, which drops `initialize` in favour of `server/discover`. Confirm `NegotiatedProtocolVersion` and `ServerInfo` are populated. The release test's `initialize`-based assertions (`M12McpProtocolCertificationTests.cs:543-555,652,670,689-690`) probably fail today (*inferred*).

### MCP: make it useful to an LLM

- [ ] ✔ **Tool descriptions.** All 25 tools share the description "Read-only SQL Observer diagnostic projection." (`McpRuntime.cs:53`), and no parameter has a description.
  - Write a real description for every tool and parameter.
  - Add an `enum` for `metricKey` (the valid keys are in `AnalyticsContracts.cs:63-72`).
  - Give tools human-readable titles.
  - Expand the server instructions: call `list_instances` first, UTC timestamps must end in `Z`, and state the window limits.
  - Changing descriptions changes the catalog digest, so re-approve it once in `McpRuntime.cs:43-44`, `McpStdioBridge.cs:18`, the release contract and the tests.
- [ ] **Field-level validation errors.** Replace "The request is invalid." with messages that name the field (`McpRuntime.cs:254-255,285`). Align the MCP window cap with the service caps: `search_diagnostic_events` accepts 31 days, but the service caps at 7 (`McpQueryServices.cs:103`).
- [ ] **Unreachable tool.** `get_incident_evidence` needs a `threadId` that no other tool returns. Add `list_incidents`, and a metric-catalog tool.
- [ ] **Wait data.** Return wait deltas, not totals since restart (`McpRuntime.cs:718`). The DTO already carries the deltas.

### MCP tests to add

- [ ] **In-process CI test.** Use `WebApplicationFactory<Program>` with the real pipeline and only the authentication scheme replaced, plus Testcontainers PostgreSQL. Drive it with the SDK `McpClient` and:
  - call all 25 tools;
  - page each paginated tool to the end and check there are no duplicates or gaps;
  - run with and without the cursor key;
  - validate `structuredContent` against `outputSchema`;
  - assert exactly one audit row per call.
- [ ] **Bridge CI test.** Add a seam so `McpStdioBridge.RunAsync` can take an `HttpMessageHandler` and stream transport. Test:
  - `tools/call` round trips;
  - identity mismatch exits with code 3;
  - server-down exits cleanly;
  - stdout contains only protocol frames.
- [ ] **Nightly or lab test.** Localhost HTTPS with real Negotiate. Launch the published exe through `StdioClientTransport` and call every tool.

**Manual smoke test**, once HTTPS, role bindings and the cursor key are configured:

```powershell
$env:SQLOBSERVER_MCP_ENDPOINT = 'https://HOST:5443/mcp'
npx @modelcontextprotocol/inspector          # stdio mode, command = path to SqlObserver.McpStdio.exe
claude mcp add sqlobserver -e SQLOBSERVER_MCP_ENDPOINT=https://HOST:5443/mcp -- C:\path\SqlObserver.McpStdio.exe
```

Then check the `audit.mcp_invocation` rows. Expect one spurious `denied` row per call from the Negotiate 401 handshake (`McpHttpAuditBoundary.cs:241-242`).

---

## Step 2: refactor, one feature area at a time

Don't patch these now; redesign them. Start with the **server dashboard plus activity and blocking**, because it touches the collector, schema, API, MCP and UI and sets the pattern for the other areas.

### Storage and retention

- ✔ Retention is effectively inoperable:
  - Policies ship disabled and can only be run by hand.
  - Detach and drop refuse to run while any analytics job in the fleet is active (`0014:1012,1030`).
  - The policies point at `*_v2` parents that nothing writes (`0014:847-891`). About 5,000 empty partitions a year are created and never dropped.
- The M5 activity partitions aren't recorded in `system.partition_registry`, so retention can't see them.
- High-volume tables with no partitioning or retention: `collection_run*`, the inventory and file snapshots, `events.query_performance_*`, `events.deadlock_*`, and the alerting queue, history and replay tables.
- **Decide which tables are canonical,** then build a leased, scheduled retention worker with defaults per data class, and a dependency check per partition instead of the fleet-wide gate.
- **Volume:** about 1 GB per instance per day, about 70% of it wait stats. Every wait type is stored every 30 s, including zero-count rows (`waits.server…sql:3-15`). That's about 9–18 TB at 100 instances × 90 days. Filter at the source.

### Collector runtime

- Global concurrency is hard-coded to 4 (`CollectorServiceRegistration.cs:179`), in batches of 16, and each batch waits for its slowest item (`CollectorScheduler.cs:141-154`). The realistic ceiling is 10–50 instances per process.
- `list_due_collector_work` only lists due work; it doesn't claim it (`0013:66-79`). Use `FOR UPDATE SKIP LOCKED` and dispatch continuously within a configurable limit.
- Add a connectivity breaker per target, and jitter.
- Live activity holds each slot with `Task.Delay`, so 200 targets are sampled every 200 s instead of every 10 s (`LiveActivityWorker.cs:24,75-76`).
- **Clock skew causes a retry loop.** A target whose clock is off by more than 5 minutes fails every commit. The next begin resets the circuit to closed without moving `next_due_at` (`0008:1398-1462`). Record skew as a visibility gap instead.
- **Incremental reads:**
  - deadlocks: an XE bookmark (`initial_file_name`/`initial_offset`); today it re-reads up to about 1 GB every 30 s;
  - Query Store: an interval watermark;
  - plan cache: deltas, with `TOP` applied before `dm_exec_plan_attributes`.
- Set `MultiSubnetFailover` and `ApplicationIntent` for AG listeners.

### Query-performance schema

`query_performance_observation` has no `instance_id`, so ranking scans every instance's rows (`0070:15-27`, `0043:7-9`). This is why `get_top_queries_projection` has been rewritten 6 times. Add `instance_id`, index on `(instance_id, interval_end)`, and partition by time.

### Fleet Overview

It runs about 13 live reads per target, 8 at a time (`OverviewQueryService.cs:43-47`). With one server in the lab it already took 2.7–6.8 s. Serve it from a precomputed latest-status table.

### API

- **Five error shapes.** Move to RFC 7807 ProblemDetails with `code` and `correlationId`, mapped in one place. Stop mapping invariant failures to 409, repository contract failures to 400, and oversized responses to 413.
- **Five cursor formats.** Replace them with one signed codec.
- **Time parameters.** Use `fromUtc`/`toUtc` everywhere, with consistent defaults and caps.
- **Timeouts.** Align the 15 s global request timeout with the 20–30 s Overview budgets.
- **OpenAPI.** Add it with typed DTOs and generate the TypeScript client.
- **Missing endpoints:** `/me`, lists of alert rules, maintenance windows and destinations, a fleet alert list, and liveness/readiness.
- **Idempotency.** The web client generates a new `operationId` on every acknowledge click (`alertApi.ts:15`), which defeats retry idempotency.

### Code structure

- **Handlers packed onto one line.** `AnalyticsEndpoints.cs` has lines up to 1,692 characters. Across the Server, Application and Reporting projects, 162 lines are over 300 characters. Use one method per route and add a line-length analyzer.
- **Oversized files:**
  - `TargetActivityEndpoints.cs`: 1,031 lines, with its own cursor codec;
  - `PostgreSqlCollectorRuntimeRepositoryPort.cs`: 1,543 lines;
  - `PostgreSqlTargetControlPlane`, which exposes about 25 ports.
- **Layering.** Move analytics authorization out of the HTTP layer into application services, and remove the Npgsql catches from the Server.
- **Tiny projects.** Merge Security, Audit, Alerting and Observability. Rename `McpQueryServices.cs`, which holds general-purpose services.
- **Dead and scaffold code:**
  - the `ScaffoldEndpoints` status;
  - 8 unused web admin client functions in `analyticsApi.ts`;
  - `IRepositoryReadinessMonitor`, which is registered but never used;
  - `AddProblemDetails`, which is registered but never used.
- **Docs that don't match the code:**
  - the README and architecture overview claim SignalR, but none exists;
  - ADR-0008 and the README describe binary COPY ingestion, which production doesn't use;
  - the README claims a PUBLIC lockdown that doesn't hold.

### Migrations

About 50 of the 77 migrations are fixes (performance, grants, binding or logic). The PL/pgSQL "output parameter shadows column" bug keeps recurring (0024, 0032, 0074, 0076). Migration 0035 edits a function by string-replacing its `pg_get_functiondef` text.

After the fix batch lands:
- publish a squashed baseline for fresh installs;
- raise the 256-migration cap (`Migrations.cs:180`, `PostgreSqlMigrationCatalog.cs:156-160`);
- add a non-transactional migration type so index builds can use `CONCURRENTLY` and constraints can use `NOT VALID`;
- lint migration preambles (`SET ROLE`, `lock_timeout`) in CI.

### Feature parity with SQL DM, by priority

1. **Query text and plans** under ADR-0011, gated by role and audited. Today the protector is permanently set to "unavailable" (`CollectorServiceRegistration.cs:105`). Also add per-query waits (`sys.query_store_wait_stats`) and plan-regression detection.
2. **Resource data from DMVs.** CPU: `Resource Pool Stats` and `dm_os_schedulers`. Memory: `dm_os_sys_memory`, clerks, grants, and PLE per `Buffer Node`. Log: `dm_db_log_stats`/`log_info`. Disk: `dm_os_volume_stats`. These can replace the PowerShell host metrics, which read the local machine only (`WindowsIntegratedHostDataReader.cs`).
3. **HA and backups.**
   - AG: read all replicas on the primary, including queues, rates and `last_commit_time`.
   - Backups: freshness per database against RPO, aware of the preferred replica.
   - Log shipping and mirroring.
4. **Alert coverage.**
   - Rule kinds across all collectors: blocking duration, deadlocks, backup age, job failures, AG queue, disk, log usage, baseline deviation.
   - Templates per group.
   - SMTP, Teams, PagerDuty and SNMP; signed webhooks; recurring maintenance windows.
5. **Drift and events.**
   - Config and database-option drift: `sys.configurations`, `log_reuse_wait_desc`, compatibility level and similar.
   - `error_reported` and security events from `system_health`, using the same bookmark reader.
6. **Database health on a slow schedule (opt-in):** unused and missing indexes, stale statistics, `LIMITED` fragmentation. This needs a user in each database, which loosens least privilege.
7. **Platform breadth:** SQL and Entra authentication through a secret store, certificate pinning, Azure SQL, Linux. This needs an ADR revision; see `BACKLOG.md:198`.

---

## Interface direction

The current UI is organized by milestone, with one tab per data type. Organize it around the DBA's workflow instead:

1. Is anything wrong?
2. Where?
3. Why?
4. What happened at a given time?

### Principles

1. **One global time context.** A top bar with a range picker, **Live** and **Rewind**. Every panel follows it; dragging across the timeline selects a window for the whole page. This builds SQL DM's History Browser into every screen. Display local time, with a UTC toggle.
2. **Show the answer, then the evidence.** An "Investigate first" list correlates findings, for example the head blocker, the regressed query and the config change 8 minutes earlier. Raw tables are one click deeper.
3. **Event markers on the timeline:** deadlocks, alerts, job failures, config and plan changes, failovers, restarts.
4. **Quieter evidence status.** Keep the freshness and partial-coverage information, but as a dot per panel and one footer line. "Unavailable" should always say what fixes it.
5. **Role-aware UI.** Hide actions the user can't take, and name the role that unlocks hidden content. This needs `/me`.

### Screens

| Level | Screen | What it does |
|---|---|---|
| Fleet | Server grid | Grouped by environment or tag, with health dots for CPU, memory, waits, blocking, backups and HA, sorted worst first. A compact version stays pinned as a strip. |
| Fleet | Alert inbox | Fleet-wide alerts: acknowledge, snooze, assign, group, and a link to the moment on the timeline. |
| Server | Dashboard | Key numbers compared with the baseline, a timeline of CPU, waits and blocked sessions with a shared crosshair and event markers, the investigate-first list, and a live blocking tree. |
| Server | Queries | Top N by CPU, duration, reads or waits, with a trend line per row and a regressed tab. Drill into history, plans and per-query waits. |
| Server | Waits | Grouped by category (lock, I/O, CPU/signal, memory, parallelism, log) over time, with benign waits removed. Drill into the queries and sessions responsible. |
| Server | Sessions and blocking | Live session list, blocking tree (head blocker, depth, duration), blocking history and deadlock graph. |
| Server | Resources | SQL vs host CPU, memory (PLE per NUMA node, grants pending), read and write latency per file, disk space and days until full. |
| Server | Health checks | Backup status against RPO, Agent jobs (failed and long-running), AG, TempDB, config drift, database options. |
| Admin | Rules, maintenance, destinations, servers | Rule templates by group, a maintenance calendar with recurrence, and a permission script generator per server. |
| Global | Ctrl+K | Jump to a server, database, query hash or alert. |

### Behavior

- **Live updates.** Push updates via SignalR, as ADR-0018 proposes, or one shared polling loop instead of remounting on refresh (`App.tsx:222`). Include a pause button.
- **Shareable URLs.** A URL restores the server, window and selection. Extend the existing hash routes and `at=` parameter.
- **Charts.** Replace `ObservationChart.tsx` (single series, `HH:MM:SS` axis, assumes a zero baseline). The replacement needs a date axis, multiple series, a baseline band, a threshold line, a shared crosshair and drag-to-zoom.
- **Incidents.** "Open incident" saves the window, the evidence and notes. That feeds reports and gives the incident tools a thread ID.
- **Assistant.** An optional "Ask about this window" side panel that uses the same read-only tools as the MCP server. Depends on the MCP fixes above.
- **Frontend infrastructure.** One shared fetch client with caching; there are currently about 12 separate wrappers. Component tests, Playwright and axe, in place of the regex-over-source tests (17 of the 32 web test files).

### UI build order

1. The frame: time bar and URLs, `/me`, the fetch client, the chart component.
2. The server dashboard and fleet grid, on data that already exists.
3. The alert inbox and admin screens. The APIs mostly exist already.
4. Queries, Waits and Resources. These depend on the collector work in the parity list.
5. Timeline markers for config and plan changes, then the assistant panel.

Some mockup panels depend on data the collectors don't gather yet: SQL-vs-host CPU, config history, plan regressions, and backups against RPO. Plan the UI and collector work together.

---

## Starting prompt for the next session

> Read `docs/technical/SqlObserver-Review-and-Plan-2026-09-23.md`. Confirm the toolchain in Step 0 is installed and run `pwsh ./tools/validate.ps1 -Profile Local` to get a baseline. Then start the Step 1 fix batch with the cross-target authorization fix, writing the failing mixed-grant tests first.
