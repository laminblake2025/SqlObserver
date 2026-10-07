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

## Running-job source-readiness checkpoint — 2026-10-07

The later [completion-v2 record](../../artifacts/dashboard-agent-running-job-2026-10-07/completion-v2.json)
binds the [source-readiness-v2 freeze](../../artifacts/dashboard-agent-running-job-2026-10-07/source-readiness-v2.json),
**313** passing pure managed receipt/coordinator/worker contracts with zero failures/skips,
and a coordinator build with zero warnings/errors. **Zero native running/completed cases**
have executed. These results prepare the witness and cleanup protocol without admitting source
association or execution/time semantics.

[Probe05](../../artifacts/dashboard-agent-running-job-2026-10-07/baseline-probe-05/guest-baseline-20261007T093858172Z.json)
adds three unfinished all-history activity rows in older Agent session2 to the existing eleven
enabled foreign jobs. Latest session4 has no unfinished row, but that does not satisfy the
all-history gate. Two retained rows have starts and one is requested without a start; NULL stop
and history IDs do not prove completion or cancellation. Agent remains stopped with SCM PID
zero. Both gates refuse the current trial; original job flags and msdb history remain unchanged.

The preservation-compatible [clean-lab plan v2](../../artifacts/dashboard-agent-running-job-2026-10-07/clean-lab-plan-v2.json)
proposes a fresh SQL17 default-instance destination. Destination and installation scope remain
pending; named instances are not supported by the current controller. Fresh physical identity,
TLS, integrated monitor SID/configuration, deployed target/CPU host binding, revision and
original authority must be qualified independently. A fresh-lab finite proof would not complete
acceptance for the original selected target or continuously deployed collection.

The separate [signature/journal tooling suite](../../artifacts/dashboard-agent-attached-invalid-2026-10-07/managed/trial-signature-contracts-attempt1.trx)
passes **709** pure managed cases with zero failures/skips, comprising the previous **578**
plus **131** new signature-construction and rollback-journal contracts. This supersedes the
earlier tooling count; it is not an additional 709 cases. The separate 313-case coordinator group
is not an aggregate unique-test total with that suite.

The later [Probe06 construction report](../../artifacts/dashboard-agent-attached-invalid-2026-10-07/construction-probe-06/guest-signature-construction-20261007T111503590Z.json)
and [completion record](../../artifacts/dashboard-agent-attached-invalid-2026-10-07/completion-v1.json)
record rollback-only module mutations on SQL Server build 17.0.1000.7, rather than a read-only probe. The signed
image and distinct genuine 392-byte target/donor signatures passed verification. Importing the
donor signature through `ADD SIGNATURE` was refused with SQL error 15299, state 1, and outcome
`UnsupportedDonorSignatureImport`; no attached-invalid postimage was established. The result is
specific to that candidate and observed build. Both construction and readback rollback
acknowledgements, exact absent-module baseline, unchanged foreign-inventory digest and held
stopped-Agent configuration passed. Cleanup and journal `rollback_verified` were confirmed;
the exact probe process was joined after exit 0. The first five pre-mutation failed attempts remain
preserved.

Construction feasibility and native attached-invalid qualification remain false; zero native
collector cases executed and no monitor launched. No invalid-signature monitor refusal,
committed-invalid recovery or production admission is qualified. Admission/association remain
false, qualification `Absent`, and elapsed/UTC job-start/long-running fields null. Persistent
provisioning, interruption recovery and continuously deployed acceptance remain open.


## Worker interruption audit preparation — 2026-10-07

`TrialWorkerTerminationAudit` adds a closed, immutable preparation contract for a future
independently supervised interruption. `TrialJournal.ReadWithDigest()` authenticates the opened
file handle and parses and hashes the same bounded bytes. Pure parsers cannot manufacture
protected-read provenance. The contract binds the original supervisor, both suspended roots,
complete process ledger, source pins, journal phase and deployment/fixture identities. Every
process birth must fall between the independently held supervisor birth and the interruption.

