# ADR-0006: Read-only MCP

- Status: Accepted
- Date: 2026-08-23

## Context

MCP clients need structured access to diagnostic evidence, but protocol input and captured database content are untrusted. A general query surface or operational action would bypass product authorization, resource bounds, audit, and DBA control. Giving a local bridge direct database credentials would create a second security architecture.

## Decision

Expose only these allowlisted, read-only diagnostic tools:

- `list_instances`
- `list_metric_catalog`
- `list_incidents`
- `get_instance_capabilities`
- `get_instance_health`
- `get_active_alerts`
- `get_metric_series`
- `compare_metric_windows`
- `get_wait_summary`
- `get_active_sessions`
- `get_active_requests`
- `get_blocking_chain`
- `get_blocking_history`
- `get_deadlock`
- `search_deadlocks`
- `get_top_queries`
- `get_query_history`
- `get_query_plan_metadata`
- `get_database_health`
- `get_tempdb_health`
- `get_file_io`
- `get_storage_forecast`
- `get_backup_status`
- `get_job_failures`
- `get_availability_health`
- `get_incident_evidence`
- `search_diagnostic_events`

There is no `execute_sql` tool. MCP cannot kill sessions, change configuration, acknowledge alerts, send notifications, create indexes, force plans, retrieve secrets, or perform any other administrative write.

Protocol handling stays behind an adapter. At Milestone 11, use the latest stable official C# MCP SDK verified at implementation time. If the newest 2.x line remains preview, ship the current stable 1.x SDK and run compatibility tests against 2.x/current protocol behavior.

The stdio bridge authenticates to `SqlObserver.Server`; it has no target or repository credential. Tools invoke approved application services that apply the same server-side RBAC, target scope, field filtering, UTC semantics, pagination, and time/row/byte/execution/concurrency limits as other clients. Every invocation, including denial and bounded failure, is audited without copying sensitive result content into the audit record.

`list_metric_catalog` returns embedded metric definitions without reading instance data or the repository. It accepts no arguments and requires an active Viewer, Operator, or TargetAdministrator role, including grants scoped to particular targets. Its audit has no target identifier. Catalog membership does not establish collection availability on an instance; subsequent instance queries retain their exact target-scope checks.

## Consequences

`list_incidents` is an instance-scoped metadata projection for Viewer, Operator,
or TargetAdministrator grants on that instance. It returns thread identities,
opening times, and generation counts/times at a fixed snapshot, without summary
JSON or evidence content. Its signed cursor binds the target revision, opening
window, repository snapshot, publication revision, and complete ordering key.
Incident mutations invalidate continuations for that target/revision; callers
restart when `cursor_stale` is returned. Evidence retrieval uses its own snapshot.

- MCP adds a diagnostic representation without creating an alternative data or authorization path.
- Tool schemas and allowlists need snapshot/contract tests; new tools require security review and an ADR update when authority changes.
- Some useful operational actions remain intentionally unavailable and require a human through separately authorized product or platform workflows.
- Query text and plan responses require the sensitivity controls in [ADR-0011](ADR-0011-query-text-and-plans-are-sensitive.md).
- SDK churn is isolated to the adapter and compatibility suite.
