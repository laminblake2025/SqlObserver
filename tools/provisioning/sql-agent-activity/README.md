# SQL17 Agent activity signed-module qualification candidate

The application now includes a separate [guarded module reader and revocable enrollment contract](../../../docs/technical/Sql-Agent-Module-Enrollment.md).
It does not install this package or enable activity collection. The offline preview and historical
qualification assets below retain their original scope; trusted enrollment is a separate control-plane action.

The [Run15 reader/enrollment checkpoint](../../../artifacts/dashboard-agent-module-adapter-2026-10-05/completion-v3.json)
passes two actual-monitor native cases: signed public-reader capture and unsigned precheck refusal.
Normal cleanup restores the baseline without fallback recovery; Agent remains stopped. The earlier
Run14 refusal exposed a client/server connection-ID namespace mismatch, now corrected with independent
continuity checks and driver SPID binding. Real PostgreSQL/API enrollment and revocation are tested
separately. Canonical retention, activity collection admission and running/session/time semantics remain open.

The original `New-QualificationPlan.ps1` remains an **offline review package**. Its manifest describes
that preview's capabilities; it does not connect to SQL Server or install anything. The separate
[finite trial library](trial/README.md) implements transactional installation, verification and
removal for controlled qualification. It does not activate a production collector or establish
native qualification merely by building. `capture-v1.sql` is a proposed procedure definition,
not a complete deployment script. Do not run it in isolation and treat the result as installed safely.

Generate a reviewable JSON plan locally:

```powershell
pwsh -NoProfile -File tools/provisioning/sql-agent-activity/New-QualificationPlan.ps1 -Principal 'LAB\Monitor'
```

`-OutputPath` optionally creates a new `.json` file in an existing directory. Existing files are
never overwritten. There is no Install, Execute, Remove, Force, server, connection-string or
credential argument. The example identity is illustrative, not an instruction to create a login.

## Proposed approval scope

The preview lists seven fixed package objects and five grants. A separately reviewed trial would
create a master schema owned by a dedicated user without a login; the fixed capture procedure;
a signing certificate in master; its certificate login; and the matching **public-only** certificate
and certificate user in msdb. The certificate login receives only `VIEW SERVER SECURITY STATE`.
The proposed server permission follows documented SQL17 requirements; native trial must establish
whether it is actually necessary on the tested build. A false `HAS_PERMS_BY_NAME` result does not
prove an actual DMV read fails. Record permission predicates and actual bounded reads separately.
The msdb certificate user receives `CONNECT`, `SELECT` on `dbo.syssessions`, and `SELECT` on
`dbo.sysjobactivity`. The existing Windows monitor receives only `EXECUTE` on the capture procedure.

Require the existing monitor login and master user to have the intended Windows SID, with no
sysadmin membership. The master user must have **exactly the supplied Principal name** because
that is the proposed EXECUTE recipient; a differently named user with the same SID is not accepted.
Require every package object to be absent; refuse name collisions instead
of adopting or overwriting objects. Preserve existing monitor mappings, grants and denies. No
Agent role, impersonation, sysadmin signer, `TRUSTWORTHY`, or cross-database ownership-chaining
change is proposed. The schema owner is deliberately different from system-table owners.

The DBA must implement and review a transaction/recovery plan covering master and msdb before
executing any DDL. Record instance identity, deployment UUID, original permission/mapping preimages,
created object IDs/types/owners, module hashes, public certificate hashes/thumbprints, signature
inventory and exact granted permissions. Commit only after expected postconditions and provenance
are durable. This package intentionally provides no unsafe partial installer or generic cleanup.

Copy public bytes using `CERTENCODED(CERT_ID(...))` and `CREATE CERTIFICATE ... FROM BINARY=0x...`
without `WITH PRIVATE KEY`. Sign the exact module, verify it, and remove the signing private key
with `ALTER CERTIFICATE ... REMOVE PRIVATE KEY`. Both databases must report `NO_PRIVATE_KEY`.
Signing material is DBA-owned and must not appear in generated plans or test logs. A future
upgrade needs a separately reviewed signing operation.

Removal remains blocked until a provenance-bound implementation exists. It must verify the
recorded instance/deployment, exact current definition/signature/object/permission inventories,
and absence of additional consumers/dependencies. It may remove only package-created objects and
the package-created monitor EXECUTE grant. It must preserve the original monitor user/login and
all pre-existing grants, and stop on any drift. This preview does not authorize removal.