The expanded whole tooling suite passes **833** cases with zero failures or skips: **709** prior
contracts plus **124** audit/journal contracts. Definitions, results, execution IDs and test entries
link uniquely. Build validation reports zero warnings or errors. The independently reviewed
[frozen evidence](../../artifacts/dashboard-agent-interruption-2026-10-07/audit-source-freeze-v3.json) records the exact source and test hashes.
These totals supersede the earlier whole-suite count; they do not add to it.

This is managed preparation only. Selection can return `EligibleForIndependentSqlValidation`;
it cannot authorize SQL mutation, recovery, replay or a worker launch. Existing native recovery
refusals remain enforced. A future gateway must independently prove all contained processes
exited and the owned scope is empty, then fence and drain both the monitor SQL epoch and the
coordinator DBA SQL epoch (or prove the coordinator has no SQL connection). The current monitor
binding alone cannot discharge that coordinator requirement. SQL local `datetime` epochs remain
separate from full-precision UTC process observations.

The separate harmless containment prototype is still being qualified. Its complete published
runtime closure, explicit launch environment, original held process epochs, external supervisor
watchdog and native token/profile behavior require their own evidence. No native interruption,
containment or standalone recovery case is qualified by these contracts; earlier Run16/Run20
are not retroactively contained. Production/association/continuous admission remains false,
qualification remains `Absent`, and derived execution/time fields remain null. The broader goal
is active; the separate clean SQL17 default-instance destination remains unresolved.


## Harmless watchdog preparation and native refusal — 2026-10-07

The separate containment prototype v6 passes **138** pure checks and validates its actual
self-contained 192-file Windows payload. The separate watchdog v2 passes **76** pure checks;
its own actual 192-file payload and repaired Windows PowerShell 5.1 bounded output capture
were verified and independently reviewed. These are separate preparation groups, not additions
to the 833-case tooling suite. Source packages and failed SDK-shape checks remain preserved.

The [first watchdog VM report](../../artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-01/guest-harmless-watchdog-20261007T123815783Z.json)
observes a freshly created harmless Administrator supervisor assigned before resume. Its deadline
expired, termination of the held private Job was accepted, the original supervisor exited with
code76, and the queried Job had zero active members. The original watchdog driver also exited
and was joined. Qualification nevertheless refused: lifetime `TotalProcesses` was **2**, while
the reviewed gate requires **1**. The additional historical member has no identity/held-exit
provenance. An empty current scope does not establish that missing provenance.

The [reviewed refusal evidence](../../artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-refusal-v1.json) preserves raw duplicate-free
output, exact package/source pins, original process epochs and the fresh nonce. The original
candidate must not be rerun unchanged, and its count gate must not be loosened. The next fresh
diagnostic must observe suspended supervisor membership in any existing Job before assignment,
then record bounded accounting and owned-Job PID-list snapshots without opening or adopting
those PIDs. Unknown nesting must refuse before assignment. No monitor logon, CWT or SQL path
was exercised. This does not qualify worker interruption/recovery, CWT/startup hard timeouts,
all historical members, production admission or the broader goal.


### Watchdog membership stage observation (2026-10-07)

A fresh, independently reviewed v3 diagnostic-only watchdog run located the membership discrepancy between snapshots. The private Job was empty before creation, and the suspended original supervisor belonged to no pre-existing Job. After assignment and before resume, the complete stable list contained only original PID19064 (total1/active1). The later pre-termination snapshot contained PIDs6228 and19064 (total2/active2). After held-Job termination the original supervisor exited76 and the Job reported total2/active0 with an empty complete list. The driver joined94, `PreserveForInspection`, refusing at `BeforeTerminationEvidence`.

The extra member was first observed post-resume; its exact association time and identity remain unknown. Diagnostic PID6228 was never reopened or adopted. No conhost/apphost/other identity is inferred. Native02 is unqualified and retired from unchanged retry; native01 remains preserved. No CWT, SQL, monitor, service or rights mutation occurred. Recovery and production admission remain closed.

