# Local development and fixtures

Use the SDK in `global.json`, Node 22.22+, pnpm 11.19, and PowerShell 7.5+.
The startup script uses `corepack pnpm@11.19.0` when Corepack is available;
otherwise it requires that exact pnpm version on PATH.
Docker must run Linux containers for PostgreSQL integration tests. The ordinary
Local validator excludes live PostgreSQL tests; run the explicit selection below
when changing repository behavior.

## Frontend with a local API

For the complete synthetic environment on Windows, start Docker Desktop with Linux
containers and run from the repository root:

```powershell
pwsh ./tools/dev-up.ps1
# Stop the API, frontend, and database while retaining data:
pwsh ./tools/dev-up.ps1 -Stop
```

The startup command restores locked dependencies, builds the CLI and Server,
starts the pinned PostgreSQL 18.4 Compose service on `127.0.0.1:55432`, applies
checksum-verified migrations, seeds two synthetic targets, and starts the API
and Vite at `http://127.0.0.1:5080` and `http://127.0.0.1:5173`.
The Collector is not started; the sample hosts end in `.invalid`.

`-NoStart` prepares the database without starting the API or Vite. After the first
setup, `pwsh ./tools/seed-lab.ps1` reuses the same guarded bootstrap. It returns
the sample's UTC window. Repeated seeding retains its original bounded snapshot,
so freshness ages normally; choose the returned window when investigating it.

Random development credentials are encrypted with Windows DPAPI for the current
user in `artifacts/development/settings.dpapi`, with a current-user-only file ACL.
Compose receives its password through a temporary process environment variable;
the scripts do not write a plaintext settings or Compose environment file.
Process logs also live under the ignored `artifacts/development/` directory.
Keep the protected settings with the persistent development volume; replacing credentials
does not reset an existing PostgreSQL volume. The stop command preserves both.
Process ownership is checked using the saved PID, executable and start time;
the script refuses to stop unrelated processes or take occupied application ports.

The Server uses a separate `sqlobserver_dev_app` login and the explicit synthetic
identity described in [ADR-0020](adr/ADR-0020-development-authentication.md).
The handler refuses to start outside Development, on other listeners, or against
other repositories. Its configured roles are scoped to the two sample targets.
Use the `127.0.0.1` URLs; remote hosts, forwarding headers, and cross-origin
requests are rejected. The existing Windows authentication remains the default.

The Compose volume is mounted at `/var/lib/postgresql`, matching the
[official PostgreSQL 18 image layout](https://hub.docker.com/_/postgres).
This setup is for synthetic development, not production deployment.

To run Vite separately against an already configured development Server:

```powershell
pnpm --dir web install --frozen-lockfile
pnpm --dir web run dev
```

Open `http://127.0.0.1:5173`. Vite binds to loopback and proxies `/api` to
`http://127.0.0.1:5080`. It preserves the browser's Host/Origin relationship and
credentials. Vite starts only the frontend; it does not start or seed the Server.
The proxy belongs to the development server and is absent from production assets.

## Synthetic browser fixtures

Build the frontend once, then start either fixture in a separate terminal:

```powershell
pnpm --dir web run build
pnpm --dir web run fixture:dashboard
# Or:
pnpm --dir web run fixture:overview
```

The dashboard fixture listens at `http://127.0.0.1:4174`; the Overview fixture
listens at `http://127.0.0.1:4185`. Both serve the last `web/dist` build and respond
with synthetic API data. They do not contact SQL Server, PostgreSQL, or the real
API. Stop each process with Ctrl+C and rebuild after changing frontend source.
These fixtures cover their named surfaces, not every application route.

For Overview states, open `/__fixture?mode=ready`, `empty`, `partial`, `error`, or
`loading` on port 4185. The selected state is stored in a loopback cookie.

## Live PostgreSQL tests

The fixture uses the digest-pinned PostgreSQL 18.4 image by default. It creates
and removes a random isolated database for each test. Build and run the functional
selection from the repository root:

```powershell
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --configuration Release --filter 'Category=RequiresPostgreSql&Category!=RequiresM12ReportsRelease&Category!=RequiresM12ObservabilityRelease' --logger 'trx;LogFileName=postgresql.trx' --results-directory artifacts/local-tests
```

To use an existing disposable PostgreSQL 18.4 service, set
`SQLOBSERVER_VALIDATION_PROFILE=Local` and `SQLOBSERVER_LOCAL_POSTGRES` in the
calling process. The connection must specify a database and one of `localhost`,
`127.0.0.1`, or `::1`; the login must be able to create isolated databases and
repository roles. Store credentials outside version control. Pooling and verbose
database error details are disabled by the fixture. Docker is still required
because the lease restart test owns a separate container.

With that local connection configured, also run the operational-health journey:

```powershell
dotnet test tests/SqlObserver.EndToEndTests/SqlObserver.EndToEndTests.csproj --configuration Release --filter 'Category=RequiresPostgreSql' --logger 'trx;LogFileName=postgresql-journey.trx' --results-directory artifacts/local-tests
```

Release always requires `SQLOBSERVER_RELEASE_POSTGRES`; a missing or malformed
Release connection fails without falling back to Local or Testcontainers. Local
functional evidence is not release certification.

## CI evidence

GitHub Actions validates pull requests, pushes to `main`, and manual runs. The
Windows job runs the canonical Local validation; the Linux job runs the live
PostgreSQL selection and operational-health journey against a PostgreSQL 18.4
service. NuGet and pnpm dependencies are cached from their lock files.

Both jobs publish available TRX results and logs even when a test fails. For the
same per-project TRX output locally, use:

```powershell
pwsh ./tools/validate.ps1 -Profile Local -TestResultsDirectory artifacts/local-tests
```
