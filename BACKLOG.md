# SqlObserver implementation backlog

This is the requirement-to-milestone map for the original clean-room product plan. It is not a claim that future behavior exists. Detailed work may be split inside a milestone, but must preserve the ordering and architectural decisions recorded here.

Status meanings:

- **Documented**: boundary or decision is captured, but runtime proof does not yet exist.
- **Scaffolded**: non-runtime structure builds or validates; no operational support is implied.
- **Implemented**: the milestone slice and its active acceptance tests exist; release support is not implied.
- **Planned**: implementation has not started in this assignment.
- **Deferred**: intentionally outside M0-M12 initial-release scope.

## Milestone map

| Milestone | Scope and required outcome | Current status |
| --- | --- | --- |
| **M0 — Product boundary and architecture** | Clean-room boundary; product terminology; modular-monolith process/module boundaries; PostgreSQL-vs-target distinction; initial/planned support matrix; security invariants; threat model; ADR-0001 through ADR-0012; milestone map | Documented and validated for the first assignment |
| **M1 — Repository bootstrap and CI** | Complete folder map; .NET 10 solution/project skeletons; nullable/analyzers/warnings-as-errors; central NuGet management; strict React/TypeScript build skeleton; test-project skeletons; validation entry point; local-development script placeholders; GitHub Actions; contribution/security policy | Non-runtime first-assignment scope scaffolded and validated; runtime work remains planned |
| **M2 — PostgreSQL repository, migrations, partitions, ingestion** | PostgreSQL 18.x repository; nine schemas; immutable numbered SQL migrations/checksums; repository roles; UTC storage; daily raw/monthly event partitions; BRIN and justified instance/time B-tree indexes; binary `COPY`; query-text/plan deduplication; partition/retention foundations; PostgreSQL integration/performance tests | Implemented and integration-tested on pinned PostgreSQL 18.4; representative-volume release certification remains M12 |
| **M3 — Onboarding, credentials, capability discovery, permissions** | Observation-target lifecycle; protected credentials; gMSA/integrated-auth path; version-aware least-privilege permission generator; no permanent `sysadmin`; capability/connection collector; supported/degraded states; expected-denial tests | Implemented and integration-tested; platform certification remains M12 |
| **M4 — Collector framework and core health** | Contract registry/manifests; leases and fencing; bounded scheduler; cancellation; non-overlap; retry/circuit breaker; telemetry and visible sample loss; core engine counters; databases/files collectors; ingestion vertical slice; health projections | Implemented and integration-tested; platform and sustained-load certification remain M12 |
| **M5 — Sessions, requests, waits, blocking** | Bounded sessions and requests, wait summaries, current blocking chains and blocking history; API/UI projections and collector-specific permission/failure/limit tests | Implemented |
| **M6 — Deadlocks and Extended Events** | Passive bounded reading of deadlocks from `system_health`; safe XML handling; typed event persistence/search; no enhanced script because system_health is the selected source; never automatic XE/blocked-process changes | Implemented locally; Docker/PostgreSQL and SQL Server lab certification remain required |
| **M7 — Query Store and query performance** | Capability-aware Query Store reads; plan-cache fallback; query history/top queries/plan metadata; sensitive-content controls and deduplication; no automatic Query Store change or plan forcing | Implemented locally |
| **M8 — Alerts, maintenance, notifications** | Rule evaluation/state, maintenance suppression, delivery adapters, audited administrative writes, active-alert projection; MCP remains unable to acknowledge or notify | Implemented locally; Docker/live Event Log gates remain environmental |
| **M9 — Backups, jobs, TempDB, Availability Groups** | Ordered collectors and bounded projections for backup status, SQL Agent failures, TempDB health, and Availability Group health | Implemented locally; Docker/live SQL certification pending |
| **M10 — Rollups, baselines, forecasts, incident correlation** | Rollups, baselines, metric-window comparison, storage forecasts, host metrics, replication evidence, evidence packets, incident threads, retention/partition production hardening | Implemented locally; Docker/live target and release certification remain pending |
| **M11 — MCP** | Official stable C# SDK selection at implementation time behind adapter; stdio-to-server authentication; all and only allowlisted read-only tools; service-layer RBAC; UTC/bounds/pagination; audit every call; stable/current-protocol compatibility suite | Implemented locally; live Kerberos/TLS and release certification remain M12 gates |
| **M12 — Reports, installer, upgrades, release hardening** | Reports/exports; WiX and PostgreSQL packaging; Windows Service/gMSA/TLS configuration; install/upgrade/recovery/uninstall; initial-release certification; security/performance/end-to-end gates; release and runbooks | Certification matrix, strict evidence verifier, explicit Local/Release validation profiles, side-effect-free lifecycle/migration and ADR-0017 deployment-security assessments, decision-neutral Vite web-asset identity catalog, and a pending-only live MCP certification producer implemented locally; reports, packaging, serving/cache/browser/accessibility/SignalR decisions, deployment decisions, runtime lifecycle, identity/TLS/secret evidence, and external release certification remain incomplete |

