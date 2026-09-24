# Single-host Windows lab deployment

## Status and intended use

This guide prepares one **non-production Windows Server lab** to host Microsoft
SQL Server, PostgreSQL, and the SqlObserver development processes. It is a
developer/operator handoff, not an installer, supported-release procedure, or
production security policy.

SqlObserver's M12 deployment, lifecycle, identity, signing, and platform cases
remain pending. In particular, this repository does not yet provide:

- a signed MSI/Burn installer or supported Windows service registration flow;
- a signed installer or supported production PostgreSQL migration executable
  (the repository includes only a source-based, lab-loopback migration host);
- an accepted secret-store, service-account, certificate, or TLS policy;
- a supported production reverse proxy/static-web deployment (the source-based
  lab host can serve a bounded `web/dist` snapshot on the authenticated API
  origin); or
- live release evidence for the single-host topology.

Do not describe a successful lab build as a supported deployment. Do not point
the lab at production SQL Server or PostgreSQL data.

## Lab topology

The smallest useful lab has these boundaries on one Windows Server 2022 or
2025 x64 host:

```text
Browser -> SqlObserver web preview
Browser/API client -> SqlObserver.Server (ASP.NET Core)
SqlObserver.Server -> PostgreSQL 18.x repository
SqlObserver.Collector -> PostgreSQL 18.x repository
SqlObserver.Collector -> local SQL Server 2019/2022/2025 target
```

PostgreSQL is SqlObserver's application repository. It is not an observation
target. Co-location is acceptable only for a lab: it removes failure and trust
boundaries that a production design must preserve.

Recommended lab names:

- host: `sqlo-lab-01.example.test`;
- PostgreSQL database: `sqlobserver`;
- PostgreSQL runtime logins: separate Server and Collector logins;
- Windows identities: separate Server and Collector lab service accounts;
- SQL Server observation target key: `lab-sql-01`.

## 1. Prepare the host

Install and patch the following before copying application artifacts:

- Windows Server 2022 or 2025 x64;
- a Windows SQL Server 2019, 2022, or 2025 Developer Edition instance;
- PostgreSQL 18.x (the automated repository suite pins PostgreSQL 18.4);
- .NET SDK/runtime 10.0.203 for a source-based lab deployment;
- Node.js 22.22.0 and pnpm 11.19.0;
- PowerShell Core 7.5 or newer; and
- Git.

Use fixed DNS names and correct system time. Keep SQL Server and PostgreSQL
listening on loopback or a lab-only interface unless a firewall rule is
explicitly required. Do not expose PostgreSQL, Kestrel, Vite, or SQL Server to
the public Internet.

Create a checkpoint or backup before repository bootstrap. The current
uninstall design preserves PostgreSQL data; there is no supported purge path.

## 2. Create identities before granting access

Use different Windows identities for the Server and Collector even when both
processes run interactively in the lab. The Collector identity is the identity
used for Windows-integrated access to the monitored SQL Server. SqlObserver
does not accept or store a SQL login password in the observation-target form.

Create a Windows group for interactive SqlObserver administrators and record
its SID. Server authorization uses SIDs, not caller-supplied role headers.

For PostgreSQL, create separate LOGIN roles outside the migration files. The
bootstrap/migration identity must be able to create the closed group roles and
schemas used by migration `0001`. After migration:

- the Server login is a member only of `sqlobserver_server`;
- the Collector login is a member only of `sqlobserver_collector`;
- neither runtime login is a superuser, database owner, migrator, or auditor;
- `PUBLIC` has no access to SqlObserver schemas; and
- Server and Collector use different credentials.

Do not put connection strings, passwords, fingerprint keys, or certificate
passwords in Git, command history, URLs, issue text, or pull-request logs.

## 3. Check out and verify the source

From an operator PowerShell session:

```powershell
git clone https://github.com/laminblake2025/SqlObserver.git
Set-Location SqlObserver
git switch main
git pull --ff-only origin main
git rev-parse HEAD
git status --short --branch
dotnet --info
node --version
pnpm --version
pwsh --version
```

