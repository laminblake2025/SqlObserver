# Finite SQL Agent signed-module trial

This Windows/.NET 10 library provides `TrialCoordinator.PreviewAsync`, `RunAsync` and `RecoverAsync`.
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
