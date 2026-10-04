# SQL Agent activity source boundary

## Dashboard requirement and status

Health checks currently displays completed failure, retry and cancellation history. Its historical duration is not the elapsed runtime of a running job. Running and long-running jobs therefore require a separate source; failure-history records and timeline markers retain their existing meanings.

The staged `SqlServerAgentActivityCaptureReader` captures bounded diagnostic evidence on one owned SQL connection. It is not registered with the collector scheduler, canonical writer, API, MCP or dashboard. Native qualification and production admission remain absent. Adding this source does not complete the running or long-running job requirement.

## Evidence boundary

The source brackets up to 512 activity rows with source UTC clocks, SQL Agent service status/process/startup evidence and the latest session from `msdb.dbo.syssessions`. It fetches one lookahead row and reports truncation. Job identifiers use a deterministic canonical UUID ordering; rows retain their source session and offset-free request, queue, start, stop and last-executed-step clocks. No service account, executable path, job command, job name or step text enters the capture.

An Agent session identifies an Agent service epoch, not a SQL session. SQL Agent creates a new session at startup, and unfinished activity can survive an unexpected Agent stop. Therefore `MAX(sysjobactivity.session_id)` and a non-null start with null stop cannot independently establish present liveness. The candidate evaluator requires stable before/after service and latest-session evidence; stopped, ambiguous, absent and changed epochs remain explicit. Service/session association is still unqualified: a newly started service can be observed before its new session is recorded. `CandidateState` therefore never implies qualified live state, and `ServiceSessionAssociationQualified` remains false. [Microsoft Agent-session documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/dbo-syssessions-transact-sql?view=sql-server-ver17), [activity table](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/dbo-sysjobactivity-transact-sql?view=sql-server-ver17).

The service DMV concerns the current SQL instance. Identification uses a bounded executable basename instead of localized service display names; ambiguous or unrecognized matches cannot establish liveness. Its native interpretation and permission behavior still need qualification. SQL Server 2019 requires `VIEW SERVER STATE`; SQL Server 2022 and later require `VIEW SERVER SECURITY STATE`. [Microsoft service DMV documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-server-services-transact-sql?view=sql-server-ver17).

Managed checks establish parsing, identity, bounds, cancellation and conservative candidate evaluation. They do not certify a native service observation, all-job visibility, an execution interval or health.

## Time and long-running policy

Job clocks in `sysjobactivity` are local `datetime` values without offsets. Preserve them as `DateTimeKind.Unspecified`; do not subtract collector UTC, attach the current offset, or silently resolve daylight-saving folds and gaps. `last_executed_step_id` means the last executed step, not the currently executing step.

The capture exposes no elapsed duration or long-running verdict. Repeated job/session/start observations and Agent uptime do not prove uninterrupted whole-job execution between polls. A future duration contract needs independently qualified execution identity and time evidence, explicit unavailable states for ambiguous/skewed clocks, threshold provenance and explicit exemptions for continuous jobs. SQL Server 2022's current time-zone ID reports current configuration; it does not independently attest historical configuration or elapsed runtime. [Current time-zone ID](https://learn.microsoft.com/en-us/sql/t-sql/functions/current-timezone-id-transact-sql?view=sql-server-ver17).

## Native prerequisites

No monitoring login is enrolled in `sysadmin` or an Agent control role by this change. The staged reader retains the repository's integrated, encrypted connection policy and requires a non-sysadmin, target/revision-bound profile plus explicit activity/session and version-specific service permission evidence. Existing failure-history or replication permission evidence does not substitute for this source. Discovery and installation of a qualified least-privilege read path remain open.

Microsoft documents restricted access to `syssessions` and to other-owned activity through `sp_help_jobactivity`. Direct grants must not be assumed to bypass those restrictions. An administrator-installed fixed signed read module is one candidate for native qualification; this stage installs none. [Activity procedure permissions](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-help-jobactivity-transact-sql?view=sql-server-ver17).

Before activation, qualify supported Windows SQL versions and the actual monitoring identity against independently controlled lab cases: queued/running/completed jobs, failure/cancellation, Agent restart and abrupt stop, ambiguous/unavailable service identity, denied and incomplete visibility, empty inventory, row-cap overflow, source clock changes and deadline cancellation. Qualification must preserve the exact source bytes and native receipts. No job-control mutation is part of the monitoring source.

The existing SSH lab route has an unresolved connection failure. This checkpoint does not repeat an unchanged network probe or claim native qualification.

## Remaining integration

1. Qualify native permissions, service/session interpretation and the execution-duration contract.
2. Persist immutable per-run receipts, including failed/partial/empty attempts, through the canonical fenced commit and registered retention dependencies in a forward migration.
3. Add protected completion-window and independent exact-run reads, preserving cutoff/epoch, target revisions, expiry, cancellation and role scope.
4. Add the activity panel to the existing SQL Agent tab. Retained evidence must say when activity was observed rather than claim that a job is running now; the newest failed attempt must not be replaced by an older success.
5. Qualify mounted and built-App URL, paging, denial/stale-response, dark-theme, keyboard and responsive behavior before declaring that panel complete.

Current Health checks theme and interactions continue to use their existing data. The accompanying narrow tab-focus repair keeps the focus indicator inside the horizontal tab scroller; it does not add a current-job panel.