The checkout must be clean before building. Confirm that the `Validate`
workflow succeeded for the exact `main` commit reported by `git rev-parse HEAD`
before using it on the lab host.

Restore and build from the checked lock files:

```powershell
dotnet restore .\SqlObserver.slnx --locked-mode --configfile .\NuGet.Config
dotnet build .\SqlObserver.slnx --configuration Release --no-restore
pnpm --dir .\web install --frozen-lockfile
pnpm --dir .\web build
```

Do not replace locked dependencies, relax warnings, or use an unreviewed
package source to make the build pass.

## 4. PostgreSQL repository gate

The migration catalog contains the exact, contiguous `0001` through `0093`
sequence and `database/migrations/checksums.sha256`. The embedded
`PostgreSqlMigrationPort` verifies those bytes, requires PostgreSQL major 18,
holds an advisory lock, validates the existing ledger as an exact prefix, and
commits ordinary migrations and their ledger rows in one transaction. Migration
`0088` builds a query-performance index concurrently outside a transaction;
the runner checks the index and records the ledger only after a valid build.
Migration `0089` adds a nullable query-observation target key and fills it on
new inserts. Migrations `0090`–`0091` add a target-ordered index and a bounded
database backfill operation for historical observations. The backfill is not
run automatically by the migration runner.
Migration `0092` adds a concurrent target/time index. Migration `0093` lets
top-query ranking and observation RLS use the target key for populated rows,
while historical NULL rows retain exact query-ownership checks during backfill.

After upgrading, a migration administrator can backfill one target in bounded
transactions. Replace the UUID in both places and repeat until `complete` is
`true`:

```sql
BEGIN;
SET LOCAL ROLE sqlobserver_migrator;
SELECT set_config('sqlobserver.target_scope','<target-uuid>',true);
SELECT * FROM control.backfill_query_observation_identity('<target-uuid>'::uuid,1000);
COMMIT;
```

**Do not run the SQL files individually with `psql -f`.** Doing so bypasses the
runner-owned migration mode, advisory lock, checksum validation, and
`system.schema_migration` ledger contract.

The source-based `SqlObserver.Cli` exposes this port only through one closed lab
command. It accepts no connection string, host, SQL text, migration path,
password argument, password environment variable, or password file. The target
is fixed at `127.0.0.1:5432`, database `sqlobserver`, and bootstrap login
`sqlobserver_bootstrap`. Transport is deliberately limited to cleartext
loopback on the co-located non-production lab; it is not a production TLS
decision or a supported deployment interface.

Create the bootstrap login and empty database with PostgreSQL's own tools. The
`--pwprompt` option keeps the new password out of command history. These
commands also prompt for the existing PostgreSQL administrator password when
needed:

```powershell
$pgBin = 'C:\Program Files\PostgreSQL\18\bin'

& "$pgBin\createuser.exe" `
  --host 127.0.0.1 --port 5432 --username postgres `
  --pwprompt --createdb --createrole --no-superuser --no-replication `
  sqlobserver_bootstrap
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap role creation failed.' }

# This fixed NOLOGIN role owns only the forced-RLS report expiry function.
# PostgreSQL requires an administrator that already has BYPASSRLS to create it.
& "$pgBin\psql.exe" `
  --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
  --no-psqlrc --set ON_ERROR_STOP=1 `
  --command 'CREATE ROLE sqlobserver_report_expirer WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS;'
if ($LASTEXITCODE -ne 0) { throw 'Report expiry role creation failed.' }

& "$pgBin\createdb.exe" `
  --host 127.0.0.1 --port 5432 --username sqlobserver_bootstrap `
  --owner sqlobserver_bootstrap sqlobserver
