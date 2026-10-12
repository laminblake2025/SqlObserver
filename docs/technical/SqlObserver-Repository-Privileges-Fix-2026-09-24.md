# Repository routine privilege repair

At migration 78, 23 application routines grant effective EXECUTE to PUBLIC.
Twelve are intended collector entry points and eleven are internal helpers or
trigger functions. A repository login with the relevant schema USAGE can invoke
them outside its explicit role grants. Live PostgreSQL tests demonstrated server
access to collector entry points and server/collector access to internal helpers.
This is a repository role-boundary defect; database CONNECT and schema USAGE
still constrain which logins can reach the routines.

The bootstrap's per-schema default REVOKE does not remove PostgreSQL's global
PUBLIC EXECUTE default. A NULL `pg_proc.proacl` also represents default privileges,
not an empty ACL. Both the migration and tests expand effective ACLs with
`aclexplode(coalesce(proacl,acldefault('f',proowner)))`. PostgreSQL documents the
global/per-schema distinction in
[ALTER DEFAULT PRIVILEGES](https://www.postgresql.org/docs/18/sql-alterdefaultprivileges.html).

## Forward repair and preserved boundaries

Migration `0079_repository_function_privileges.sql` revokes only PUBLIC EXECUTE
from existing routines in the ten application schemas and globally changes the
migrator's defaults for future functions. Explicit collector, server and auditor
grants remain intact. No function body, target-scope check, RLS policy, or runtime
role membership changes.

Migrations 0036 and 0077 omitted `SET LOCAL ROLE sqlobserver_migrator`. The repair
transfers these objects from their bootstrap owner to the intended migrator:

- `control.observation_target_revision_identity`, including its primary-key index;
- `control.capture_observation_target_revision_identity()`;
- `reporting.list_m10_backfill_jobs(uuid,bigint,integer,timestamptz,uuid,timestamptz)`.

The dedicated `reporting.expire_report_runs(integer,uuid,bigint)` function keeps
its `sqlobserver_report_expirer` owner, SECURITY DEFINER behavior,
`row_security=off`, collector-only runtime grant, and audited lease checks. Its
invoker trigger helpers retain their existing execution mode. This exception is
intentional and is asserted by the catalog test.

The checksum of the migration is
`2742863f73fad10cb46aa9c9f1d218033d6ba2389106af7d82330d86779828e3`.
Existing migrations remain immutable. The patch was derived from repository
source and callers, live PostgreSQL 18.4 catalogs, official PostgreSQL
documentation, and an independent review of the helper and report-expiry paths.

## Regression evidence

The focused RED run used migration 78 and excluded the two cases requiring the
new migration asset. It completed 11 cases: ten failed at the expected boundary
and the independent collector control passed. Five forbidden calls returned
success; the partition helper entered its body and raised invalid-parameter
SQLSTATE `22023` instead of permission denial `42501`. Catalog checks found
PUBLIC access and bootstrap ownership, and a newly created migrator function
also granted PUBLIC execution.

After migration 79, all 13 focused cases passed without skips. They cover:

- no effective PUBLIC EXECUTE across all application schemas, including implicit defaults;
- intended routine/table owners and the deliberate report-expirer exception;
- newly created migrator functions in all ten schemas denying PUBLIC execution;
- exact collector/internal grants for all 23 affected signatures;
- actual wrong-role calls failing with `42501`, and a permitted collector call;
- migration 78 to 79 preserving existing revision identities, backfill jobs, and every explicit runtime function grant;
- the revision trigger continuing to capture a new revision after ownership transfer;
- twice reapplying the repair without owner/grant changes, followed by an empty migration-runner result.

The build completed with zero warnings and zero errors. Commands from the
repository root:

```powershell
dotnet build tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --configuration Debug --no-restore
$env:SQLOBSERVER_VALIDATION_PROFILE = 'Local'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --configuration Debug --no-build --no-restore --filter 'FullyQualifiedName~RepositoryPrivilegePostgreSqlIntegrationTests&FullyQualifiedName!~UpgradeFrom78&FullyQualifiedName!~RepairCanBeReapplied' --logger 'trx;LogFileName=privilege-red-docker.trx' --results-directory artifacts/revamp-repository-privileges
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --configuration Debug --no-build --no-restore --filter 'FullyQualifiedName~RepositoryPrivilegePostgreSqlIntegrationTests' --logger 'trx;LogFileName=privilege-green.trx' --results-directory artifacts/revamp-repository-privileges
```

The RED command ran before the migration was promoted. Evidence is retained in
`artifacts/revamp-repository-privileges`: `red-tests-docker.log`,
`privilege-red-docker.trx`, `green-build.log`, `green-tests.log`, and
`privilege-green.trx`. The tests use the digest-pinned disposable PostgreSQL 18.4
fixture. The earlier Release build lacked reference outputs, and the first
sandboxed test attempt could not access the Docker pipe; those environment-only
failures are retained separately and are not counted as the reproduction.

The complete PostgreSQL functional selection then passed 191 tests with zero
failures and zero skips in 3 minutes 27 seconds. This includes alert
administration/evaluation/replay/delivery, report creation and actual fenced
expiry, M10 backfill/revision behavior, and all 13 privilege regressions. The
command was:

```powershell
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --configuration Debug --no-build --no-restore --filter 'Category=RequiresPostgreSql&Category!=RequiresM12ReportsRelease&Category!=RequiresM12ObservabilityRelease' --logger 'trx;LogFileName=privilege-postgresql-functional.trx' --results-directory artifacts/revamp-repository-privileges
```

Evidence: `postgresql-functional.log` and
`privilege-postgresql-functional.trx` in the same artifact directory.

The updated EndToEnd project also built without warnings or errors, and all six
`M9OperationalHealthEndToEndTests` cases passed without skips. This includes the
actual external-service PostgreSQL journey, which created and removed its own
isolated database. The local development container was started only for this
check and returned to its prior stopped state; its DPAPI-protected credential was
passed through the process environment and was not printed or persisted as
plaintext. The test used `SQLOBSERVER_VALIDATION_PROFILE=Local` and the existing
loopback `SQLOBSERVER_LOCAL_POSTGRES` service contract. Commands:

```powershell
dotnet build tests/SqlObserver.EndToEndTests/SqlObserver.EndToEndTests.csproj --configuration Debug --no-restore
dotnet test tests/SqlObserver.EndToEndTests/SqlObserver.EndToEndTests.csproj --configuration Debug --no-build --no-restore --filter 'FullyQualifiedName~M9OperationalHealthEndToEndTests' --logger 'trx;LogFileName=privilege-m9-journey.trx' --results-directory artifacts/revamp-repository-privileges
```

Journey evidence: `m9-journey-build.log`, `m9-journey.log`, and
`privilege-m9-journey.trx`. These checks do not constitute release certification.

A fresh, read-only candidate review found no concrete surviving bypass or
regression. It inspected ownership transfer, effective and default ACLs, direct
runtime callers, nested helper/trigger execution contexts, and the dedicated
report-expirer exception. The migration checksum matches its manifest. The
review did not run additional builds or database tests.

The final repository validation passed with 1,418 selected .NET tests and 147
frontend tests, with no failures or skips in those selections:

```powershell
pwsh -NoLogo -NoProfile -File ./tools/validate.ps1 -Profile Local -TestResultsDirectory artifacts/revamp-security-final/test-results
```

Builds and checksum/contract checks also passed. Evidence is in
`artifacts/revamp-security-final-local.log` and its per-suite TRX directory.
The separate live PostgreSQL results above cover the database paths that the
Local profile intentionally excludes. The Linux CI timestamp fixture repair
also passed all 15 focused live M9 tests in
`artifacts/revamp-ci-m9/postgresql-m9-fixed.trx`.

## Application and recovery

Apply the migration through the normal migration runner. The migration login
must have authority to transfer the three known bootstrap-owned objects to the
migrator. Ownership repair runs before `SET LOCAL ROLE`, as in migration 0020;
insufficient authority fails instead of silently preserving incorrect ownership.
Runtime identities should not receive migration privileges.

The runner owns one transaction containing the migration and ledger insertion.
The repair uses a five-second lock timeout and five-minute statement and idle
transaction timeouts. If an ownership operation, privilege operation, lock wait,
or final catalog assertion fails, the whole migration rolls back and migration
78 remains the last applied version. Resolve the prerequisite or transient lock
and retry the same immutable migration through the runner. No application rows
are deleted or rewritten.

After commit, correct any demonstrated compatibility issue with a new numbered
forward migration. Do not restore PUBLIC EXECUTE, alter an applied migration,
remove its ledger entry, or transfer the report-expirer function to the migrator.
Existing database backup and restore procedures remain the operator's recovery
path when a full restore is required.
