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
post-commit baseline verification and returned result. Crash recovery was not exercised.

Pure validation tests are in `sql-tests/` and `../trial-tests/`. The native test class is
`SqlAgentActivitySignedModuleQualificationTests`; it is excluded from ordinary SQL sweeps. Its
three tests cover production parsing under the actual monitor, invalid/NULL bound refusal and
pre-cancelled generic source opening. The last case does not qualify a production module adapter.
The enrolled definition hash alone is not proof of DBA verification. Final managed groups pass
60 pure SQL-plan, 98 journal/guard/privacy, 99 module-contract and 74 source/parser cases;
these separate groups are not an aggregate unique-test count.

This library is qualification tooling. Production module discovery, adapter admission, Agent
session/running-job behavior, explicit-DENY/signature-removal cases and foreign-owner visibility
remain separate work.

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

## Remaining signature-failure qualification

Do not drop or alter the signature during the current callback: its immutable signed postimage
would correctly block cleanup. A subsequent trial needs a separately versioned journal recording
both signed and unsigned postimages and the uncertain-commit transition before committing signature
removal. Recovery must accept only the recorded state and preserve the strict signed validator.
Uncommitted removal on a DBA connection cannot qualify a separate monitor session. `ALTER`
removes signatures; it does not demonstrate execution with an attached invalid signature.
