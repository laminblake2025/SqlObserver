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

The [later connected acceptance](../../artifacts/dashboard-agent-deny-connected-2026-10-07/local-qualification-v1.json) exercises the production collector through its internal test-only synthetic source delegate, real collector-role enrollment resolution, sealed canonical repository, scoped root HTTP and built UI without API interception. Synthetic capability evidence and test authentication are explicit. Success, sanitized permission failure, committed replay, revocation disabling the schedule and historical reads pass; public source clocks remain exact and admission/association flags remain false with qualification `Absent`. It is a disposable connected fixture, separate from the [four passing Run16 actual-monitor cases](../../artifacts/dashboard-agent-deny-connected-2026-10-07/native-qualification-v1.json). Native-source canonical replay and continuous deployed acceptance remain unqualified.

## Finite native-source replay — 2026-10-07

The [Run17 replay record](../../artifacts/dashboard-agent-native-replay-2026-10-07/completion-v1.json) subsequently qualifies one original restored-phase native capture through restricted canonical PostgreSQL18.4 admission, root HTTP and the built Operations UI. The actual monitor passes the four signed/DENY/restored cases again, with normal baseline cleanup. Before the first monitor launch, the exporter preserves the full original enrollment and validated installed snapshot in a bounded, protected private sidecar. The portable parser links the unchanged raw artifact hashes, original target/revision/run, authority window, SID and pins, exact TRX identity, four native results and cleanup evidence. It does not repeat live DBA validation.

The restored source bracket is `2026-10-07T06:21:33.4340334Z` through `2026-10-07T06:21:33.4355773Z`. Fresh repository completion at `2026-10-07T06:24:19.392235Z` meets the unchanged five-minute source fence and original authorization window. No source timestamp or authority is reconstructed, extended or backdated. Canonical committed replay, revocation disabling collection, historical list/exact reads, all 12 retained jobs and their local source dates, private-field exclusion and browser query bindings pass. Admission/association flags remain false and qualification `Absent`.

The final groups pass **137 portable contracts**, **one explicitly synthetic downstream rehearsal**, **four actual-monitor native cases** and **one native-source replay journey**, with zero skips. Failed compilation, traffic-count and initial wire-format attempts are preserved. The native replay uses test authentication and synthetic capability/scheduling prerequisites; it does not invoke the collector again or qualify its native repository-backed resolver. Running/session/time semantics, native interruption, persistent provisioning and continuously deployed acceptance remain open. Ordinary validation includes the portable contracts and excludes both opt-in browser journeys.

## Finite native repository resolution — 2026-10-07

The later [Run20 checkpoint](../../artifacts/dashboard-agent-repository-native-2026-10-07/completion-v1.json) qualifies the actual Windows monitor's public reader and observations collector with the concrete `PostgreSqlSqlAgentModuleEnrollmentPort`. Eight cases pass with zero skips across signed, explicit-DENY, restored and repository-revoked phases. The repository-backed DENY case exercises the public reader; signed/restored capture and revoked refusal exercise the observations collector. The four repository cases obtain three, two, three and one fresh authoritative answers respectively; the original authorization is used only for independent comparison. Its exact validity window, revision, enrollment ID and evidence digest are retained in the protected original sidecar. Revocation refuses a new capture while that original window is still active.

The repository is a dedicated loopback PostgreSQL18.6 cluster restored from a blank, owner/ACL-preserving PostgreSQL18.4 archive with all372 migrations and sealed canonical routes. Private collector/server login roles use only their corresponding safe fixed group. Both the session login and effective collector role lack direct control-table and column mutation privileges. The existing guest PostgreSQL cluster is unchanged. Relation ACL comparison preserves raw inventories and independently compares PostgreSQL-expanded grantor, grantee, privilege and grant-option tuples; all1,621 relations match despite309 equivalent raw representations. All other state sections, including644 function definitions/raw ACLs, retain their original comparison. Seven real ACL mutations in a separate private probe are refused and each restoration passes. Column and future-object default-creation ACLs are explicitly outside that inventory comparison.

All four worker termination fences, normal SQL baseline cleanup and owned PostgreSQL clean shutdown pass. The worker fence verifies cooperative receipts, exact native child exit and temporary service stop/deletion; exhaustive historical descendant cleanup remains unverified and standalone recovery stays disabled. Earlier failed trials/probes and the two pre-launch wrapper repairs are preserved. The owning checks also pass76 portable input contracts and one blank archive producer case. Public admission/association remain false with qualification `Absent`; service/session, running/local-time/elapsed, interruption, persistent provisioning and continuously deployed acceptance remain open. Run17's recorded-source dashboard replay remains separate from this finite native collection qualification.

## Controlled running-job preparation — 2026-10-07

Two opt-in actual-monitor tests now require a concrete repository enrollment answer on every
authorization call, matching before/after Agent process/startup/session headers, and one exact
owned Running or Stopped candidate. Their closed private receipt retains the full source and
repository provenance plus ten independently supplied fixture fields. An opaque witness digest
binds the coordinator comparison; it supplies no enrollment authority or association admission.
The existing harness emits the receipt only after all final identity and metadata checks.

The new **55** portable contracts and **76** existing repository contracts pass without skips.
The SQL fixture/journal tooling suite passes **578** cases. These are managed proofs; the two
new native facts have not run. The [read-only guest baseline](../../artifacts/dashboard-agent-running-job-2026-10-07/baseline-probe-03/guest-baseline-20261007T084743815Z.json)
verified the target and unchanged stopped Agent state, but found **11 enabled existing jobs**.
Starting Agent remains gated until a reviewed isolation strategy is available. No existing job,
database, service configuration or Agent state changed. The earlier failed diagnostic attempts
and corrected negative test fixtures are retained in the preparation evidence.

Native execution and cleanup, service/session association, elapsed time, interruption recovery,
persistent provisioning and continuously deployed acceptance remain open. Admission/association
stay false, qualification stays `Absent`, and derived elapsed/UTC job-start/long-running fields
stay null.