if ($LASTEXITCODE -ne 0) { throw 'Repository database creation failed.' }
```

Stop if any object already exists unexpectedly; inspect it rather than
changing or reusing it blindly. Then publish and run the exact lab host:

```powershell
$migrationHost = 'C:\SqlObserverLab\MigrationHost'
dotnet publish .\src\SqlObserver.Cli\SqlObserver.Cli.csproj `
  --configuration Release --no-restore --output $migrationHost
if ($LASTEXITCODE -ne 0) { throw 'Migration-host publish failed.' }

# PostgreSQL requires temporary ADMIN OPTION membership to transfer the
# forced-RLS function to its fixed NOLOGIN owner. The migration revokes it on
# success; this finally block also revokes it after any failed attempt.
& "$pgBin\psql.exe" `
  --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
  --no-psqlrc --set ON_ERROR_STOP=1 `
  --command 'GRANT sqlobserver_report_expirer TO sqlobserver_bootstrap WITH ADMIN OPTION;'
if ($LASTEXITCODE -ne 0) { throw 'Temporary report expiry membership grant failed.' }

try {
  & "$migrationHost\SqlObserver.Cli.exe" postgres migrate `
    --lab-loopback --allow-loopback-cleartext `
    --database sqlobserver --username sqlobserver_bootstrap
  if ($LASTEXITCODE -ne 0) { throw 'Repository migration failed.' }
}
finally {
  & "$pgBin\psql.exe" `
    --host 127.0.0.1 --port 5432 --username postgres --dbname postgres `
    --no-psqlrc --set ON_ERROR_STOP=1 `
    --command 'REVOKE sqlobserver_report_expirer FROM sqlobserver_bootstrap; DO $verify$ BEGIN IF EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles granted ON granted.oid=m.roleid JOIN pg_catalog.pg_roles member ON member.oid=m.member WHERE granted.rolname=''sqlobserver_report_expirer'' OR member.rolname=''sqlobserver_report_expirer'') THEN RAISE EXCEPTION ''sqlobserver_report_expirer membership cleanup failed''; END IF; END $verify$;'
  if ($LASTEXITCODE -ne 0) { throw 'Report expiry membership cleanup failed.' }
}
```

The executable prompts once with hidden input. A successful result is a single
secret-free JSON object with `status` equal to `succeeded`, zero failed
migrations, and the applied migration list. An already-current exact ledger is
also successful with an empty list. Any unknown, missing, reordered, or
checksum-mismatched ledger row fails closed. Save the JSON with the deployed
commit ID if lab evidence is required; it contains no credential or endpoint.

After migration, create distinct runtime logins with prompted passwords, then
grant each only its closed group role:

```powershell
& "$pgBin\createuser.exe" --host 127.0.0.1 --port 5432 --username postgres `
  --pwprompt --no-createdb --no-createrole --no-superuser --no-replication `
  sqlobserver_server_login
if ($LASTEXITCODE -ne 0) { throw 'Server login creation failed.' }

& "$pgBin\createuser.exe" --host 127.0.0.1 --port 5432 --username postgres `
  --pwprompt --no-createdb --no-createrole --no-superuser --no-replication `
  sqlobserver_collector_login
if ($LASTEXITCODE -ne 0) { throw 'Collector login creation failed.' }

& "$pgBin\psql.exe" --host 127.0.0.1 --port 5432 `
  --username sqlobserver_bootstrap --dbname sqlobserver `
  --no-psqlrc --set ON_ERROR_STOP=1 `
  --command 'GRANT sqlobserver_server TO sqlobserver_server_login; GRANT sqlobserver_collector TO sqlobserver_collector_login;'
if ($LASTEXITCODE -ne 0) { throw 'Runtime membership grant failed.' }
```

Do not grant either runtime login `sqlobserver_migrator`, CREATEROLE, CREATEDB,
superuser, replication, or bypass-row-security privileges. Keep the bootstrap
credential separate and use it only for reviewed migration runs.

The PostgreSQL integration suite may be run to validate the repository code in
an isolated disposable container, but its Testcontainers database is not the
lab application's persistent repository:

```powershell
dotnet test .\tests\SqlObserver.IntegrationTests.PostgreSql\SqlObserver.IntegrationTests.PostgreSql.csproj `
  --configuration Release --no-restore
```

This gate remains intentional for every environment except the explicitly
bounded single-host lab flow above. Do not synthesize ledger rows or grant
runtime accounts migration authority to work around it.

## 5. Build application artifacts

After the repository migration gate has an approved implementation, publish
the three processes into separate directories:

```powershell
$artifactRoot = 'C:\SqlObserverLab'
dotnet publish .\src\SqlObserver.Server\SqlObserver.Server.csproj `
  --configuration Release --no-restore --output "$artifactRoot\Server"
dotnet publish .\src\SqlObserver.Collector\SqlObserver.Collector.csproj `
  --configuration Release --no-restore --output "$artifactRoot\Collector"
dotnet publish .\src\SqlObserver.McpStdio\SqlObserver.McpStdio.csproj `
  --configuration Release --no-restore --output "$artifactRoot\McpStdio"
Copy-Item .\web\dist "$artifactRoot\Web" -Recurse
```

Keep the directories distinct. Do not give the Server access to the monitored
SQL Server, do not give MCP stdio a database credential, and do not give the
Collector the Server's PostgreSQL role.

## 6. Required runtime configuration

Server and Collector both require:

- `ConnectionStrings:SqlObserverRepository`, using their distinct PostgreSQL
  runtime logins; and
- `SqlObserver:IdentityFingerprintKey`, the same 32-byte secret represented as
  exactly 64 hexadecimal characters.

The Server additionally requires bounded SID-based authorization under
`SqlObserver:Authorization`. A lab administrator binding resembles this
configuration shape:

```json
{
  "SqlObserver": {
    "Authorization": {
      "Bindings": [
        {
          "GroupSid": "S-1-5-21-REPLACE-WITH-LAB-GROUP-SID",
          "Roles": ["TargetAdministrator"],
          "AllTargets": true,
          "TargetIds": []
        }
      ],
      "DisabledActorSids": []
    }
  }
}
```

Role names are case-sensitive application enum values. Keep configuration files
outside the repository, restrict their NTFS ACLs to the owning identity and
administrators, and never commit populated copies. The final secret store and
certificate policy are still owner decisions under ADR-0017; an ACL-restricted
lab file is not a production recommendation.

The Server requires authenticated HTTPS outside the contract-test environment.
Use a certificate whose subject/SAN matches the browser hostname and whose
chain is trusted by the lab client. Do not use certificate-validation bypasses,
plain HTTP fallbacks, or caller-supplied identity headers.

## 7. SQL Server least-privilege preparation

Create the Collector Windows login on the lab SQL Server, but do not grant
`sysadmin`. Generate the reviewed permission plan offline:

```powershell
pwsh .\tools\generate-permissions.ps1 `
  -SqlServerMajorVersion 16 `
  -Principal 'LAB\SqlObserverCollector' `
  -Operation Grant `
  -OutputPath .\artifacts\lab-sql-permissions.sql
```

Use major version `15` for SQL Server 2019, `16` for SQL Server 2022, and `17`
for SQL Server 2025. An authorized DBA must review and execute the generated SQL
independently. The script does not connect to SQL Server and does not create the
Windows login.

Only request the optional replication plan when replication is configured and
the distribution database has been explicitly identified.

## 8. Authenticated lab web interface

`pnpm --dir web build` produces deterministic static assets in `web/dist`.
Copy that completed build to a versioned, administrator-owned directory. The
Server loads one bounded immutable snapshot at startup and serves only
`index.html` and allowlisted files below `assets/`; source maps and unknown
paths are not served.