Actual report: `artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-02/guest-harmless-watchdog-20261007T131500518Z.json`, SHA256 `2784d7427e01fe0e5d3d0ff84e590bebf4fa43f1c0c62f0137365c3c4dc8eb0c`. Root and independent review verified recursive key uniqueness, raw/materialized equality, original driver/supervisor epochs, nonce/image/payload pins and all four stable bounded snapshots. `watchdog-native-diagnostic-v2.json` records the bounded findings, SHA256 `ecc688ff672c858b53b697ccf157882361bb94ac595d326cdee3c9f595838f9d`. Preparation passed 113 pure checks and complete published-payload validation (192 files); these checks do not qualify native CWT, SQL drainage or worker cleanup.


### Fresh WinExe supervisor comparison (2026-10-07)

Native03 changed only the watchdog project to `WinExe`; all C# launch, environment, membership and total-one checks remained unchanged. The independently pinned actual apphost is AMD64/PE32+/Windows subsystem2. The new complete publish passed runtime policy validation (192 files) and 113 pure checks; explicitly redirected WinExe output and original-process join were checked on Windows PowerShell 5.1 before the VM invocation.

The fresh harmless supervisor case reported `SupervisorJoinedOnly`: original driver PID24628 joined0, original supervisor PID11108 exited76 after deadline Job termination, and complete stable snapshots were 0/0,1/1,1/1,1/0 with only PID11108 in both nonempty lists. Any-Job membership was false before assignment. No additional member was observed in this case. Diagnostic qualification remains0; the comparison neither identifies native02 PID6228 nor proves a universal cause/fix, CWT startup containment or recovery readiness.

Report `artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-03/guest-harmless-watchdog-20261007T132548624Z.json` SHA256 `1456e72527f9e0ad11dd349642db9b8889b7f08bf41fec8fe014a6948d5d6d6e`; independently verified record `watchdog-winexe-diagnostic-v3.json` SHA256 `3f32ac9b43bf27248e4d10cd6082a4cfef9d786b2b4959519f84720d27b7cadd`. Earlier refusals remain preserved. No CWT/monitor/SQL/service/rights changes occurred; production and recovery admission remain closed.


### Two-phase external-registration contracts (2026-10-07)

The separate pure registration module now preserves causal creation order: early scope/permit
messages carry no worker PID or birth, but retain the approved image and primary-token SID.
A separately parsed immutable returned-original-handle simulation context completes the worker
epoch after creation; incoming receipts cannot supply their own expected worker. Closed schema2
and producer `RegistrationPureSimulationV2` reject the earlier wire version. Head/full digests,
model-specific creator/issuer/directions, distinct nonces, original handle continuity and worker
birth between creation permission and return are checked. Prior AnyJob=true remains a sticky
refusal, and equal process/Job slots in the same source table are refused.

Root built the exact extracted seven-file v4 freeze with locked restore and repository
analyzers: zero warnings/errors, **286 logical cases / 887 assertions passed**.
These are separate pure checks, not additions to the 833-case tooling suite or native qualification.
Manifest SHA256 `8e8bb82c1a921ea49b6afde372a37b8ebb3e42f1398a64ca9276ccffe95bd72a`; archive SHA256
`57e1f45567a5f8bbb635db2657ad85613229498cf3ea6aea3977df712bdf28b1`. Source bytes remained unchanged after the build.
Root record `artifacts/dashboard-agent-interruption-2026-10-07/registration-managed-validation-v4.json`
SHA256 `881f875ebfb419be5e7c3ec0cb16d172e3fe811eaba089a5ccca62abe7758d93`; independent review is retained separately. Earlier freezes, failed initial
build and v3's 210-case/620-assertion result remain preserved.

No native bootstrap, actual duplicate custody, ACK/resume/recovery authority, SQL drain or
startup-timeout qualification follows from parsed simulation facts; all authority/admission flags
remain false and relaunch remains forbidden. The frozen credentialed factory still uses
CreateProcessWithLogonW with ordinary STARTUPINFO; the reviewed current-API research records
that AsUser/WithToken Job-list proposals need a distinct implementation and actual validation.
Next implement authenticated original-handle bootstrap and bounded credential-free Job custody,
then original-worker custody before resume. Actual two-root interruption/SQL drainage, standalone
recovery, deployed CPU/Agent/dashboard qualification and pre-return containment remain open.
The broad goal remains active; no native, credential, SQL, service or rights change occurred here.


