# Finite SQL Agent signed-module trial

This Windows/.NET 10 library provides `TrialCoordinator.PreviewAsync`, `RunAsync`,
`RunSignatureRemovalAsync`, `RunExplicitDenyAsync` and `RecoverAsync`.
The caller supplies one dedicated nonpooled SQL17 sysadmin connection using the enrolled TCP endpoint,
integrated authentication, validated TLS, the certificate host name, `master`, and no transparent
reconnection. The actual collector Windows account remains a separate, nonsysadmin identity.

`PreviewAsync` reads the existing login/user mapping and catalog baseline in a rolled-back transaction.
It refuses existing trial objects, incomplete metadata visibility, unexpected platform/build, and
offline databases. No trial object or permission is created by preview.

`RunAsync` creates seven fixed objects: an unprivileged schema owner, its schema, the pinned
procedure, master certificate and certificate login, and matching public certificate and user in
msdb. If the existing Windows login has no master user, the trial also creates one temporary master
user for that exact login and records its new ID. An existing user is preserved; same-name or SID
conflicts are refused. The signer receives `VIEW SERVER SECURITY STATE`, msdb `CONNECT`, and `SELECT` on syssessions
and sysjobactivity. The existing monitor receives only module `EXECUTE`. Private signing material
is removed before the installed snapshot is accepted. Five grants are explicitly issued. A newly
created monitor user's zero or one SQL-created CONNECT grant is captured exactly; no separate
CONNECT grant is issued to that user. No existing service is restarted or admitted.
The newly created certificate login's automatic CONNECT SQL and the newly created WITHOUT LOGIN
schema owner's CONNECT are revoked before verification; neither is needed by these helper principals.
Existing principals are never targeted by those REVOKEs. The exact five-grant check remains strict.
Microsoft documents automatic CONNECT SQL on [login creation](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-login-transact-sql?view=sql-server-ver17).

The protected receipt at `%ProgramData%\SqlObserverAgentActivityTrials\<deployment-guid>\receipt.json`
records the original instance/database GUIDs, monitor SID/mappings/permissions and exact installed
object IDs, owners, certificate/signature evidence, and grants. Only Administrators and SYSTEM have
access. No password is recorded. Prepared and verified postimage records are flushed before commit.

After a committed-state check, the trusted callback runs bounded tests as the real monitor. The
callback must join and clean up its process before returning, including on cancellation. Cleanup
has an independent deadline and recaptures the exact postimage before removal, then verifies the
original baseline after commit, including absence of the temporary monitor user. The user's roles,
ownership, dependencies and exact grants must match before its removal. An uncertain SQL session is closed. If the session is lost or an
object changes, cleanup refuses and preserves the receipt for explicit `RecoverAsync` on a new
reviewed connection. Recovery never adopts objects without an installed postimage.

Catalog checks include static dependencies, duplicate certificates/mappings, roles, ownership and
signatures. They cannot prove absence of arbitrary dynamic SQL consumers. The advisory lock does
not prevent an unrelated sysadmin from editing these objects; qualification requires a controlled
DBA maintenance window. The [2026-10-05 native trial](../../../../artifacts/dashboard-agent-module-trial-2026-10-05/completion.json)
passed all three cases under the actual monitor on SQL17, including signed cross-database reads,
production parsing of 12 rows, and coordinator-verified installation and normal removal restoring
the original baseline and absent master mapping. Direct syssessions SELECT still failed with 229.
Agent was stopped: running-session association and full job visibility remain unqualified.
The protected guest journal was not copied locally; cleanup evidence is the coordinator's live
post-commit baseline verification and returned result. That run did not exercise process-exit recovery.

Pure validation tests are in `sql-tests/` and `../trial-tests/`. The native test class is
`SqlAgentActivitySignedModuleQualificationTests`; it is excluded from ordinary SQL sweeps. Its
three tests cover production parsing under the actual monitor, invalid/NULL bound refusal and
pre-cancelled generic source opening. The last case does not qualify a production module adapter.
The enrolled definition hash alone is not proof of DBA verification. Final managed groups pass
60 pure SQL-plan, 98 journal/guard/privacy, 99 module-contract and 74 source/parser cases;
these separate groups are not an aggregate unique-test count.