## High-level requirement traceability

| Requirement area | Owning milestone(s) | Completion evidence required |
| --- | --- | --- |
| Windows-hosted SQL Server diagnostics; PostgreSQL is repository only | M0, M1, M2, M12 | Architecture boundary; host/repository projects; deployment and target-isolation tests |
| Historical telemetry | M2, M4, M10 | Partitioned ingestion, retention, range-query and rollup tests |
| Query diagnostics | M7 | Query Store/fallback compatibility, sensitive-content and bounded-query tests |
| Waits and blocking | M5 | Collector, history/projection, permission, timeout and payload tests |
| Deadlocks | M6 | Bounded `system_health` reader, safe XML and search tests |
| Alerts | M8 | Rule/state/delivery behavior, audit and failure tests |
| Baselines, forecasts, incident correlation | M10 | Deterministic analytics, backfill, confidence/visibility-gap and performance tests |
| Reports | M12 | Authorized bounded generation/export, unsafe-content and browser tests (local implementation complete; external/release evidence pending) |
| Agentless default and passive non-mutation | M0, M3-M10, M12 | Contract/query review plus target state before/after integration evidence |
| Optional enhanced monitoring is separate and DBA-run | M0, M6, M12 | Versioned script package, review/removal docs and proof no service execution path exists |
| Modular monolith with separately deployable Server, Collector, MCP stdio | M0, M1, M4, M11, M12 | Dependency tests, compiled hosts, process-level deployment/upgrade tests |
| Server owns API/web/SignalR/auth/RBAC/reports/MCP HTTP | M1, M4-M12 | Composition/contract tests and deployment evidence at owning feature milestones |
| Collector owns scheduling/ingestion/alerts/rollups/baselines/partitions/retention | M2, M4, M8, M10 | Lease, integration, failure, performance and operational tests |
| MCP stdio authenticates to Server and has no DB/target path | M1, M11 | Dependency/credential inventory and end-to-end authorization tests |
| Repository schemas: `control`, `security`, `telemetry`, `events`, `analytics`, `alerting`, `reporting`, `audit`, `system` | M2 | Migration assertions and role/access integration tests |
| Daily raw and monthly event native range partitions | M2 | UTC boundary, late-arrival, maintenance and retention tests |
| BRIN time and justified B-tree instance/time indexes | M2, M10 | Representative query plans and ingest/read performance gates |
| Binary `COPY` high-volume ingestion | M2, M4 | Bounded batch correctness/failure/performance tests |
| Immutable numbered SQL migrations with checksums; no ORM generation | M1 policy, M2 runtime | Drift/gap/tamper/upgrade tests and repository policy validation |
| Deduplicated sensitive query text and plans | M2, M7 | Collision/reference/retention/RBAC tests |
| UTC persisted timestamps | M0, M2-M12 | Schema/contract checks plus locale, DST and clock-skew tests |
| PostgreSQL worker leases before any broker | M2, M4 | Acquisition, fencing, expiry, stale-writer and fault tests |
| Collector contract metadata and bounded behavior | M4, then every collector milestone | Manifest-schema, permission, version, interval, cost, fallback, cancellation, non-overlap and output-version tests |
| Structured logs and OpenTelemetry; no hidden sample loss | M1, M4-M12 | Redaction tests and required metrics/events under fault/saturation |
| Parameterized SQL; no undocumented internals where supported APIs exist | M1 policy, M2-M12 | Static checks, query review, injection and compatibility tests |
| Strict API/MCP time, row, byte and execution limits | M4 API foundation, M11 MCP, M12 hardening | Boundary, cancellation, concurrency and saturation tests |
| Audit every MCP call and administrative write | M2 audit store, M8 admin flows, M11 MCP | Success/denial/failure audit contract and integrity tests |
| Windows Integrated Authentication, RBAC, gMSA preference | M3, M12 | Identity, SPN/TLS, authorization and installation tests |
| Initial support: Windows Server 2022/2025; PostgreSQL 18.x; SQL Server 2019/2022/2025 on Windows; Edge/Chrome; Windows Integrated Authentication | M2-M11 implementation, M12 certification | Full [support-matrix](docs/architecture/support-matrix.md) qualification; no claim before it passes |
| Planned SQL Server 2016/2017, SQL Server 2014 best effort, SQL Server on Linux, Azure SQL Database/Managed Instance, Amazon RDS, Hyper-V/VMware adapters | After M12 | Deferred capability/permission/platform designs and dedicated certification; excluded from initial release |