## 2026-10-07: actual authenticated empty-Job bootstrap checkpoint

The isolated Stage A prototype is in
`artifacts/dashboard-agent-interruption-2026-10-07/registration-bootstrap/`.
It uses an explicitly listed, one-shot inherited read endpoint to bootstrap an original
suspended Supervisor, authenticates both original process epochs and pipe peers, and
verifies duplicated custody of a fresh empty Job before releasing duplicates and joining
the original private scope. The Supervisor creates this empty inner Job; this is distinct
from the forthcoming Observer-created WorkerJob custody boundary.

The first native attempt is retired. Its original driver joined, but the old wrapper
retained empty stdout without its exit code; an independently opened protected journal
contains only the initial Prepared checkpoint. Its exact failure cause and scope cleanup
remain unknown. Later success does not resolve this earlier attempt or authorize relaunch.

Two concrete source defects were repaired in a new immutable V4 freeze: the closed refusal
filter explicitly includes InvalidDataException, which is not an IOException subclass;
Job verification enforces exact effective LimitFlags 0x2000 without requiring ignored,
inactive storage to be zero. Every additional active limit/breakaway flag still refuses.
Preparation stages are journaled inside the diagnostic bound try, unknown cleanup remains
unknown, and stream failures preserve independently available bounded output and original
exit status. These defects are not asserted to be the unobserved first attempt's cause.

Root built the exact extracted 18-file V4 freeze with locked restore and repository
analyzers: zero warnings/errors, **304 logical cases / 601 assertions passed**. The fresh
published GUI executable independently passed those checks, validated all **192 files /
80,684,833 bytes** of its runtime payload, and refused malformed read-only input with
original exit 65, exact three-key refusal, 96 stdout bytes and zero stderr. Separate
Windows PowerShell stream-evidence regressions passed **12 cases / 60 assertions** with
zero processes/native cases. These suites are separate from the earlier 286/887 contract
simulation and the 833-case tooling suite; no counts are added to native qualification.

V4 source manifest SHA256 `358936c3b7986db8ec1a6a184feb7725ef4c0594269139d434805b69906a56b6`;
source archive SHA256 `5119ce73779d3718cffa29b0031524adf5f66086100884a4e1bc202d8c23335c`.
The independently reviewed native02 wrapper manifest SHA256 is
`db23bd70dfe056aaf046e922b2502f3cd74925a5ca934d6b1ff10b9d9ded1b03`;
payload manifest SHA256 `a09ae57c60c22f5ab9d1ca8abc1c26441ad2df0a31ddc49e6e87e196f3615d92`.

One separately reviewed finite native02 diagnostic on Server2025Test completed with
`registered_empty_job_only` / `CleanupJoined`: original driver 23424, born
2026-10-07T16:44:02.8425109Z, exited 0; original Supervisor 21672, born
2026-10-07T16:44:04.1017815Z, exited 0. The actual original-thread resume returned prior
count 1, and the outer Job ended with total 1 / active 0. Its 1,785-byte closed raw result
matches the materialized capture. Independently opened final-path and protected-ACL
verification read back the 1,042-byte journal; SHA256
`482ea17f929bd6b62e21bb006fe69ade57adbc96f82d684ff53e525192de7405` matches the native
result and scope/epoch/cleanup fields. Launch nonce
`2acb4fc2-69cd-4655-b910-deeac150d9cf` is retired; submission markers forbid replay.

Root evidence `artifacts/dashboard-agent-interruption-2026-10-07/bootstrap-native02-diagnostic-v1.json`
SHA256 `d31772a929ad85e91f8fccd73eaaf50386a31201da9e5ab6abd330c8df65db4d` retains the raw
report, opened-journal readback, frozen package and review pins. This is one credential-free
empty-Job mechanism observation, with native qualified cases still zero. No worker/CWT,
SQL/SCM, recovery, native-creation timeout or production authority follows from it.

