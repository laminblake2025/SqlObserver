# Collector assets

Collector SQL and manifests begin with the passive `capability.connection` slice in
Milestone 3. Its checked-in statements are fixed, parameterless, one-row probes for
SQL Server 2019, 2022, and 2025 on Windows. They discover normalized version,
platform, transport, authentication, privilege, and permission evidence without
mutating the monitored instance.

Every collector manifest must declare its identity, required capabilities and
permissions, supported targets, cadence bounds, timeout, row ceiling, estimated cost,
fallback, and output schema version before implementation is accepted. SQL assets are
embedded with a checksum manifest, bounded by their adapter, and may not contain
configuration, DDL, DML, dynamic SQL, undocumented interfaces, or arbitrary input.

Least-privilege setup is deliberately outside the service execution path. Use
`tools/generate-permissions.ps1` to create an offline DBA-reviewed grant or removal
plan for the exact supported SQL Server major version.

## M6 system_health deadlocks

The `deadlocks.system-health` bundle is a passive order-8 collector for SQL Server
15, 16, and 17 on Windows. It derives the trusted directory from the existing
`system_health` event-file target and uses only the fixed `system_health*.xel`
rollover pattern; it reads only `xml_deadlock_report` rows through
`sys.fn_xe_file_target_read_file`. The service has no XE create/alter/start/stop
path and never changes blocked-process settings. XML is bounded and parsed with
DTD/entity resolution disabled; only typed participant/relation evidence and an
opaque fingerprint leave the adapter.
