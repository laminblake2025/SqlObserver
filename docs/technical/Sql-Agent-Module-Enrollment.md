# SQL Agent module enrollment and guarded reader

The [2026-10-05 qualification checkpoint](../../artifacts/dashboard-agent-module-adapter-2026-10-05/completion-v3.json)
records 340 unit/catalog, 68 HTTP, 8 PostgreSQL and 135 SQL contract cases, plus two actual-monitor native
cases, all with zero skips. These are separate groups. The signed native case retains 12 rows; unsigned
metadata refuses the public capture. Normal cleanup restores the original SQL baseline, with Agent stopped.

The module reader provides a separate, explicitly authorized source path for the fixed SQL17 Windows Agent procedure. Enrollment does not install SQL objects, grant SQL permissions, enable a schedule, or admit running/elapsed-time metrics. The existing direct reader keeps its permission requirements.

## Administrative contract

Migration **0369** adds an append-only enrollment ledger. The server's restricted PostgreSQL role can administer it; the collector's restricted role can resolve current authorization. Runtime roles cannot edit ledger rows directly. Each successful change and its audit record commit together.

Target-scoped **Target Administrator** access is required for both methods on:

`/api/v1/observation-targets/{instanceId}/sql-agent/module-enrollment`

- `GET ?targetRevision=...` returns `targetId`, `targetRevision`, `enrollmentRevision`, and the enrollment or `null`. A valid target with no enrollment has revision zero. Revocation keeps the advanced revision. Expired and future enrollments remain visible to administrators.
- `PUT` requires `expectedTargetRevision`, `expectedEnrollmentRevision`, a nonempty `operationId`, `enrolled`, and an enrollment when `enrolled` is true. Revocation uses `enrolled: false` and no enrollment. Unknown fields and omitted required fields are rejected.
- Enrollment contains `enrollmentId`, `expectedWindowsSid`, `binding`, `notBeforeUtc`, `expiresAtUtc`, and `evidenceSha256`. Binding contains the TCP endpoint and certificate host, server/machine/instance names, object and certificate IDs, the approved definition digest, public certificate digest, and thumbprint. The default SQL instance uses an empty `instanceName`.
- Version constants are module **1**, source **2**, production probe **1** (result schema **2**), and installation policy **2**. They are fixed by the implementation and stored in the ledger; clients cannot select other versions.

The administrator supplies independently reviewed enrollment evidence and its digest. The HTTP endpoint validates the bounded contract; it does not independently perform DBA installation, inspect a journal, or certify the evidence's origin. Pins must come from trusted provisioning evidence, not the monitor's own current probe response.

Writes use compare-and-set revisions. An exact retry of the latest operation returns its original revision. A reused operation with changed content, a stale operation after revocation, or a competing stale revision returns conflict. Re-enrolling identical pins still advances the revision, so an in-flight read detects revoke/re-enroll sequences.

Responses are private and no-store. Missing targets return 404; inactive targets or revision conflicts return 409; repository unavailability returns a sanitized 503. After an uncertain write, read current state before changing it again. Enrollment never changes discovery permission evidence or collection settings.

## Reader contract

`SqlServerAgentActivityModuleCaptureReader` resolves authorization before opening SQL, immediately before submitting the fixed capture command, and after the buffered capture and postchecks. All three resolutions must agree exactly on target, target revision, enrollment revision/ID, expected Windows SID, every binding pin, validity window, and evidence digest. Missing, revoked, expired, or changed authorization refuses the read with no fallback.

One original deadline, capped at ten seconds, covers resolution, connection, probes, capture, and final checks. The reader derives the current Windows SID internally. Its dedicated connection uses Windows integrated authentication, validated encrypted TCP, no pooling, and no transparent connection retry. Before and after capture it verifies current/original SQL identity, non-sysadmin status, absence of CONTROL SERVER/ALTER ANY LOGIN, the physical connection ID and SPID, master context, SQL major/edition, and enrolled server/machine/instance properties.

Driver and server connection IDs are tracked separately and each must remain stable. SQL SPID must match the owned driver's `ServerProcessId`. The driver sends its own tracing ID, whereas the DMV exposes the server's connection identity; no equality between those GUID namespaces is assumed. See Microsoft's [SqlClient tracing documentation](https://learn.microsoft.com/en-us/sql/connect/ado-net/data-tracing?view=sql-server-ver17) and [connection DMV contract](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-exec-connections-transact-sql).

The bounded metadata probe completes as a separate command before capture EXEC can be constructed and sent. It verifies the approved definition, expected object/certificate identities and public bytes, caller execution context, ANSI options, absent signing private key, and positive cryptographic signature evidence. After capture it repeats the probe and identity checks. Removed signatures refuse execution at the precheck. The fixed procedure accepts 513 source rows; the shared three-result parser retains at most 512 and reports truncation.

Results expose the capture and sanitized enrollment revision/ID/evidence digest. They do not expose the enrolled endpoint, Windows SID, SQL object/certificate pins, or raw DBA evidence. Local job dates remain local. Production admission and service/session association remain false; stopped Agent yields unavailable running/elapsed-time conclusions.

## Limits and next work

The reader checks authorization at observation boundaries. It cannot make revocation atomic after its final check, or rule out privileged change-and-restore between metadata probes. A future canonical writer must fence the enrollment revision when committing. This implementation does not freshly verify physical hardware identity, master/msdb database GUIDs, cross-database grant inventories, or arbitrary installer provenance.

Persistent installation/admission, canonical retention/read/API/UI integration, qualified running/session/time semantics, explicit DENY and attached-invalid-signature native cases, and broader load/overflow/cancellation qualification remain separate work. Native tests use a finite, independently constructed enrollment resolver; real PostgreSQL/API authorization is tested separately against disposable PostgreSQL. These are separate evidence boundaries.

The next cohesive slice is retained activity observations. Follow the CPU canonical writer/history and default-off lifecycle catalog patterns. Its new writer must acquire the exact `sqlobserver:agent-module-target:<id>` advisory lock before run/schedule locks, verify current enrollment and its validity while holding that lock, and retain enrollment provenance with the bounded source receipt. Replacement/revocation should atomically disable the future activity schedule and advance its revision. A matching already-committed replay can remain readable after revocation, while failure/gap completion must remain possible without active enrollment so revoked runs can finish.

That slice also needs partition/retention registration and reviewed run references, target-scoped cutoff history and exact detail, explicit collection controls, and an observations view alongside existing Agent failure history. Acceptance must include revoke-versus-commit races, enrollment replacement ABA, stale schedules, replay, failure completion after revocation, detached retention, and scoped HTTP/browser behavior. Persisting a candidate observation must not upgrade `ProductionAdmitted` or association flags, derive elapsed time from local dates, or change existing Agent failure history semantics.