Next work has started in the separate `registration-worker-custody/` prototype: the Observer
creates and retains the WorkerJob and original suspended worker process/thread, installs
only process-query/wait and Job-query/terminate/read-control duplicates into the held original
Supervisor, independently re-pulls them against its originals, and resumes its original worker
thread exactly once after verified custody. The first diagnostic remains credential-free;
the existing credentialed factory/API and account rights are not changed by this checkpoint.
Actual worker custody, pre-return containment, two-root interruption/coordinator SQL drainage,
standalone recovery, genuine attached-invalid signature, deployed CPU/Agent/AG/dashboard
qualification and final acceptance remain open. The broad goal remains active.


## 2026-10-07: actual partial Observer-owned worker custody checkpoint

The separate credential-free prototype is in
`artifacts/dashboard-agent-interruption-2026-10-07/registration-worker-custody/`.
The Observer reserves the immutable custody head before creation, retains its original
suspended worker process/thread and separate fresh WorkerJob, and installs only noninheritable
process query/wait and Job query/terminate/read-control duplicates into the original Supervisor.
It authenticates the custody ACK, re-pulls the installed slots against its held originals,
persists an acknowledged verified checkpoint and attempts its own original worker resume once.
No thread is transferred. The harmless GUI worker waits 250ms and naturally exits.

The V1 syntax-error build and V2 analyzer-error build remain preserved. The exact extracted
22-file V3 freeze passed locked restore and repository analyzers with zero warnings/errors.
Actual bootstrap checks passed **304 logical cases / 601 assertions**; independent worker
checks passed **316 cases / 474 assertions**. The earlier 574 assertion estimate was a manual
counting error, not an actual result. The fresh GUI publish independently matched both suites,
validated **192 files / 80,849,727 bytes**, and refused malformed read-only input with original
exit 65, exact three-key refusal and zero stderr. Windows PowerShell wrapper checks passed
133 cases / 141 assertions and stream-evidence checks passed 12 / 60, with no native workers.
These separate pure suites do not add to native qualification.

V3 source manifest SHA256 `f8fcf14028845abbb0adfa3ba22a749e7a3ddbb68b956848658e81cf9cb37056`;
archive SHA256 `c5d49a71f042c5924bd733d40b38f64af43bcadc7a2b178e744990d5f3779d21`.
The reviewed 13-file wrapper manifest SHA256 is
`ce4a61ed6d303756acc8c2e77c89102742ff20c6449daf342a17555f78ec0bdd`;
payload manifest SHA256 `748e46fa55d3fa3756d1c197596cc3bbff866081f2330e81db9c92a0709d37a5`.
The submission entry point durably reserves its one attempt before reading secure credentials;
both prior reports and reservations prohibit replay. Optimized Python explicitly refuses.

One separately reviewed finite native attempt on Server2025Test reached verified custody,
checkpoint persistence and original worker resume with prior count 1. Original driver 23428,
born 2026-10-07T17:45:03.9000666Z, exited 94; original Supervisor 24828, born
2026-10-07T17:45:04.8706888Z, exited 87; original worker 11656, born
2026-10-07T17:45:05.7424290Z, naturally exited 0. Both separately assigned private Jobs ended
with total 1 / active 0, and both held original process exits and scoped cleanup were confirmed.

Custody release was not acknowledged. The closed 58-key, 2,557-byte result reports
`OriginalWorkerJoined` / `native_refused`, with `custodyReleased=false` and
`cleanupConfirmed=true`. This is a partial mechanism observation, not a completed worker
custody diagnostic. The exact Supervisor release-refusal cause remains unknown; the last
Observer stage and internal exit 87 do not identify the failing Supervisor operation.

Independent opened-final-path and protected-ACL inspection read back the 3,118-byte journal;
SHA256 `b94bb2b1a72cad0ca4ca15b9df748544f48f92726bd134750d70c399d39ad2bf` matches the native
result and both scope-bound process/job ledgers. Immutable custody-head SHA256 is
`e03a1c3e751cc5558fd9ef2cc832abca99455103f58256c2c31baa0b0dac4b19`.
Launch nonce `a35b83d8-a2ce-4e57-9f14-c6d92c0468f8` is retired; no replay is authorized.