This library is qualification tooling. Production module discovery, adapter admission, Agent
session/running-job behavior, explicit DENY and broader foreign-owner visibility remain separate
work. Later signature-removal and controlled process-exit evidence is recorded below.

## Independent visibility witness

`TrialVisibilityWitness.CaptureAsync` reads the latest Agent session and bounded activity rows as
the DBA before and after the actual monitor's trial. It uses the same enrolled nonpooled SQL17
connection, verifies the original monitor login/SID and instance identity, and reads all eight
source fields plus whether the recorded owner SID differs from the monitor SID. No job name or
owner SID is projected. Missing owners, empty/no-foreign-owner fixtures and a 513th row refuse
this comparison. The existing monitor permissions and jobs are unchanged.

`TrialVisibilityReceipt.ReadTrx` accepts only the exact three successful native cases, a unique
receipt from the capture case, the expected target/revision, and closed bounded JSON with no
duplicate properties. Its digest covers the exact parsed bytes. The caller must supply the fresh
worker-owned file and DBA SQL-clock interval. The complete ordered monitor rows must equal both
DBA observations, with matching session details and the monitor capture inside their brackets.
Only counts and booleans are returned in the comparison summary.

This establishes agreement for the observed latest-session row set, including observed rows with
a differing owner SID. It cannot prove continuous stability between observations, unchanged exact
owner identities, another human's identity, universal foreign-job visibility, running association,
or elapsed duration. The 512+lookahead/load gate remains separate. The full affected tooling suite
passes 208 cases, including the previous 98 journal/guard/privacy cases, 44 witness cases and 66
receipt cases. Reading the prior Run08 TRX confirms format compatibility only.

The [fresh Run09 comparison](../../../../artifacts/dashboard-agent-module-trial-2026-10-05/visibility-completion.json)
passed on 2026-10-05: all 12 monitor rows matched both DBA observations, including 12 whose owner
SID differed from the monitor SID. The three actual-monitor tests passed with zero skips. The
coordinator verified original-baseline restoration and the temporary service was deleted. Raw
DBA snapshots were compared in memory; local evidence contains the safe comparison result, exact
parsed-TRX digest and reviewed implementation, not an independent copy of those snapshots.

## Signature-removal qualification