## Collector implementation order

Order is mandatory because later capability and interpretation depend on earlier evidence. A milestone may contain adjacent entries, but may not invert the sequence.

| Order | Collector area | Milestone | Required gate |
| ---: | --- | --- | --- |
| 1 | Capability and connection | M3 | Version/platform/edition/features/permissions profile; bounded connection failure and denial evidence |
| 2 | Core engine counters | M4 | Versioned manifest/output, least privilege, bounded interval/timeout/rows/cost |
| 3 | Databases and files | M4 | Database/file identity and state projection with supported-version tests |
| 4 | Sessions and requests | M5 | Sensitive field controls, cancellation, truncation and expected-denial tests |
| 5 | Waits | M5 | Counter/reset semantics and UTC window interpretation tests |
| 6 | Blocking | M5 | Current-chain and historical evidence with bounded traversal/output |
| 8 | Deadlocks from `system_health` | M6 | Passive read, deduplication, safe XML parsing and event bounds |
| 9 | Query Store and plan-cache fallback | M7 | Explicit capability/fallback state; no Query Store mutation; sensitive text/plan controls |
| 10 | Backups | M9 | Version/edition-aware backup freshness and permission tests |
| 11 | SQL Agent | M9 | Job-failure projection with job steps treated as sensitive/untrusted |
| 12 | TempDB | M9 | Bounded health evidence and version-aware counters |
| 13 | Availability Groups | M9 | Topology/replica health with unsupported/degraded states |
| 14 | Host metrics | M10 | Separate host capability/identity boundary and correlation tests |
| 15 | Replication | M10 | Version/topology capability contract, bounded evidence and fallback behavior |

M2 contains no production collector or monitored-target SQL statement. The first such work is the capability/connection collector in M3.

## MCP tool mapping

All tools belong to M11, are read-only, operate through authenticated/authorized `SqlObserver.Server` application services, and require per-call audit plus time/row/byte/execution/concurrency limits.

| Allowlisted tool | Primary prerequisite milestone(s) | Implementation milestone |
| --- | --- | --- |
| `list_instances` | M3 target inventory | M11 |
| `get_instance_capabilities` | M3 capability profile | M11 |
| `get_instance_health` | M4 core health | M11 |
| `get_active_alerts` | M8 alerts | M11 |
| `get_metric_series` | M2 telemetry, M4 ingestion | M11 |
| `compare_metric_windows` | M10 analytics | M11 |
| `get_wait_summary` | M5 waits | M11 |
| `get_active_sessions` | M5 sessions | M11 |
| `get_active_requests` | M5 requests | M11 |
| `get_blocking_chain` | M5 blocking | M11 |
| `get_blocking_history` | M5 blocking history | M11 |
| `get_deadlock` | M6 deadlock events | M11 |
| `search_deadlocks` | M6 deadlock search | M11 |
| `get_top_queries` | M7 query analytics | M11 |
| `get_query_history` | M7 query history | M11 |
| `get_query_plan_metadata` | M7 plan metadata | M11 |
| `get_database_health` | M4 databases | M11 |
| `get_tempdb_health` | M9 TempDB | M11 |
| `get_file_io` | M4 files | M11 |
| `get_storage_forecast` | M10 forecasts | M11 |
| `get_backup_status` | M9 backups | M11 |
| `get_job_failures` | M9 SQL Agent | M11 |
| `get_availability_health` | M9 Availability Groups | M11 |
| `get_incident_evidence` | M10 incident correlation | M11 |
| `search_diagnostic_events` | M6 events, extended through M7-M10 | M11 |