Root record `artifacts/dashboard-agent-interruption-2026-10-07/worker-custody-native01-diagnostic-v1.json`
SHA256 `800b046fd0fc7f77a0885cf4b6aad4f45bf31edf0ec0512f1133ef91b781b91a` retains the raw
capture, opened-journal readback and exact source/package/review/submission pins. Qualified
native cases remain zero; no monitor account, CWT, SQL, SCM, recovery, hard creation timeout
or production authority follows. Historical unknown scopes and their retired nonces remain
unchanged. Next work must locate the release refusal using explicit operation evidence in
a new candidate, preserving existing gates. Credentialed custody additionally requires distinct
ordinary monitor token/profile/image/environment bindings; Administrator assumptions cannot be
reused for SqlObserverCollector. Full two-root interruption/coordinator SQL drain, standalone
recovery, attached-invalid signature, deployed CPU/Agent/AG/dashboard qualification and final
acceptance remain open. The broad goal remains active.


### Terminal exit provenance remains unknown

The observed Supervisor exit 87 can also be produced by the Observer's existing
`TerminateOwned` cleanup (`TerminateJobObject(..., 87)`). The partial result does not record
whether that call was attempted or accepted. References above to a release refusal describe
the incomplete protocol; they do not establish that the Supervisor entered its internal
refusal handler. Both the failing operation and the exit's producer remain unknown.
The new `registration-worker-observation/` candidate is adding authenticated terminal-operation
evidence and explicit parent termination-attempt/result observations while retaining every
existing custody, identity, Job and cleanup guard. No new native attempt has occurred here.


### Authenticated post-exit image query refusal (2026-10-07)

One distinct credential-free `registration-worker-observation/` attempt recorded an
authenticated Supervisor terminal refusal at `EpochQueryFullProcessImageName`, category
`win32`, native error 31, after the original worker exited naturally with code 0.
The driver returned 94, Supervisor exit was 87, and custody release remained false.
Both independently held Job ledgers ended at total 1 / active 0 with original exits and
scoped cleanup confirmed. The original worker resumed once with prior count 1, after
verified custody and a persisted checkpoint. The protected journal was independently
opened with its final path and ACL verified; its 4,111 UTF-8 bytes hash to
`99a8ab8fc7027168344d453e854c11e8635ddd6f7ccc3b1bb92732ddad0fa0e4`.
Its terminal frame, process epochs and separate Job ledgers match the captured result.

The Observer recorded Supervisor-job termination attempted and accepted as true; these
report the native call outcome, not the producer of exit 87. That producer remains unknown.
The earlier Stage B refusal cause also remains unknown. This new attempt did not reach
the post-exit token or Job membership checks, so neither is inferred to pass or fail.
Nonce `e65b14a4-712f-4bf6-bfca-f7af52f1dfd8` is retired and must not be replayed.
Actual result: `artifacts/dashboard-agent-interruption-2026-10-07/worker-observation-native01-diagnostic-v1.json`
(SHA-256 `fb0fe4061eb16064ca72f3e58a9a6341595b179d17b63b12411d72fb425d3eb4`).

Preparation now targets a native lease captured while the original worker is alive,
retaining verified process and token handles, followed by same-kernel-object, PID,
creation-time, signal and exit-code validation after join. Historical live token evidence
must not be promoted into current token authority. All live pre-resume checks, Job
membership, ACL, flags, complete accounting/member lists and deadlines remain required.
The new identity candidate has not yet been built or run. Completed custody diagnostics
and qualified native cases remain zero; no monitor, credentialed worker, SQL or SCM action,
recovery permission or production admission follows from this observation. The goal is active.


### Retained worker identity and custody joined (2026-10-07)

The distinct `registration-worker-identity/` candidate completed one harmless native
custody diagnostic. Driver 25200, Supervisor 22992 and original worker 8224 all exited
with code 0. The result reached `CleanupJoined` / `observer_worker_custody_joined`,
with custody verified, checkpoint persisted, original resume prior counts 1,
custody released, original exits confirmed and both Job ledgers at total 1 / active 0.
Neither owned Job termination was attempted. The unchanged post-exit Job membership,
ACL, exact flags, stable accounting and complete member-list guards passed in this case.

