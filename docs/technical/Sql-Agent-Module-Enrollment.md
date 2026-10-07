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

The reader checks authorization at observation boundaries. It cannot make revocation atomic after its final check, or rule out privileged change-and-restore between metadata probes. The canonical writer in migration0370 now fences enrollment revision, identity, digest and validity while holding the shared target advisory lock at commit. This implementation does not freshly verify physical hardware identity, master/msdb database GUIDs, cross-database grant inventories, or arbitrary installer provenance.

Persistent installation, qualified running/session/time semantics, attached-invalid-signature native cases, and broader native load/overflow/cancellation qualification remain separate work. Native tests use a finite, independently constructed enrollment resolver; real PostgreSQL/API authorization is tested separately against disposable PostgreSQL. These are separate evidence boundaries.

The [retained activity observations slice](Sql-Agent-Activity-Observations.md) now provides canonical receipts, default-off collection controls, enrollment-fenced commit and recovery-backed retention. Replacement/revocation atomically disables collection and advances its revision; exact enrollment replay does not advance it again. Matching committed completion can replay after revocation, while owned failures can finish without current enrollment.

The qualified [activity window history slice](Sql-Agent-Activity-Window-History.md) now provides target-scoped completion-window summaries and independent exact run/sample-revision detail, with an observations view alongside existing Agent failure history. Failed and missing receipts remain explicit, private enrollment fields stay excluded and historical reads remain available after revocation. Persisted candidate observations retain false admission/association flags and absent native qualification; local job timestamps cannot establish elapsed time. Existing failure history keeps its first-observed window semantics. Continuously deployed native-to-dashboard acceptance remains open.

## Effective explicit-DENY qualification — 2026-10-07

The [fresh Run16 evidence](../../artifacts/dashboard-agent-deny-connected-2026-10-07/native-qualification-v1.json) passes four actual-monitor cases, with zero skips: public collector capture while signed, effective permission refusal, invalid-bound refusal while denied, and public collector capture after restoration. The finite schema4/policy2 trial changes only its own certificate user's `SELECT ON msdb.dbo.syssessions` from grant to deny and back. Original signed metadata, direct monitor permissions and enrolled identities remain exact. The public reader refuses with two authorization resolutions and no accepted capture; independent canonical execution returns only SQL229. NULL/512/514 bounds return50001 before source results.

Normal cleanup verifies the original SQL baseline, and each worker's direct child exit and temporary service deletion are recorded. Agent remains stopped and the five existing core services are running. Unconfirmed worker termination retires the dedicated DBA session before restoration/removal and preserves the journal; blind standalone and fallback recovery are disabled pending a reviewed private termination audit. Native interruption and universal historical descendant absence are not qualified.

The two signed collector receipts preserve complete bounded source captures and immutable enrollment revision/ID/digest for private evidence. They use the finite enrollment resolver, not the repository-backed resolver. The original ephemeral authorization window was not exported, so these artifacts cannot establish fresh canonical admission by inventing or extending authorization. A future native replay needs the original full private enrollment preserved before execution and must meet the unchanged five-minute source clock boundary.
