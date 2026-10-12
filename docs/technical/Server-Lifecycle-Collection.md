# Server lifecycle collection

`server.lifecycle` retains sampled SQL Server engine identity and startup evidence. It can detect a changed startup incarnation or responding-node fingerprint between comparable completed observations. Detection time is repository completion time; neither a local SQL startup timestamp nor an observation bracket supplies an exact occurrence UTC or cause. Availability-group snapshots remain unqualified and `sourceConsistencyVerified` remains false.

## Default-off admission

The production collector registry places schema 1 at order 21, with a checksum-pinned manifest, schema and unchanged passive SQL source. The manifest declares SQL Server 2019–2025 on Windows, a 30-second default cadence, 10-second minimum, five-second command timeout, 1,024 rows and 1 MiB bounds. Runtime capability and permission checks remain mandatory. Successful native evidence currently covers SQL Server 2025 under the actual non-sysadmin monitor on the standalone lab; this does not qualify every declared version or topology.

Forward migration `0368_server_lifecycle_catalog.sql` installs the immutable contract, exact reconciliation entry and role-scoped collection control. Existing and newly enrolled targets start disabled. The connection-discovery prerequisite belongs to the manifest dependency catalog; `engine.core` is the scheduled prerequisite. Reconciliation preserves an existing opt-in at the same target revision. A changed target definition disables lifecycle collection and requires a fresh explicit decision. Retention policies remain separately controlled and disabled by default.

The collector role receives only the existing fenced lifecycle commit and partition-set entry points. Private commit helpers and direct table access remain unavailable. The writer retains its target, schedule, active-run, lease and canonical completion checks. Enabling a schedule does not change SQL permissions, restart services, or establish source consistency.

## Collection controls

The dashboard exposes current lifecycle collection state independently of the selected historical window. Same-target Viewer, Operator and Target Administrator roles may read it. Only a same-target Target Administrator can change it.

- `GET /api/v1/observation-targets/{targetId}/lifecycle/collection?targetRevision={revision}` returns `targetId`, `targetRevision`, `scheduleRevision` and `collectorEnabled`.
- `PUT` to the same path requires `expectedTargetRevision`, `expectedScheduleRevision`, `expectedEnabled` and `enabled`. Unknown body fields are refused. A successful changed state increments schedule revision exactly once; a matching no-op preserves it and is audited.

Both target and schedule state are checked under repository locks. A stale target, stale schedule, or enable/disable/enable sequence conflicts even when the boolean value returns to its earlier value. Denied administration is audited; successful/conflicting repository mutations use the existing administrative audit transaction. PUT is authenticated and rate limited. State and errors are private/no-store.

The UI binds request ownership to target, revision and current authority, including A → B → A navigation. It prevents overlapping reads/writes and clears the old setting when a mutation starts. An uncertain or conflicting response requires an explicit authoritative GET before another mutation; there is no blind PUT retry. A denied write hides mutation controls until authority changes. This control does not clear or reinterpret retained history.

## Evidence boundaries

The recorded-native replay preserves two actual source snapshots through the production canonical writer and history port. New repository start/completion clocks and zero replay duration describe isolated ingestion. A replay can construct work from its exact installed schedule without claiming the live `engine.core` prerequisite was observed; scheduler eligibility is tested separately using clearly synthetic canonical engine fixtures.

Stable native samples yield two receipts and no inferred events. The initial comparison boundary remains a visibility gap. Restart transitions, physical failover, qualified AG consistency, in-flight native faults, continuously deployed collection, supported-browser certification and manual accessibility remain separate qualification work. No live migration or opt-in is implied by the implementation or disposable test results.

Current evidence and next work are recorded in the [dashboard handoff](SqlObserver-Dashboard-Refactor-Handoff-2026-10-02.md) and lifecycle admission evidence (local output: `artifacts/dashboard-lifecycle-admission-2026-10-05/completion.json`).
