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