The private native identity lease captures and retains verified live process/token
handles before ACK4. After join it validates the same held kernel process object,
PID, raw creation/exit FILETIME, signal and exit code; it does not requery an exited
process's image or token. Existing live pre-resume checks and Job guards remain intact.
Historical token evidence grants no present token authority or privilege-stability claim.
Fresh source/archive/extracted verification covered 27 files; locked restore and build
passed with zero warnings/errors. DLL and published GUI suites agreed at 304/601,
316/474, 152/168 and 89/105 cases/assertions. Wrapper checks passed 307/415 and stream
checks 12/60. A copied stale-pin stream refusal remains preserved separately; after
alignment with the new frozen sources its rerun passed. These are pure-check counts.

The independent protected-journal inspection verified final path, protected ACL and
all 3,361 UTF-8 bytes (SHA-256 `e6ac2579aef5dc1962ad514f7a86ec4f5cb5fb31d786d8bd9e8a835b8ade47fc`).
Its epochs, custody head, separate ledgers and no-termination observations match the
duplicate-free 64-key / 2,800-byte raw result. Evidence record:
`artifacts/dashboard-agent-interruption-2026-10-07/worker-identity-native01-diagnostic-v1.json`
(SHA-256 `7b7e9ac8775d928f65a7f20589478005c70e0199aee7c876e34fca916fe02be1`).
Nonce `ce20c077-f5ec-4329-bcf6-9884bdf338cd` is retired and must not be replayed.

Completed harmless custody diagnostics are now one; qualified native cases remain zero.
The wrapper's broad success/admission flags deliberately remain false. No monitor-account,
CWT, SQL or SCM action occurred. The synchronous credentialed-creation timeout, real
DBA/monitor two-root cleanup and both SQL-drain gates remain unqualified. Next preparation
is a distinct harmless monitor-account candidate with separate worker SID/profile/image,
payload/environment pins and memory-only credential transport. No rights fallback follows
from this success, and prior refusal causes/exit provenance retain their recorded limits.
The broader goal remains active.


### Monitor credentialed creation refused; provenance remains limited (2026-10-07)

The distinct Monitor preparation passed locked restore/build with zero warnings/errors.
DLL and published GUI suites agreed at 304/601, 316/474, 152/168, 89/105 and 333/508
cases/assertions. The frozen wrapper passed 392/544 diagnostic and 12/60 stream cases.
Four actual original-stdin cases (empty, partial header, partial secret, withheld EOF)
refused with exit 65 after 10,088 / 10,087 / 10,160 / 10,151 ms while stdin remained
open. These checks establish those input observations, not CWT startup containment.
The repaired decoder polls the original synchronous byte pipe under its deadline;
it does not rely on cancellation interrupting an already blocked console read.
Earlier source, failed analyzer build, helper refusal and review records are preserved.

One separately reviewed guarded submission reserved its attempt before secure inputs.
Driver 8912 joined with exit 94 and emitted a duplicate-free 70-key / 2,873-byte
MonitorAccountCustodyDiagnostic. It reached CreatingOriginalWorker / native_refused.
CWT entered and returned from 20:38:17.5363508Z to 20:38:17.5492026Z; no original
Monitor process handle or epoch was reported. Monitor launch remains null/unknown,
not an absence claim. No custody offer, verified checkpoint or Monitor resume occurred.
The Monitor exit and worker Job total/active observations remain null; cleanup is false.
Supervisor 25128 joined with exit 87 and its Job ledger was total 1 / active 0.
Its Job termination was attempted and accepted, which does not identify the producer
of exit 87. No authenticated Supervisor terminal diagnostic was observed.

The native CWT return Boolean, failed-call error and observer exception provenance
were not persisted by this candidate. The cause remains unknown. Independent current
artifact inspection is retained in the evidence record. A separate read-only query
found no Security 4625/4648 events in the call window expanded by two seconds; that
does not prove credential acceptance or process absence. A final journal snapshot
and supplemental events cannot reconstruct unrecorded syscall error provenance.
The wrapper skipped explicit remote-session removal but helper exit may release it;
durable retention of those remote leases is not proven. No missing proof is repaired
by replaying, reopening a PID, relabeling the Supervisor or adopting a process.

