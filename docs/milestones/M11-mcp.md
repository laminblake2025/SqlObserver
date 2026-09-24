# M11 read-only MCP

Milestone 11 implements the MCP boundary described by ADR-0006, ADR-0007, and
ADR-0011. The protocol adapter uses the official stable C# SDK 2.2.0. SDK types
are confined to `SqlObserver.Mcp`; `SqlObserver.Server` exposes the authenticated
stateless Streamable HTTP endpoint, and `SqlObserver.McpStdio` is a thin local
entry point into the authenticated HTTPS proxy. The bridge has no PostgreSQL,
SQL Server, collector, target-credential, or administrative-service dependency.

## Fixed authority

The runtime catalog contains exactly these 27 read-only tools:

`list_instances`, `get_instance_capabilities`, `get_instance_health`,
`get_active_alerts`, `get_metric_series`, `compare_metric_windows`,
`get_wait_summary`, `get_active_sessions`, `get_active_requests`,
`get_blocking_chain`, `get_blocking_history`, `get_deadlock`,
`search_deadlocks`, `get_top_queries`, `get_query_history`,
`get_query_plan_metadata`, `get_database_health`, `get_tempdb_health`,
`get_file_io`, `get_storage_forecast`, `get_backup_status`,
`get_job_failures`, `get_availability_health`, `get_incident_evidence`, and
`search_diagnostic_events`, `list_metric_catalog`, and `list_incidents`.

Registration is explicit and one-to-one with the catalog. Every schema is
closed, every tool is annotated read-only, idempotent, non-destructive, and
closed-world, and the catalog has a deterministic digest checked by the stdio
bridge before it begins serving. There is no arbitrary query tool, resource,
prompt, sampling, root, elicitation, task, app, or administrative capability.

## Authentication and authorization

`/mcp` is mapped after ASP.NET Core authentication and requires an authenticated
principal. The Server resolves the principal through the existing Windows group
role resolver; caller-supplied identities, roles, groups, target scopes, bearer
tokens, and trusted identity headers are not inputs. Every tool then calls an
authorized Application query service, which rejects inactive identities and
missing roles. Instance reads reject disabled targets and cross-target access
before repository I/O. The static `list_metric_catalog` call requires Viewer,
Operator, or TargetAdministrator, accepts no arguments, reads no repository or
instance data, and records a null target in its audit. Target-scoped read grants
can discover these same embedded definitions without gaining additional target
access.

The stdio bridge accepts only a configured HTTPS `/mcp` URI without userinfo,
query, fragment, redirect, or certificate-validation bypass. It uses Windows
default credentials, compares the authenticated remote catalog and digest, and
fails closed on authentication, protocol, or inventory mismatch. Standard
output is reserved for protocol frames; diagnostics go to standard error.

## Bounds and projections

MCP input is closed-schema JSON with a 64 KiB request bound and depth 32.
Application calls use five-second repository deadlines inside a ten-second tool
deadline and the Server's fifteen-second request timeout. Admission is limited
to four concurrent calls per actor and 32 globally with no queue. Tool-specific
row, UTC-window, cursor, and identifier bounds are enforced without clamping;
serialized results are withheld above 1 MiB.

Results are minimum typed projections. Query text, plans, raw deadlock XML,
provider errors, job commands/messages, physical paths, credentials, and
arbitrary analytics JSON are excluded. Metric and diagnostic pagination freeze
repository-clock snapshots and use complete tie keys, including equal-timestamp
cases. The MCP bridge never opens the repository or a monitored target.

`list_metric_catalog` returns the catalog version/checksum and enabled metric
keys, display names, units, source, aggregation, and allowed dimension keys.
It returns the complete bounded definition list without pagination. Definitions
describe supported inputs; they do not report available samples or collection
coverage on any instance.

`list_incidents` discovers thread IDs within a UTC opening-time window of up to
31 days (default: last 24 hours), with at most 100 items per page. It returns
opening times and generation counts/latest observation times at its snapshot,
without summaries or evidence payloads. Pass a thread ID to
`get_incident_evidence`, which takes its own repository snapshot. Continuations
freeze the window, snapshot, target revision, and full ordering key; an incident
publication revision rejects changed result sets with `cursor_stale`. Restart
without a cursor after this response. Incidents opened before the selected
window are excluded regardless of whether they remain active.

Successful calls expose a stable structured-output envelope, `{ data: ... }`,
whose closed schema is advertised on every tool; the text content carries the
same sanitized projection. Continuation values in `data.nextCursor` are
bounded base64url tokens and are decoded only at the MCP boundary, never
serialized typed cursor objects.

## Terminal audit

Every recognized invocation reaches one terminal audit boundary before its
result is disclosed. Success, denial, invalid input, unknown direct dispatch,
timeout, caller cancellation, concurrency rejection, oversized response, and
repository failure have closed outcome/reason vocabularies. Audit append uses an
independent short timeout and failure withholds the result.

The repository records its own UTC timestamp and only safe metadata: MCP client
identifier, canonical tool/action, authorization/outcome/reason, target or
incident identifiers, correlation identifier, canonical-parameter SHA-256,
duration, response bytes, and a safe detail code. It never stores raw arguments,
results, tokens, connection data, diagnostic content, or exception text. The
Server can execute only the append function, the Auditor can read only the audit
table, the Collector has neither permission, and divergent invocation replays
fail.

## Compatibility and remaining certification

The adapter targets the stable `2026-07-28` protocol and retains an active
down-level `2025-11-25` compatibility contract. Local builds and contract,
unit, security, performance, and composition tests are M11 evidence. Live
Kerberos/SPN delegation, trusted production TLS, supported Windows Server
qualification, Docker-backed PostgreSQL execution where unavailable locally,
installer lifecycle, sustained load, and release certification remain M12
gates. Passing M11 does not create a production support claim.