`TrialCoordinator.RunSignatureRemovalAsync` uses journal schema 2. It first performs the signed
qualification, recaptures the exact signed state, then removes only the fixed module signature
inside a transaction. The unsigned postimage may differ only by removal of that signature fact
and its validity changing from `1:1` to `0:0`; definition, identities, owners, permissions and all
other evidence remain identical. The [signature-status function](https://learn.microsoft.com/en-us/sql/relational-databases/system-functions/sys-fn-check-object-signatures-transact-sql?view=sql-server-ver17)
reports `0:0` for an unsigned object.

The journal stores both immutable postimages in `signature_remove_pending` before commit. After
commit and a fresh exact unsigned-state check, the second bounded actual-monitor callback runs.
Recovery accepts either recorded image only during the uncertain-commit phase; confirmed unsigned
state permits only unsigned cleanup. `unsigned_remove_pending` records that decision before the
normal owned-object removal and original-baseline verification. Existing schema 1 remains signed
only, with its previous wire shape; no journal is silently upgraded. A signed trial callback may
not change a signature on its own or bypass drift checks.

The separate `RequiresSqlAgentActivityUnsignedModule` category requires explicit unsigned opt-in
plus the original pinned enrollment. Its two native cases require canonical-bound execution to
produce only SQL 229, invalid bounds to produce 50001, and unchanged monitor identity/direct
permissions before and after. Any partial result, unexpected error or additional result set fails.
The runner retains both phase results on failure and reports unknown execution counts as unknown.
Ordinary SQL sweeps exclude both explicit native categories.

The affected tooling suite passes 303 cases; the selected signed/unsigned enrollment group passes
22, and the original pure SQL-plan suite passes 60. These groups are separate evidence, not a
unique-test aggregate. This implementation does not itself establish native crash/uncertain-commit
recovery, explicit DENY behavior or execution with an attached invalid signature. `ALTER` removes
signatures, so it cannot demonstrate that last case.

The [Run10 native evidence](../../../../artifacts/dashboard-agent-module-trial-2026-10-05/signature-completion.json)
passes all five cases: three signed and two unsigned. Signed reads again matched all 12 observed
rows to both DBA observations. After committed signature removal, the monitor received only 229
for the canonical bound and 50001 for NULL/512/514. Its direct permission vector remained 0/0/1
and direct syssessions SELECT remained denied before and after both unsigned cases. Both temporary
services were deleted and the coordinator verified restoration of the original SQL baseline.
The 2,540 packaged source inputs and four runner inputs were unchanged before/after. The protected
journal remains on the guest; that run qualified normal unsigned-state removal and did not exercise
process-exit recovery.

## Controlled exit after committed signature removal

The [Run11 recovery evidence](../../../../artifacts/dashboard-agent-module-trial-2026-10-05/recovery-completion.json)
records three signed native tests passing with zero skips. The first runner deliberately exited
with code 73 after an acknowledged signature-removal commit and an exact unsigned-state check.
The signed worker had already exited and its temporary service deletion was verified. A separate
runner process with a fresh DBA connection then used the same deployment receipt to verify the
recorded unsigned state, remove the owned objects and restore the original baseline. Recovery
exited with code 0; the journal advanced from `unsigned` to `removed` while preserving its immutable
baseline, signed and unsigned images.

Recovery executed zero native tests. Its result intentionally reports `QualificationPassed=false`,
`CleanupVerified=true` and `qualification_not_replayed`. The parent harness separately qualifies
the controlled exit/recovery scenario using the exact three-test receipt, checkpoint, process
identities and recovery evidence. This run contains no unsigned native-test phase.

All 2,540 packaged source inputs and four runner inputs were unchanged before and after execution.
The final snapshot at 2026-10-05 09:41:10.968 UTC found no qualification processes or services,
all five existing core services running and SQL Server Agent stopped. This is a final observation,
not proof about every historical descendant. The protected guest journal was not copied locally.

This qualifies controlled process exit after an acknowledged commit and completed worker cleanup.
Uncertain COMMIT, power loss, interruption during SQL work or journal writes, in-flight cancellation,
orphaned workers, explicit DENY, attached invalid signatures and production admission remain open.

## Metadata-verification policy implementation

`TrialCoordinator.RunMetadataVerificationAsync` uses immutable journal schema 3 and
`MetadataVerificationV2` installation policy 2. The [versioned policy](installation-policy.v2.json)
adds only monitor `VIEW DEFINITION` on the fixed module and master certificate: seven explicit
grants in total. Existing schema 1/2 receipts and omitted policy arguments retain the exact
five-grant `ExecuteOnlyV1` contract. No receipt is upgraded or policy inferred from live grants.
Recovery derives its policy from the validated receipt and requires its exact signed or unsigned
postimage before removal. The optional temporary monitor user's captured SQL-created CONNECT
variant remains separate from the explicit grant count.

Policy 2 explicitly issues module `EXECUTE` and `VIEW DEFINITION` as
`SqlObserverAgentActivitySchemaOwnerV1`, the module's independently validated owner. The certificate
`VIEW DEFINITION` grant is explicitly issued as `dbo`. Validation and cleanup require these exact
grantors; they do not accept either principal interchangeably. Legacy SQL and validation behavior
remain unchanged.

The pinned [metadata probe](../verify-metadata-v2.sql) is a separate bounded read. The finite
qualification opens the actual monitor's connection through the production integrated connection
factory, checks the trusted DBA-installed definition, public certificate and signature before
constructing or sending capture EXEC, then repeats the metadata check after the production parser
finishes and before accepting its capture. Both checks and capture use the same owned SQL session;
monitor identity and direct permissions are also checked before/after. This is test-only wiring,
with no public production module adapter or admission change.

The unsigned case must observe the missing signature and reject capture eligibility. A separate
negative execution experiment then deliberately bypasses that eligibility check to require only
SQL 229, with no source result sets or accepted capture. It does not qualify an attached invalid
signature or explicit DENY. Equal metadata observations are checks at two points in time; they
cannot exclude a privileged change-and-restore between them.

Managed validation passes 394 affected tooling cases, 132 selected SQL metadata/contract cases
and 60 pure SQL-plan cases. These are separate groups, not an aggregate unique-test count. The
two native cases use the explicit `RequiresSqlAgentActivityMetadataModule` category, excluded
from ordinary sweeps. Managed results and the native evidence below are separate qualification layers.

Run12 is retained as a failed attempt: installation validation refused the postimage before any
native tests executed, and cleanup verified restoration of the original baseline. The new validator
had incorrectly required `dbo` as the module grantor. A read-only inspection of the protected,
retained Run10 receipt confirmed that the actual module `EXECUTE` grantor was the recorded schema
owner. The correction makes policy-2 grantors explicit and tests rejection of `dbo` for module
grants and the schema owner for the certificate grant. The fresh Run13 result is recorded below.

The [Run13 metadata qualification](../../../../artifacts/dashboard-agent-module-trial-2026-10-05/metadata-completion.json)
passes both native cases with zero skips. The signed case verified the enrolled definition,
public certificate, cryptographic signature validity and actual-monitor identity before/after
capture on the same owned session. After removal of the signature, metadata remained readable,
capture eligibility was false and the separate negative execution returned only SQL 229 with no
source result sets. Direct permissions remained unchanged. The runner exited 0 and normal cleanup
restored the original baseline; fallback recovery was not needed.

All 2,547 packaged source inputs and four runner inputs were unchanged before/after. The final
snapshot at 2026-10-05 10:13:43.7300142 UTC found no qualification processes/services, all five core
services running and Agent stopped; it does not prove absence of every historical descendant.
The two independently checked TRX digests and preserved Run12 failure are bound in the completion
record. This qualifies the finite schema-3/policy-2 metadata experiment, not continuous stability,
attached invalid signatures, explicit DENY, running-job association or elapsed time.

The next step is an explicit module adapter with trusted, versioned, revocable enrollment before
retention, API or UI work. No public production adapter or admission is established by this trial.

## Finite explicit-DENY fixture — 2026-10-07

`RunExplicitDenyAsync` adds journal schema4 with installation policy2. Its three callbacks qualify
signed capture, effective denial and restored signed capture. The immutable installed snapshot is
preserved; a separate denied snapshot may differ only in the sole dbo-granted package certificate
user's `SELECT ON msdb.dbo.syssessions` state, `G` to `D`. Monitor permissions, signature validity,
definition, identities and every other grant remain exact. Fixed guarded DDL runs in the caller's
owned transaction. The DENY is a finite failure fixture; it is not a production permission design.
Microsoft's [module-signing guidance](https://learn.microsoft.com/en-us/sql/t-sql/statements/add-signature-transact-sql?view=sql-server-ver17)
describes certificates as an additive permission mechanism.

Write-ahead `deny_pending` and `deny_restore_pending` retain both complete postimages before
commit. Recovery permits either exact image only in those uncertain-commit phases; confirmed
phases require their recorded image. It restores the grant before ordinary package removal and
verifies the original baseline. Foreign drift and unrelated changes refuse recovery. Existing
schemas1–3 retain their prior contracts; schema4 does not permit an unsigned image.

The [Run16 qualification](../../../../artifacts/dashboard-agent-deny-connected-2026-10-07/native-qualification-v1.json)
passes four actual-monitor cases, zero skips: signed public collector capture, public reader
refusal plus independent canonical SQL229 after DENY, NULL/512/514 bound refusal with50001,
and restored public collector capture. Two private receipts preserve the complete bounded source
capture and original enrollment digest. Agent stays stopped; running/session/time remains open.
The coordinator verifies original-baseline restoration and all temporary service deletions.

The reviewed Run16 wrapper closes and disposes the dedicated DBA session before a callback exits
if an authenticated worker receipt, child PID/start and exit, or service stop/deletion is unconfirmed.
That fences coordinator cleanup and preserves the journal. Its blind standalone/fallback recovery
is disabled until a reviewed private worker-termination audit exists. Normal successful cleanup
remains qualified; actual interruption and universal descendant stability remain unqualified.
Local evidence includes554 trial contracts,211 SQL gate/receipt contracts and52 synthetic wrapper
fence decisions. The synthetic fence cases use initially closed connections and do not certify
open DBA teardown or native termination. Attached-invalid-signature construction remains open.

## Running-job fixture preparation — 2026-10-07

`RunMetadataVerificationSignedAsync` adds a signed-only policy-2 entry point using the existing
schema-3 module journal. The separate `TrialRunningJobWitness` and `TrialRunningJobJournal`
prepare one owned, unscheduled LOCAL TSQL job and database. Their private inventory binds the
same DBA connection/SPID, object GUIDs, owner SID, exact definitions, foreign jobs and permissions.
Every uncertain create/start/delete/drop action remains recorded; pending actions cannot authorize
retries. The protected journal permits only its individual owner SID and SYSTEM.

The step writes a fresh nonce, job UUID, SPID and three independently sampled native clocks,
polls an owned release flag for at most 1,200 iterations, and records completion. These clocks
do not establish whole-job start or elapsed time. Running/completed source comparison requires
the independent execution witness and the existing full-row DBA bracket. Release requires
confirmed monitor termination. Deletion refuses unfinished requested, queued or started activity;
database drop requires exact ownership and no remaining database sessions or requests. The
coordinator must separately prove its held Agent process epoch before stopping that service.

The portable trial suite passes **578** cases with zero skips. The new monitor receipt/input
contracts pass **55**, alongside **76** existing repository contracts. Two opt-in native facts
are prepared for running and completed candidates; neither has executed. A separate ignored
SCM/process controller compiles, but its native start/stop and coordinator integration remain open.

The successful read-only [guest baseline](../../../../artifacts/dashboard-agent-running-job-2026-10-07/baseline-probe-03/guest-baseline-20261007T084743815Z.json)
finds 12 existing jobs, 11 enabled, no unfinished latest-session activity, and Agent stopped with
SCM PID zero. The SQL DMV reports stopped status with a null PID. Existing jobs prevent the
controlled trial: the fixture requires all foreign jobs disabled. The [temporary isolation proposal](../../../../artifacts/dashboard-agent-running-job-2026-10-07/job-isolation-proposal-v1.json)
would change exactly those enabled flags and restore them after verified Agent stop, retaining
supported metadata/history residue. It remains a proposed scope expansion. No job, database or
Agent-state mutation occurred in this preparation checkpoint.

Public admission and service/session association remain `false`, qualification remains `Absent`,
and elapsed/UTC job-start/long-running fields remain null. This preparation does not qualify
native execution, cleanup, restart, interruption, persistent provisioning or continuous deployment.

## Running-job coordinator checkpoint — 2026-10-07

The later [completion-v2 checkpoint](../../../../artifacts/dashboard-agent-running-job-2026-10-07/completion-v2.json)
and [source-readiness-v2 freeze](../../../../artifacts/dashboard-agent-running-job-2026-10-07/source-readiness-v2.json)
record **313** passing managed receipt, coordinator, worker-boundary and monitor-worker contracts,
zero failures/skips, and a coordinator build with zero warnings/errors. These contracts prepare
the bounded actual-monitor protocol; **zero native running/completed cases have executed**.

The more complete [Probe05 baseline](../../../../artifacts/dashboard-agent-running-job-2026-10-07/baseline-probe-05/guest-baseline-20261007T093858172Z.json)
finds three all-history unfinished activity rows in Agent session2, older than latest session4,
in addition to the eleven enabled foreign jobs. Two rows have execution starts and one is
requested without a start; all have NULL stop and history IDs. Their older session establishes
retention, not completion or cancellation. Both existing refusal gates remain enforced even
though the latest-session unfinished count is zero and Agent is stopped with SCM PID zero.
The earlier enabled-flag proposal does not resolve the retained-row gate. No original job flags,
jobs or msdb history were disabled, changed or deleted.

The [clean-lab plan v2](../../../../artifacts/dashboard-agent-running-job-2026-10-07/clean-lab-plan-v2.json)
preserves the current instance and proposes a separate clean SQL17 default-instance destination.
Destination and installation scope remain pending. Current SCM/coordinator contracts support
the default instance only; a named instance requires separate implementation and review. Fresh
physical database GUIDs, TLS/configuration, integrated monitor SID, deployed selected-target and
CPU host bindings, target revision, original enrollment authority and owned repository archive
must be established for that destination. A finite result there would bind the fresh lab, not
qualify the original selected target or continuous deployment. Public admission/association
remain false, qualification `Absent`, and derived execution/time fields null.

## Rollback-only signature construction preparation — 2026-10-07

`TrialAttachedInvalidSignatureSql` prepares genuine signed target/donor provenance and validates
an attached-invalid image against exact target metadata and donor bytes. Its caller owns the
transaction. `TrialSignatureConstructionJournal` is a separate rollback-only journal with
individual-owner-SID/SYSTEM protection, fresh fixture/deployment identities, the exact physical
DBA session and baseline digests, and immutable captured images. Pending or blocked evidence
never authorizes replay, reconnect, commit, worker execution or automatic recovery. Terminal
`rollback_verified` requires acknowledged construction and readback rollbacks, an exact absent
baseline readback on the same session, and independently unchanged OS/foreign inventories.

The [managed signature tooling result](../../../../artifacts/dashboard-agent-attached-invalid-2026-10-07/managed/trial-signature-contracts-attempt1.trx)
passes **709** cases with zero failures/skips. This is the expanded whole tooling suite: the
previous **578** cases plus **131** new signature/journal contracts, not separate totals to add
again. The **313** running coordinator contracts are a separate owning group.

The [Probe06 native construction report](../../../../artifacts/dashboard-agent-attached-invalid-2026-10-07/construction-probe-06/guest-signature-construction-20261007T111503590Z.json)
and [completion record](../../../../artifacts/dashboard-agent-attached-invalid-2026-10-07/completion-v1.json)
record a bounded transactional experiment on SQL Server build 17.0.1000.7. It performed module mutations
inside the caller's rollback-only transaction; it was not a read-only probe. The signed image
and genuine target/donor signatures were verified, with distinct 392-byte signatures. SQL
`ADD SIGNATURE` refused importing the donor signature with error 15299, state 1, producing
`UnsupportedDonorSignatureImport` before an attached-invalid postimage was established. This
demonstrates refusal of that construction candidate on the observed build, not a universal claim
about invalid-signature construction or SQL Server support.

Construction and readback rollbacks were acknowledged; exact absent-module baseline readback,
unchanged independent foreign inventory and held stopped-Agent configuration were verified.
The journal reached `rollback_verified`, cleanup passed, and the exact probe process exited 0
and was joined. The first five failed attempts are preserved and failed before module mutations.
Construction feasibility and attached-invalid qualification remain false. No monitor launched,
repository was created or native collector test executed. This result establishes no native
invalid-signature monitor behavior, committed-invalid recovery or production admission; public
admission/association remain false with qualification `Absent`.


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
[frozen evidence](../../../../artifacts/dashboard-agent-interruption-2026-10-07/audit-source-freeze-v3.json) records the exact source and test hashes.
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

The [first watchdog VM report](../../../../artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-01/guest-harmless-watchdog-20261007T123815783Z.json)
observes a freshly created harmless Administrator supervisor assigned before resume. Its deadline
expired, termination of the held private Job was accepted, the original supervisor exited with
code76, and the queried Job had zero active members. The original watchdog driver also exited
and was joined. Qualification nevertheless refused: lifetime `TotalProcesses` was **2**, while
the reviewed gate requires **1**. The additional historical member has no identity/held-exit
provenance. An empty current scope does not establish that missing provenance.

The [reviewed refusal evidence](../../../../artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-refusal-v1.json) preserves raw duplicate-free
output, exact package/source pins, original process epochs and the fresh nonce. The original
candidate must not be rerun unchanged, and its count gate must not be loosened. The next fresh
diagnostic must observe suspended supervisor membership in any existing Job before assignment,
then record bounded accounting and owned-Job PID-list snapshots without opening or adopting
those PIDs. Unknown nesting must refuse before assignment. No monitor logon, CWT or SQL path
was exercised. This does not qualify worker interruption/recovery, CWT/startup hard timeouts,
all historical members, production admission or the broader goal.


### Native watchdog stage diagnostics (2026-10-07, native02)

The separate frozen v3 diagnostic watchdog built cleanly and passed 113 pure checks. Its actual runtime-policy validator accepted the complete self-contained publish: 192 files, 80,558,629 bytes, manifest `662c0c3319c5bc972b84778b133fea0245b97fa2ff8a1009439e845bb56d70b2`. Independent review cleared one fresh Administrator-only harmless supervisor diagnostic; no CWT, monitor, SQL, service or rights changes were performed. Every diagnostic wrapper outcome remains unqualified.

The native02 driver joined with exit 94 and `PreserveForInspection`, refusing at `BeforeTerminationEvidence`. The held supervisor exited with code 76. Complete stable accounting/PID-list snapshots show: fresh Job total0/active0/empty; assigned suspended supervisor total1/active1 with only original PID19064; later before termination total2/active2 with diagnostic PIDs6228 and19064; after termination total2/active0/empty. The suspended supervisor's any-Job query succeeded and was false before assignment. The extra member was absent from the assigned-before-resume snapshot and first observed in the later post-resume snapshot. Its exact association time, image, token and birth remain unknown; no listed PID was used to reopen, adopt or target a process; termination used only the held owned Job. These observations do not establish historical provenance for the extra member.

Evidence: `artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-02/guest-harmless-watchdog-20261007T131500518Z.json` SHA256 `2784d7427e01fe0e5d3d0ff84e590bebf4fa43f1c0c62f0137365c3c4dc8eb0c`; independent duplicate-sensitive raw/materialized verification is recorded in `watchdog-native-diagnostic-v2.json`, SHA256 `ecc688ff672c858b53b697ccf157882361bb94ac595d326cdee3c9f595838f9d`. Original driver PID24148/birth `2026-10-07T13:15:10.5110924Z`, supervisor PID19064/birth `2026-10-07T13:15:10.6802650+00:00`, fresh retired nonce `9d01dc77-e500-4df1-8452-dca7d535c0e1` bind this case. Native01 and native02 are preserved without unchanged reruns. The total-one gate, zero qualified cases, absent native worker audit and closed recovery/production admission remain unchanged.


### WinExe watchdog comparison (2026-10-07, native03)

A fresh v4 diagnostic changed only the project output type from `Exe` to `WinExe`. C# source, creation flags `0x08000404`, closed environment, snapshot checks and the total-one gate remained byte-identical. Root and independent review verified the actual pinned AMD64/PE32+/Windows subsystem2 apphost, redirected bounded CLI output and held original-process join on Windows PowerShell 5.1. The clean build passed 113 pure checks; the actual runtime policy accepted all192 fresh published files (80,558,629 bytes), manifest `3bbed00104d2a7fcbbfe9f8a6b5e4b0d229d674154ce7294967268062d0410d6`.

Native03's original driver PID24628/birth `2026-10-07T13:25:56.1791482Z` joined0. Original supervisor PID11108/birth `2026-10-07T13:25:56.9671503+00:00` reached the deadline and exited76 after accepted held-Job termination. The complete stable snapshots were fresh0/0/empty, assigned-before-resume1/1/sole11108, before-termination1/1/sole11108 and after-zero1/0/empty. Preassignment any-Job membership was false. Source disposition was `SupervisorJoinedOnly`, with no refusal stage; the diagnostic wrapper still records qualified0/successfalse and does not mint a native worker audit or recovery authority.

Actual report `artifacts/dashboard-agent-interruption-2026-10-07/watchdog-native-03/guest-harmless-watchdog-20261007T132548624Z.json` SHA256 `1456e72527f9e0ad11dd349642db9b8889b7f08bf41fec8fe014a6948d5d6d6e`; bounded independently reviewed record `watchdog-winexe-diagnostic-v3.json` SHA256 `3f32ac9b43bf27248e4d10cd6082a4cfef9d786b2b4959519f84720d27b7cadd`; fresh nonce `3c188220-31aa-436c-87b5-33d51e0c91ec`. This is one successful harmless supervisor comparison with no extra member observed, not a universal causal fix or identification of native02 PID6228. No CWT, monitor, SQL, SCM/service, account-right or CPU provisioning change occurred.

Credentialed-worker startup remains unqualified. V7's any-preexisting-Job guard would refuse even a worker already in an authoritative outer watchdog Job; ordinary process inheritance documentation cannot establish CWT inheritance or absence of a late suspended worker after interrupted CWT. A future original-worker/inner-Job handle registration protocol must complete before resume acknowledgement and preserve missing/stale/uncertain evidence. Registration after CWT return cannot solve the unresolved pre-return cleanup gap. The existing native recovery gateway stays closed, and the broad goal remains active.


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