`execute_sql` is forbidden in every milestone. MCP also never kills sessions, changes configuration, acknowledges alerts, sends notifications, creates indexes, forces plans, retrieves secrets, accesses PostgreSQL/targets directly, or performs any administrative write. A new tool is not implicitly allowed because it appears read-only; the allowlist and security decision must be deliberately revised.

## Repository structure and validation mapping

| Area | Scaffold milestone | Runtime/acceptance milestone |
| --- | --- | --- |
| Domain, Application, PostgreSQL/SQL Server/Windows infrastructure projects | M1 | M2-M4 behavior; M12 release gate |
| Collector abstractions and collectors project | M1 | M4-M10 in mandated order |
| Alerting, analytics, security, audit libraries | M1 | M2, M8, M10-M11 |
| Server, Collector, MCP, MCP stdio, CLI projects | M1 | M3-M12 by host responsibility |
| `web/` strict React/TypeScript | M1 | Feature slices M3-M12; browser certification M12 |
| `database/migrations`, `functions`, `views`, `seeds`, `testdata` | M1 directories only | M2 objects and integration tests |
| `collectors/sql`, `collectors/manifests` | M1 directories only | M3-M10 reviewed collector resources |
| `installer/wix`, `installer/postgres` | M1 directories only | M12 packaging and lifecycle behavior |
| Unit test project | M1 | Every implementation milestone |
| PostgreSQL integration tests | M1 skeleton | M2 onward |
| SQL Server integration tests | M1 skeleton | M3 onward across supported versions |
| API contract tests | M1 skeleton | M3 onward |
| MCP contract tests | M1 skeleton | M11 |
| Security tests | M1 skeleton | Every implementation milestone, M12 gate |
| Performance tests | M1 skeleton | M2/M4 onward, M12 gate |
| End-to-end tests | M1 skeleton | M3 onward, M12 gate |
| `tools/validate.ps1` | M1 | Expanded with every milestone; one-command release gate M12 |
| `tools/dev-up.ps1` | M1 placeholder | Local PostgreSQL/application lab M2-M4 |
| `tools/seed-lab.ps1` | M1 placeholder | Synthetic authorized lab data M2-M4 |
| `tools/generate-permissions.ps1` | M1 placeholder | Version-aware reviewed grants M3 |
| GitHub Actions | M1 | Expanded platform/integration/release matrices through M12 |

## Completed non-runtime M1 tasks in dependency order

This is the completion checklist for the first assignment. Each item was inspected in the authoritative worktree and exercised by the repository validation command where applicable.

1. **Repository policy and toolchain pins — complete.** Ignore/editor settings, .NET SDK pin, central build/package properties, nullable/analyzers/warnings-as-errors, and reproducible frontend metadata are present.
2. **Solution and project graph — complete.** Every required source and test skeleton is in `SqlObserver.slnx`; dependencies point inward and executable hosts make only scaffold claims.
3. **Frontend skeleton — complete.** The repository contains an original minimal React UI, strict TypeScript configuration, deterministic pnpm lock, and typecheck/test/build commands.
4. **Non-runtime directory contracts — complete.** Database, collector-resource, installer, and test-data paths contain explanatory placeholders and no production artifacts.
5. **Validation entry point — complete.** `tools/validate.ps1` fails on restore/build/test/frontend/policy errors, uses explicit Local/Release lanes and fail-closed external filters, and resolves the repository from its own path.
6. **Development helper placeholders — complete.** `dev-up.ps1`, `seed-lab.ps1`, and `generate-permissions.ps1` are safe notices that do not provision, connect, grant, or mutate.
7. **Initial CI workflow — complete.** The least-privilege Windows workflow installs the pinned toolchains and invokes the single validation entry point.
8. **Clean first-assignment validation — complete.** `pwsh ./tools/validate.ps1` completed locked restore, Release build with zero warnings/errors, all test-project runs with no hidden runtime skips, frozen frontend install, strict typecheck, frontend test, and production build.