## Fixed capture contract

The pinned v2 source template, shared projection and expanded-query hashes are in
`capture-v1.manifest.json`. The two projection markers are fully expanded using the exact
catalog replacement rule before the body is wrapped; no unresolved marker is deployable.
Module version1 is independent of source asset version2 and result schema1. The procedure uses
`EXECUTE AS CALLER`, has no dynamic SQL or nested procedure calls, and returns exactly the existing
before header, activity rows, and after header. It accepts exactly 513 rows, rejects NULL/other
bounds, preserves current-session filtering and binary GUID ordering, and keeps the existing
schema1 result types. The parser retains at most 512 rows; the extra row signals truncation.
No service account, executable path or job name is projected. Local job dates stay local; no
elapsed-time or UTC conversion is added.

Submit the exact UTF-8 LF module text as its own SQL batch with `ANSI_NULLS` and
`QUOTED_IDENTIFIER` ON. The manifest separately records SHA256 over UTF-8 file bytes and over
the exact UTF-16LE definition expected from `OBJECT_DEFINITION`. Do not normalize SQL text after
signing. SQL Server metadata round-trip equality is itself a native gate, not assumed evidence.

`verify-v1.sql` is a separate DBA-only, read-only inspection batch. It checks presence/SQL major
and returns definition, execution-context, signature and permission metadata for comparison with
the reviewed plan/receipt. It does not grant the monitor metadata access. Require the expected
definition hash, caller context, ANSI flags, schema owner, exactly the expected signature,
matching public certificate hashes, no private keys, and exact permission inventories.
`sys.crypt_properties` inventories signatures; `sys.fn_check_object_signatures` supplies
`is_signed` and `is_signature_valid`. A metadata row alone is not a qualification result.

## Native qualification and admission gates

1. On the actual SQL17 build, verify certificate-backed access to both msdb tables and the service
   DMV. Microsoft documents `syssessions` as sysadmin-only; this narrow candidate is unproven.
   Failure does not authorize broader signer privileges.
2. Execute as the actual monitor over integrated authentication and validated TLS. Attest original
   login, current login, Windows identity, SPID and physical connection before and after. The
   monitor's baseline direct permission/read outcomes must remain unchanged while the signed
   module succeeds. An already-readable service DMV must not be relabeled denied. A denied
   direct syssessions read should remain denied; qualify the minimum working signer grants.
3. Compare bounded results with a private DBA baseline containing a known foreign-owner job in
   the same Agent session. An empty result cannot establish full visibility. Do not replace the
   direct table query with `sp_help_jobactivity`, whose filtering/contract differs.
4. Verify all three exact result sets, scalar types, ordering, 512+lookahead behavior, invalid/NULL
   bound refusal, timeout/cancellation and unchanged caller identity.
5. Test relevant explicit DENY and absent/removed/invalid signatures fail closed in the isolated
   trial. Do not remove denies or use EXECUTE AS OWNER to make a test pass.
6. Verify actual module-definition round trip, signature validity, cross-database public certificate
   identity, private-key removal and rollback/recovery. Any uncertainty blocks deployment.
7. Service/session association, Agent restarts, queued/running states and full job visibility remain
   separate native gates. Elapsed-time, long-running and continuous production admission remain open.

Only after the initial trial should runtime discovery gain a distinct signed-module capability
record with enrolled module/version/hash/certificate evidence. Never relabel missing direct
permissions as Granted. The existing reader's direct-permission gate is unchanged by this folder.

## Primary references