Evidence: artifacts/dashboard-agent-interruption-2026-10-07/monitor-custody-native01-diagnostic-v1.json
(SHA-256 8cbb7c8b3482df2365a8329fcc6f91fc65a1534c7feeacb4ccc304945b4d9ece), independently reviewed at b010671b3c5f0b05fc911c6f939a92533440f049aba56da87dbd5b72e4b919d2.
Nonce b982e80c-6d86-469a-b285-af3b7ff44524 is retired and must not be replayed.
One Monitor CWT attempt entered/returned; qualified native Monitor cases remain zero.
SQL/SCM, recovery and production admission stayed false. Existing account rights and
ancestor ACLs were unchanged; only fresh protected payload scope files were created.
Next work is managed preparation in a fresh observer-provenance source clone to retain
the actual CWT return/error and typed private observer failure without secret text.
No second native launch is implied. CWT hard timeout containment, original two-root
Coordinator DBA/Monitor cleanup and both SQL drains remain unqualified. Goal active.


### Monitor creation error captured without changing account rights (2026-10-07)

The distinct observer-provenance source retains the actual credentialed-creation
Boolean and failed-call error before further interop. Its private journal has a new
schema4/kind and 38 keys; the public diagnostic remains schema3 with 70 keys.
The 44-file source passed locked restore/build with zero warnings/errors. DLL and
published GUI suites agreed at 304/601, 316/474, 152/168, 89/105 and 443/657
cases/assertions. These are managed checks; the published payload contains 192 files.

One guarded native attempt entered and returned CWT. Driver3184 joined with exit94;
Supervisor9868 joined with exit87. The raw 70-key result contains 2,872 UTF-8 bytes.
Independent protected readback verified the 5,085-byte private journal, public pin,
canonical custody-head/scope hashes and all192 runtime files with held-path/ACL checks.
The actual syscall Boolean is false and its immediately cached failed-call error is
1385. The bound first Observer failure records NativeCwtCall / win32 / nativeError1385.
[Microsoft defines 1385 as ERROR_LOGON_TYPE_NOT_GRANTED](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-).
[Ordinary CreateProcessWithLogonW requires local interactive logon permission](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithlogonw).
The effective policy causing this refusal has not yet been independently inspected.

Evidence: artifacts/dashboard-agent-interruption-2026-10-07/monitor-observer-provenance-native01-diagnostic-v1.json
(SHA-256 9591ffa9db06df0f1fb8c3f0b44e627248ca7ac6518ef955c4396e4a493c5d62),
independently reviewed at 873a3200d9d55ce5c2e5e82e65f9ae39f6d0700ecd21cc13e79334f8b5106a1e.
Nonce19f82411-28fc-4120-8faa-4ad8961b1197 is retired. Monitor launch remains null,
cleanup remains false, worker identity/counts are null, and the producer of Supervisor87
remains unknown. This does not establish process absence, password correctness or cleanup.
The earlier b982e80c-6d86-469a-b285-af3b7ff44524 case retains its unknown syscall cause.

The preparer's outer UTF-8 BOM refusal and the first remote read-only validation refusal
are preserved. A local Windows PowerShell probe reproduced the epoch clock rejection:
an untyped format array selects the wrong TryParseExact overload. Explicit string[]
formats passed both real public epochs without changing format/UTC requirements. A fresh
reviewed reader then completed the protected readback; the old remote failure site itself
was not recorded. No failure, source freeze or used wrapper was rewritten or replayed.

The initial installer explicitly configured the Collector for service-only logon and
denied interactive/remote-interactive logon. That historical intent is distinct from
current LSA policy evidence. Next work reads the exact current allow/deny assignments;
any service-compatible launch design needs separate preparation, review and qualification.
Existing rights and ancestor ACLs remain unchanged. There are now two entered/returned
Monitor CWT attempts and zero qualified native Monitor cases. SQL/SCM actions, recovery
and production admission stay false. CWT startup hard containment, original DBA/Monitor
two-root cleanup and both SQL drains remain unqualified. The broader goal is active.