## Completed M2 repository tasks

1. **Immutable repository deployment — complete.** Six LF-only, gap-free migrations are embedded with a bijective SHA-256 manifest and applied one transaction at a time under a bounded PostgreSQL advisory lock. Exact-prefix drift, gaps, unknown history, and top-level transaction control fail closed.
2. **PostgreSQL 18 repository boundary — complete.** The compatibility probe and bootstrap both require major version 18; nine schemas and four restrictive NOLOGIN group roles are created without embedded credentials.
3. **UTC partition foundation — complete.** Native daily raw-metric and monthly diagnostic-event parents, fixed-purpose concurrency-safe creation functions, partition registry, BRIN time indexes, and instance/time B-tree indexes are implemented.
4. **Bounded ingestion and protected content — complete.** Caller-validated batches use binary `COPY` into transaction-local staging and idempotent parent insertion under a fenced lease. Sensitive payload storage accepts ciphertext, nonce, tag, external key identifier, and opaque fingerprint only.
5. **Coordination and retention safety — complete.** Repository-clock leases use persistent monotonic fencing; retention policy is disabled and its view is preview-only with recovery prerequisites unsatisfied.
6. **Active evidence — complete for M2.** Unit, security, and PostgreSQL 18.4 integration suites cover contracts, boundaries, migration history, roles/schemas, partitions/indexes, ingestion/deduplication, cancellation, and stale fences without PostgreSQL-test skips. Full representative-volume and platform certification remains a release gate.

M7 Query Store and plan-cache evidence, M8 alert evaluation/delivery, M9 operational health, M10 analytics/host/replication, and the M11 read-only MCP slice are implemented locally. M12 reports, packaging, deployment, and release certification are the remaining initial-release milestone; Docker/live target and platform certification remain explicit environmental gates.

## Completed M4 collector and core-health tasks

1. **Immutable collector registry — complete.** Three versioned v2 manifests, nine supported-version SQL assets, exact SHA-256 pins, closed output contracts, and deterministic dependency order are enforced at startup and repository reconciliation.
2. **Bounded execution and scheduling — complete.** Repository-clock due work, renewable fenced leases, local non-overlap, cancellation, original-deadline retry, durable circuit state, and bounded concurrency have active unit and performance evidence.
3. **Core target evidence — complete.** Core cumulative counters, database inventory, and logical-file capacity/I/O collectors are parameterized, row/byte bounded, integrated-authenticated, TLS-validated, and passive in the available SQL Server development lab.
4. **Atomic repository data plane — complete.** Collection run/outcome history, exact replay, schedule/revision conflicts, output snapshots, circuit transitions, and explicit visibility gaps commit through bounded PostgreSQL functions with direct base-table access denied.
5. **Scoped health surface — complete.** Application services, API DTOs, and React views expose bounded freshness, accounting, loss, circuit, metric, database, and file evidence without secrets, provider messages, raw SQL, or physical paths.
6. **Active evidence — complete for M4.** Unit, PostgreSQL, SQL Server, API, security, performance, web, and end-to-end suites remain active; support-platform, gMSA/Kerberos, trusted-TLS, and long-duration certification remain M12 gates.

## Completed M5 activity tasks

1. **Active collector bundle — complete.** Four checksum-pinned passive collectors run at mandatory orders 4–7 with SQL Server 2019/2022/2025 Windows contracts, bounded rows/bytes/time, cancellation, permission-denial mapping, and explicit loss accounting.
2. **Repository and API — complete.** Migration 0009 persists activity snapshots and reporting functions; target-scoped application services and Windows/RBAC API routes expose stable bounded cursors and safe DTOs.
3. **Activity UI — complete.** The target workflow exposes sessions, active requests, waits with reset/baseline semantics, current blocking, and bounded one-hour blocking history with freshness and truncation evidence.
4. **Active evidence — implementation slice verified where runnable.** M5-focused SQL Server, end-to-end composition, API DTO, and web contract tests are active; PostgreSQL integration execution is pending the Docker PostgreSQL environment. Support-platform, gMSA/Kerberos, trusted-TLS, sustained-load, installer, and release certification remain M12 exclusions.

