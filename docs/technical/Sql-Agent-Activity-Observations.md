# Retained SQL Agent activity observations

The activity observation path uses the [guarded module reader and trusted enrollment](Sql-Agent-Module-Enrollment.md). It retains bounded source evidence without admitting a live running-job count, physical elapsed duration, current step, or long-running alert. Stopped Agent remains `ServiceNotRunning`, with `Unknown` candidates.

## Collection and authorization

Migration **0371** installs the pinned order-22 collector `sql-agent.activity-observations` with collection disabled for existing and new targets. It supports SQL17 Windows Standard/Enterprise, requires validated integrated TCP/TLS and non-sysadmin discovery, and depends on `capability.connection` and canonical `engine.core` eligibility. Direct `syssessions` grants are not a prerequisite for this separate enrolled source path. The collector calls the public guarded reader; its descriptive invocation asset is never executed as an alternate path.

Target-scoped Viewer, Operator and Target Administrator roles can read collection state. Only Target Administrator can mutate it through:

`/api/v1/observation-targets/{instanceId}/sql-agent/activity-observations/collection`

GET requires a canonical single `targetRevision`. PUT requires `expectedTargetRevision`, `expectedScheduleRevision`, `expectedEnabled` and `enabled`; unknown or omitted fields are rejected. Responses are private/no-store. Enablement requires current enrollment, its exact validity window, and endpoint/port/certificate/transport agreement with the current target. Schedule changes and their audit receipt commit atomically. After an uncertain write, read current state before retrying a change.

Every applied enrollment replacement or revocation atomically disables this schedule and advances its revision. Exact enrollment replay does not advance it again. Re-enrollment never resumes collection. Target definition changes also disable collection; reconciliation preserves explicit same-revision opt-in only while enrollment remains valid. These controls do not install the SQL module or configure its grants.

## Canonical receipt and fences

Migration **0370** adds daily partitioned, append-only `telemetry.sql_agent_activity_receipt`. The restricted runtime roles cannot read or mutate it directly. The fenced writer atomically retains the receipt, canonical outcome, visibility gap when applicable, and owned schedule transition.

The writer acquires the exact enrollment target advisory lock before lease/run/schedule locks. New successful or partial completion requires the immutable run identity, current lease, target and schedule revisions, active run ownership, and current enrollment revision/ID/evidence digest. The source bracket must lie inside the enrollment window, span at most ten seconds, and meet the bounded five-minute repository clock boundary. UTC comparisons preserve the seventh fractional digit separately from PostgreSQL's microsecond timestamp precision.

Matching committed completion can replay after revocation. A divergent completion cannot rewrite it. A still-owned failed run may finish after administration advances the schedule; it clears only its active run ID and preserves the new revision, enabled state, cadence and circuit settings. The existing target revision trigger already terminalizes affected active runs; a later success is a divergent replay.

The closed projection contains source schema **1**, before/after service and latest-session evidence, ordered unique jobs, exact source accounting, and sanitized enrollment revision/ID/evidence digest. Module metadata result schema **2** remains a separate version. Endpoint, Windows SID, SQL object/certificate identities, signing pins and raw DBA evidence are excluded. Admission flags remain `false`, `false`, and `Absent`.

At most **513** job source rows are read; **512** jobs are retained. The sentinel produces a partial receipt with an inexact minimum-one-row loss. Zero jobs still produces one capture receipt. Logical response accounting is `4096 + sourceRowsRead × 768`, and estimated output accounting is `4096 + retainedJobs × 768`; these are declared conservative bounds, not measured TDS bytes. Response, observation and persistence envelope limits are 524288, 524288 and 530000 bytes respectively.

Job timestamps are exact seven-digit, offset-free local strings. They are never assigned UTC or subtracted to derive elapsed time; reversed local dates remain valid source evidence. `lastExecutedStepId` describes the last executed step and does not identify a current step. Candidate state is evaluated from the retained source and its explicit gap.

## Retention and remaining acceptance

The new retention policy defaults off, with a minimum 90-day lifetime and three retained daily partitions. Partition registration, reviewed canonical-run references, recovery attestation, detach/drop and restoration follow the existing retention protocol. Receipt retirement preserves the canonical run until its own retention permits removal.

Final owning groups pass **487 unit**, **100 HTTP**, **78 PostgreSQL integration** and **1 isolated retention** cases, all with zero skips. The failed accounting attempt and its correction are retained in the evidence.

The [qualification record](../../artifacts/dashboard-agent-activity-observations-2026-10-06/completion.json) records the final owning groups, source archive, failed attempts and independent review. PostgreSQL tests use synthetic Agent captures against disposable pinned PostgreSQL18.4. The earlier actual-monitor reader qualification is separate; this slice does not constitute a new native SQL capture, live migration, persistent installation, opt-in, deployment, or connected collector-to-dashboard acceptance.

The qualified [activity window history slice](Sql-Agent-Activity-Window-History.md) now supplies scoped completion-window summaries and independent exact run/sample-revision detail, with an observations panel alongside existing Agent failure history. Failed and missing receipts remain explicit; a selected run never substitutes an older success. Historical reads remain available after enrollment revocation and do not resolve authority to perform a new capture. Existing failure history retains its seven-day first-observed window semantics. Running/session/time qualification, persistent provisioning and connected collector-to-dashboard acceptance remain separate work.