```powershell
$commit = (git rev-parse --short=7 HEAD).Trim()
$webRoot = "C:\SqlObserverLab\Web-$commit"
if (Test-Path -LiteralPath $webRoot) {
  throw "Refusing to replace existing web root: $webRoot"
}

Copy-Item -LiteralPath .\web\dist -Destination $webRoot -Recurse
```

Protect the copied directory from writes by the Server service account. Grant
that identity read and execute only; retain full control only for SYSTEM and
Administrators. Then append the absolute web-root setting to the Server service
command line while retaining its existing content root:

```powershell
$serverExecutable = "C:\SqlObserverLab\Server-$commit\SqlObserver.Server.exe"
$serverContentRoot = 'C:\SqlObserverLab\Config\Server'
$serverCommand =
  "`"$serverExecutable`" --contentRoot `"$serverContentRoot`" " +
  "--SqlObserver:WebRootPath `"$webRoot`""

sc.exe config SqlObserverServer binPath= $serverCommand
Restart-Service SqlObserverServer
```

The configured path must be absolute. Startup fails closed for a missing,
empty, oversized, hidden, system, or reparse-point build. The web endpoints use
the same HTTPS listener, Negotiate authentication, fallback authorization, and
origin as `/api`; no CORS or identity-header bypass is required. Verify from a
domain or explicitly authorized lab browser:

```powershell
$response = Invoke-WebRequest `
  -Uri 'https://SERVER-DNS-NAME:5443/' `
  -UseDefaultCredentials
if ($response.StatusCode -ne 200 -or $response.Content -notmatch '<title>SqlObserver</title>') {
  throw 'Authenticated web interface verification failed.'
}
```

If `SqlObserver:WebRootPath` is omitted, `/` continues to return the service
descriptor and the web interface is disabled. `/api/v1/service` always returns
that descriptor. Do not expose Vite, add an unauthenticated proxy, bypass the
certificate, or grant the Server write access to the copied web build.

With Windows authentication, `GET /health` returns `alive` when the Server
process is responding. `GET /ready` checks PostgreSQL compatibility through the
configured repository connection and returns `ready` (HTTP 200) or `not_ready`
(HTTP 503). Neither endpoint reports SQL Server target health. The readiness
response is not cached and does not expose repository version or connection
details.

## 9. Register a target after all gates pass

When PostgreSQL migrations, API hosting, authentication, HTTPS, and the
Collector identity are working, register the local SQL Server through the web
form:

- stable key: `lab-sql-01`;
- display name: a human-readable lab name;
- Windows host: the certificate-valid DNS name;
- exactly one of TCP port or named instance; and
- certificate host name when it differs from the connection hostname.

Registration records configuration and requests discovery. It does not grant
SQL permissions. The expected initial state is `Pending discovery`; the
Collector then records explicit supported, degraded, unsupported, unreachable,
authentication-failed, TLS-failed, or timed-out evidence.

## 10. Smoke checks and stop conditions

Before collecting data, verify:

- Server and Collector use different PostgreSQL roles;
- the Collector login is not SQL Server `sysadmin`;
- PostgreSQL and SQL Server are not publicly exposed;
- browser-to-Server traffic uses validated HTTPS and Windows authentication;
- the repository ledger is the exact checksum-verified migration prefix;
- no secret appears in logs, command history, Git status, or web responses;
- registration reaches the intended target and remains target-scoped; and
- stopping either process produces visible unavailable/degraded state rather
  than a fabricated healthy result.

Stop and investigate on any checksum mismatch, unexpected migration ledger row,
certificate error, identity ambiguity, authorization denial, unsupported
server version, target write requirement, or request to weaken TLS/permissions.

## 11. Current completion boundary

Host preparation, source verification, locked builds, isolated integration
tests, and offline SQL permission generation are available now. Persistent
repository migration, supported service installation, secrets/certificate
provisioning, and production web hosting remain explicit M12 deployment
blockers. The authenticated same-origin source-based web host is lab-only.
Resolve the remaining gaps through accepted ADRs and reviewed code;
do not fill the gaps with ad hoc production procedures.
