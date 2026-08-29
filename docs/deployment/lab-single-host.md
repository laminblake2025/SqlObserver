# Single-host Windows lab deployment

## Status and intended use

This guide prepares one **non-production Windows Server lab** to host Microsoft
SQL Server, PostgreSQL, and the SqlObserver development processes. It is a
developer/operator handoff, not an installer, supported-release procedure, or
production security policy.

SqlObserver's M12 deployment, lifecycle, identity, signing, and platform cases
remain pending. In particular, this repository does not yet provide:

- a signed MSI/Burn installer or supported Windows service registration flow;
- a standalone, operator-facing PostgreSQL migration executable;
- an accepted secret-store, service-account, certificate, or TLS policy;
- a supported reverse proxy/static-web deployment that joins `web/dist` to the
  authenticated Server API; or
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
git switch codex/m12-reports-installer-release-hardening
git status --short --branch
dotnet --info
node --version
pnpm --version
pwsh --version
```

The checkout must be clean before building. Review the branch's pull request
and its validation results before using it on the lab host.

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

The migration catalog currently contains the exact, contiguous `0001` through
`0021` sequence and `database/migrations/checksums.sha256`. The embedded
`PostgreSqlMigrationPort` verifies those bytes, requires PostgreSQL major 18,
holds an advisory lock, validates the existing ledger as an exact prefix, and
commits each migration and ledger row in one transaction.

**Do not run the SQL files individually with `psql -f`.** Doing so bypasses the
runner-owned transaction, advisory lock, checksum validation, and
`system.schema_migration` ledger contract.

The repository does not yet expose that migration port through a supported
installer or operator CLI. Therefore a complete application deployment must
stop here until one of the following exists and is reviewed:

1. the planned signed installer invokes the embedded migration port; or
2. a separately reviewed, lab-only migration host exposes the same bounded
   behavior without accepting arbitrary SQL or logging credentials.

The PostgreSQL integration suite may be run to validate the repository code in
an isolated disposable container, but its Testcontainers database is not the
lab application's persistent repository:

```powershell
dotnet test .\tests\SqlObserver.IntegrationTests.PostgreSql\SqlObserver.IntegrationTests.PostgreSql.csproj `
  --configuration Release --no-restore
```

This gate is intentional. Do not synthesize ledger rows or grant runtime
accounts migration authority to work around it.

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

## 8. Web interface gate

`pnpm --dir web build` produces deterministic static assets in `web/dist`.
The current Server host does not yet serve those files, and the Vite development
server does not proxy `/api`. A Vite-only preview therefore shows the interface
but API calls return 404.

For visual review only:

```powershell
node .\web\node_modules\vite\bin\vite.js --host 127.0.0.1 --port 5173
```

Do not treat that preview as an application deployment. A complete lab requires
a reviewed same-origin static-file/reverse-proxy design that preserves Windows
authentication, HTTPS, request bounds, and target-scoped authorization. Do not
place an unauthenticated proxy in front of the API merely to make the UI load.

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
provisioning, and authenticated same-origin web hosting remain explicit M12
deployment blockers. Resolve those through accepted ADRs and reviewed code;
do not fill the gaps with ad hoc production procedures.