- [Service DMV permissions](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-server-services-transact-sql?view=sql-server-ver17)
- [syssessions access](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/dbo-syssessions-transact-sql?view=sql-server-ver17)
- [Permission checks and signed identities](https://learn.microsoft.com/en-us/sql/relational-databases/security/permissions-database-engine?view=sql-server-ver17)
- [Signature lifecycle](https://learn.microsoft.com/en-us/sql/t-sql/statements/add-signature-transact-sql?view=sql-server-ver17)
- [Signature validity function](https://learn.microsoft.com/en-us/sql/relational-databases/system-functions/sys-fn-check-object-signatures-transact-sql?view=sql-server-ver17)
- [Public certificate bytes](https://learn.microsoft.com/en-us/sql/t-sql/functions/certencoded-transact-sql?view=sql-server-ver17)
- [Private-key removal](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-certificate-transact-sql?view=sql-server-ver17)

## Finite trial result — 2026-10-05

The separate [trial library](trial/README.md) has a passing initial actual-monitor SQL17 run: three native cases, zero skips, signed reads through the production parser, and coordinator-verified normal removal restoring the original baseline. [Evidence and limitations](../../../artifacts/dashboard-agent-module-trial-2026-10-05/completion.json). Agent was stopped; that run did not exercise full visibility, running/session/time semantics, signature/DENY negatives or process-exit recovery. Production adapter/admission remains open. The original offline preview and its manifest retain their original scope.

A subsequent [independent DBA comparison](../../../artifacts/dashboard-agent-module-trial-2026-10-05/visibility-completion.json) matched all 12 latest-session monitor rows to both DBA observations; all 12 had owner SIDs differing from the monitor SID. It qualifies that observed stopped-Agent row set, not universal visibility, exact owner continuity, running association or elapsed duration. Cleanup restored the baseline again. The affected tooling suite passes 208 managed cases.

The [signature-removal trial](../../../artifacts/dashboard-agent-module-trial-2026-10-05/signature-completion.json) passes five native cases: three signed, then two under a separately verified committed unsigned state. Removing only the recorded signature caused the monitor's canonical read to fail with 229 and no source rows; invalid bounds still failed with 50001. Schema 2 records both immutable postimages before committing removal and restores the original baseline afterward. The 303-case tooling suite, 22 selected enrollment cases and 60 pure SQL-plan cases pass. That run did not exercise process-exit recovery; explicit DENY and attached-invalid-signature behavior remain unqualified.

The [Run11 controlled-exit trial](../../../artifacts/dashboard-agent-module-trial-2026-10-05/recovery-completion.json) passes three signed native tests, then deliberately exits with code 73 after acknowledged signature-removal commit, exact unsigned verification and worker cleanup. A separate process and fresh DBA connection recover the same receipt, verify the unsigned state and restore the original baseline, exiting 0. Recovery executes zero native tests; immutable journal images are preserved as the phase changes from `unsigned` to `removed`. All 2,540 source inputs and four runner inputs were unchanged. The final snapshot found no qualification processes/services, all five core services running and Agent stopped. [Detailed scope](trial/README.md#controlled-exit-after-committed-signature-removal) excludes uncertain COMMIT, power loss, in-flight interruption and orphaned-worker recovery. Production adapter/admission remains open.

The [metadata-verification implementation](trial/README.md#metadata-verification-policy-implementation) provides immutable schema 3 / policy 2, adding only module and certificate `VIEW DEFINITION` for seven explicit grants; schema 1/2 retain their original five-grant contract. A pinned separate probe checks trusted metadata before capture EXEC and after parsing, before accepting the capture, on the actual monitor's same owned session. The finite tests use the production connection factory/parser without admitting a public module adapter. Managed groups pass 394 tooling, 132 selected SQL metadata/contract and 60 pure SQL-plan cases, reported separately. Checks at two points cannot exclude privileged change-and-restore; unsigned behavior does not establish attached-invalid-signature or explicit-DENY behavior.

Run12 failed installation validation before executing native tests and restored the original baseline. Inspection of the protected Run10 receipt confirmed that the module grantor was its fixed schema owner, correcting the new validator's `dbo` assumption. Policy 2 now explicitly grants module `EXECUTE` and `VIEW DEFINITION` as that owner, and certificate `VIEW DEFINITION` as `dbo`, with exact validation. The failed attempt remains retained; Run13 uses a fresh deployment identity without widening permissions or changing legacy policy behavior.

The [Run13 native metadata trial](../../../artifacts/dashboard-agent-module-trial-2026-10-05/metadata-completion.json) passes 2/2 cases with zero skips. Signed capture verifies definition, public certificate, cryptographic signature validity and monitor identity before/after; unsigned metadata stays readable but blocks capture eligibility, and separate negative execution returns only 229. Normal cleanup restores the original baseline, without fallback recovery. All 2,547 source inputs and four runner inputs were unchanged; the final snapshot found no qualification processes/services, five core services running and Agent stopped. The next step is an explicit module adapter with trusted, versioned, revocable enrollment before retention/API/UI. This finite trial does not admit that adapter or establish continuous metadata stability.
