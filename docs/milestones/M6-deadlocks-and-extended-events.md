# M6 — deadlocks and Extended Events

M6 is a passive, read-only system-health integration. The Collector reads only
`xml_deadlock_report` rows from the already-running SQL Server `system_health`
event-file target through documented XE catalog surfaces and
`sys.fn_xe_file_target_read_file`. The SQL derives only the trusted target
directory and fixed `system_health*.xel` rollover pattern, never a client path.
Before invoking the TVF, SQL validates the running `system_health` event-file
configuration through `sys.server_event_session_fields`: rollover count must
be 1–10 and the documented `max_file_size` value (MiB) must be 1–100 MiB.
With those caps proven before invocation, the wildcard reader starts at the
beginning of the finite retained set (paired NULL initial-file/offset), so
prior rollovers remain covered; the 33-day UTC predicate is a secondary
occurrence-window bound. Reads use a deterministic newest-first
window; invalid configuration emits explicit degraded loss evidence. Bounded
source-state sentinels distinguish empty, missing, invalid, and oversized source
rows; an oversized sentinel is handled as loss/abort before its LOB can reach
the client, and absence is never reported as a successful read. It never creates, alters, starts, stops, or
repairs an XE session and never changes blocked-process settings. No client path,
SQL text, or target configuration is accepted.

Event XML is hostile input. Reads are version-pinned for SQL Server 15–17 on
Windows, ordered deterministically, cancellable, and bounded by rows, response
bytes, XML bytes, depth, node count, and parser time. Only UTC occurrence,
The logical batch cap is 256 observations; the reviewed provider maximum is 257 so the collector can consume the 257th row as a bounded truncation probe and record explicit source-row loss.
opaque SHA-256 fingerprint, bounded participant/session counts, allowlisted lock
categories/modes, and blocker/waiter relations are retained. Raw XML, SQL text,
resource descriptions, object names, host/login/application/network values,
provider errors, and paths are intentionally not persisted or returned. A
deployment-backed content-protection/key service is not certified for this
slice, so raw XML retrieval is deliberately unavailable.

Malformed or oversized events, missing/incompatible `system_health`, permission
denial, timeout, source-row truncation, response-byte truncation, and parser
loss are explicit degraded evidence. No self-repair is attempted. The API is
target-scoped and bounded (`/api/v1/observation-targets/{instanceId}/deadlocks`)
with UTC windows, opaque target-bound cursors, safe DTOs, and Windows/RBAC
authorization.

List traversal and the fenced collector commit take the same target-scoped
transaction advisory lock. The cursor watermark is therefore captured only
after any competing commit is visible (or before a waiting commit begins), so
an in-flight commit cannot be skipped by a later page.

The next dependency is M7 Query Store/plan-cache fallback. It is not part of
this milestone. PostgreSQL integration and M12 certification remain required
deployment gates where the environment cannot provide Docker/runtime evidence.
