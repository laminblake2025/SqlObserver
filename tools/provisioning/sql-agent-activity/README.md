# SQL17 Agent activity signed-module qualification candidate

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

## Native gates before any adapter or admission

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

The separate [trial library](trial/README.md) now has a passing actual-monitor SQL17 run: three native cases, zero skips, signed reads through the production parser, and coordinator-verified normal removal restoring the original baseline. [Evidence and limitations](../../../artifacts/dashboard-agent-module-trial-2026-10-05/completion.json). Agent was stopped; full visibility, running/session/time semantics, signature/DENY negatives, crash recovery and production adapter/admission remain open. The original offline preview and its manifest retain their original scope.

A subsequent [independent DBA comparison](../../../artifacts/dashboard-agent-module-trial-2026-10-05/visibility-completion.json) matched all 12 latest-session monitor rows to both DBA observations; all 12 had owner SIDs differing from the monitor SID. It qualifies that observed stopped-Agent row set, not universal visibility, exact owner continuity, running association or elapsed duration. Cleanup restored the baseline again. The affected tooling suite passes 208 managed cases.