## Current post-M11 exclusions

The following are explicitly incomplete and must not be represented as working:

- reports and exports are implemented locally through the ADR-0015 vertical slice; PostgreSQL runtime, browser, installer, and external/release certification evidence remains pending;

- production collector implementations or target SQL beyond the active M10 bundle (the bundle is `capability.connection`, `engine.core`, `database.inventory`, `database.files`, `activity.sessions`, `activity.requests`, `waits.server`, `blocking.current`, `deadlocks.system-health`, `queries.performance`, `backups.status`, `sql-agent.failures`, `tempdb.health`, `availability-groups.health`, `host.metrics`, and `replication.health`);
- reusable target credentials, automatic permission grants, or target mutation;
- automatic retention execution, repository installation/backup/restore/HA, or production repository provisioning;
- API behavior beyond target management, M4 health, M5 activity, M6 deadlock, M7 query-performance, M8 alerts, M9 operational-health, M10 analytics, and the fixed M11 MCP surface; SignalR remains excluded (reports/exports are implemented locally, with external/release certification pending);
- Query Store configuration/change, Extended Events, blocked-process, index, plan-forcing, session, or configuration changes;
- live environment bootstrap, seed data, installer, upgrade, uninstall, or release packaging;
- production support/certification for any platform in the support matrix.

## Completed M10 analytics, host, and replication tasks

1. **Bounded analytics API — locally runtime-verified.** Target-scoped series, rollups, comparisons, baselines, forecasts, incidents, host/replication surfaces, jobs, and retention routes are mapped through the Server API. In-process contract tests cover authorization and target scope, malformed windows/limits/cursors, cursor terminal pages, cancellation propagation, and 1 MiB response rejection.
2. **Collector ownership — locally runtime-verified.** The Collector composition owns the analytics derivation and backfill workers and wires both to the restricted PostgreSQL analytics ports. Contract-testing composition deliberately excludes those hosted workers; no target connection is introduced by the analytics workers.
3. **Analytics evidence — implemented locally.** Deterministic rollup, baseline, forecast, evidence, incident, host, replication, and retention contracts have focused unit/security/performance coverage. PostgreSQL migration/integration, Docker, live SQL Server, sustained-load, and release/platform certification remain environmental or M12 gates.

## Completed M11 MCP tasks

1. **Fixed protocol surface — locally runtime-verified.** The official stable C# SDK 2.2.0 is isolated behind the MCP adapter and exposes exactly the 25 ADR/BACKLOG tools with closed schemas, read-only annotations, an authenticated stateless HTTP endpoint, and a catalog-verified stdio proxy.
2. **Normal authorization and bounded reads — locally runtime-verified.** Every handler resolves the Server principal and calls target-scoped Application query services with UTC windows, opaque bounded cursors, fixed repository/tool deadlines, four-per-actor and 32-global no-queue admission, and a 1 MiB response ceiling. The bridge has no repository, target, collector, or administrative path.
3. **Terminal audit — locally runtime-verified.** Success and bounded failure outcomes append one safe repository-time audit record before disclosure. Server, Auditor, and Collector permissions are separated; raw parameters, results, diagnostic content, tokens, connection data, and exception text are not persisted.
4. **Compatibility evidence — active where runnable.** Exact inventory, stable/current and down-level protocol, authorization, denial-before-I/O, limit, cancellation, audit, process-boundary, and equal-timestamp pagination tests are active. Docker PostgreSQL execution and live Kerberos/SPN/trusted-TLS certification remain environmental or M12 gates.

## Cross-cutting release gates

Each implementation milestone must inspect preceding ADRs, state scope/assumptions/risks, deliver the smallest complete vertical slice with tests, run `pwsh ./tools/validate.ps1`, update documentation, and identify the next dependency. M12 cannot claim release readiness until the completion evidence above covers permissions, failure, timeout, cancellation, payload limits, security, performance, installation, upgrade/recovery, and supported-platform behavior without hidden skips.
## M9 operational health

Implemented locally; certification pending. Backups, SQL Agent failure history, TempDB, and Availability Group health are bounded and read-only. Live SQL Server edition/AG and PostgreSQL integration certification remain environment-dependent.
