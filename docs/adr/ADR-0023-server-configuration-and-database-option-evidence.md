# ADR-0023: Collect exact server configuration and database-option evidence

Status: proposed, 2026-09-29

## Context

The review plan calls for server configuration drift from `sys.configurations`,
database-option changes, and `log_reuse_wait_desc` on the Health checks and
shared timeline. Migration 0249 and its successor already compare recovery
model, access mode, read-only status, and compatibility level in consecutive
complete `database.inventory` runs. They do not collect server configuration or
log reuse state. The current server timeline can mark the option changes, but
the exact time of a change remains unknown between two inventory observations.

The collector framework pins read-only SQL, bounds rows and bytes, commits under
a fenced lease, and records sample loss. `MetricSample.Value` is a `double`.
Using ordinary metrics for configuration values would risk precision loss and
would not preserve the distinction between `value` and `value_in_use`.
Changing the existing version-1 database inventory SQL in place would also
break its immutable asset checksum and persisted collector contract.

Microsoft documents `sys.configurations.value` as the configured value,
`value_in_use` as the running value, and `is_dynamic` as whether a change can
take effect without a restart. SQL Server 2022 and later require `VIEW SERVER
PERFORMANCE STATE` to read the view; earlier versions allow `public`. The
view can show different configured and running values for self-configuring
options, so a difference alone is not proof that a restart is pending. See
[sys.configurations](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-configurations-transact-sql?view=sql-server-ver17).
`sys.databases.log_reuse_wait_desc` describes the current reason log space
cannot be reused as of the last checkpoint. It is health state, not a database
configuration setting. See
[sys.databases](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-databases-transact-sql?view=sql-server-ver17).

## Decision

Add a dedicated passive, slow-schedule `engine.configurations` collector. Its
versioned, checksum-pinned query reads only `sys.configurations` and returns a
bounded row for each visible `configuration_id`: name, exact configured value,
exact in-use value, `is_dynamic`, `is_advanced`, source type, and one UTC scan
time. Store exact values as validated text with an explicit source type, not
as floating-point metrics. A conversion or validation failure makes the scan
incomplete; it must never silently omit a configuration. The collector makes
no SQL Server configuration changes.

The query reads one extra row beyond the published cap to detect truncation.
Set a slow default interval of five minutes, a one-minute minimum, a fixed
command budget, and a bounded response size. Keep scheduling disabled until
the SQL Server 2019, 2022, and 2025 queries are qualified with the actual
least-privilege login and validated TLS. The manifest and bundle have their
own immutable version and digest; existing M4 assets remain untouched.

Persist one canonical `telemetry.server_configuration_snapshot` row per
configuration ID and completed run, with target ID/revision, SQL Server major
version, observed time, name, exact configured/in-use values, and dynamic and
advanced flags. The run reference follows the collection-run identity bridge
in [ADR-0021](ADR-0021-canonical-retention-graph.md). Use forced row-level
security, target scope, append-only writes, restrictive grants, and a bounded
retention class. A new fenced commit path must validate the full payload and
run accounting before publishing a successful scan. Failed, partial, or
truncated scans retain their run outcome but publish no configuration change
events.

Derive `events.server_configuration_change` during the successful commit, by
comparing the new complete scan with the immediately preceding terminal scan
for the same target revision and server major version. Compare only matching
configuration IDs and names. The first complete scan establishes a baseline.
If a failed, partial, or truncated scan intervenes, the next complete scan
establishes a new baseline instead of attributing a change across that gap.
Store previous and current configured and in-use values, both observation
times, the detecting run, and the repository detection time. A change event
means **observed difference**, not a known SQL change time or identified actor.
Deduplicate by detecting run and configuration ID so a replay cannot duplicate
an event. Bound event size and count per run. This commit-time projection avoids
rescanning many five-minute snapshots for each dashboard request.

The target-scoped API exposes a bounded seven-day change list with a stable
cursor, explicit coverage and gap state, and links to the two underlying
snapshots. Require a Viewer, Operator, or TargetAdministrator grant on that
same target before repository access. Health checks shows the current
configured/in-use values, observed differences, and the reason evidence is
missing. The timeline marks repository detection time and labels it as such.
An optional alert rule for selected configuration IDs may be added only after
the exact event and coverage contract is in place; a missing scan is never an
"unchanged" or healthy value. MCP can expose the same target-scoped read once
its output schema and audit contract are updated.

Extend database inventory under a **new collector version** to capture
`log_reuse_wait_desc`; do not edit the pinned version-1 SQL. Present it as a
current database health fact and optionally as a state-transition marker.
Do not call every transition configuration drift: checkpoint, active
transactions, log backups, and availability work can change the value without
a DBA configuration action. Existing database-option change history remains
separate and keeps its complete-run and rename guards.

## Delivery and verification

1. Add the snapshot and change-event schema in forward migrations, with owner,
   grants, forced-RLS and identity-FK catalog checks. Stage retention according
   to the dependency graph; do not enable deletion before the run-reference
   and reader leases are handled.
2. Add versioned SQL Server 15/16/17 query assets, source parser, output
   contract, and collector registration. Verify the exact query and permissions
   against the three supported major versions, including an empty or
   over-limit scan and a conversion failure. Keep the new schedule disabled
   until these checks pass.
3. Add fenced repository commit, replay, gap, and drift tests in PostgreSQL.
   Exercise a changed configured value, a changed in-use value, a self-
   configuring difference, a failed intervening run, rename/ID reuse, target
   revision change, output truncation, duplicate replay, and mixed role grants.
4. Add the bounded API, Health checks view, and shared timeline markers. Test
   target authorization and URL time restoration. Verify the displayed
   detection time does not claim an exact SQL change time.
5. Version the database-inventory collector for log reuse state after the
   server configuration path is running. Qualify its SQL visibility and
   distinguish a normal changing wait reason from a configuration change.

This ADR defines the implementation boundary. It does not claim the server
configuration collector or log reuse feature has been shipped.

Migration 0251 stages the private snapshot and change-event tables with run
identity links, append-only triggers, forced RLS, and no direct server or
collector grants. The collector, fenced writer, retention route, and reader
are staged separately before any schedule is enabled.

Migration 0252 and the server adapter add a bounded, target-scoped change read
with keyset paging. A database trigger increments its publication generation
in the change insertion transaction; a changed
generation invalidates a continuation. The HTTP cursor uses the reusable
purpose-bound API HMAC signer. A configured `SqlObserver:Api:CursorSigningKey`
keeps cursors valid across server processes; without one, a process-local key
allows paging until restart and logs the limitation. No configuration data is
published by these read-path changes alone.
