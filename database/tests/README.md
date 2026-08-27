# PostgreSQL M10 tests

Run validation commands with PowerShell Core 7.5+ (`pwsh`); Windows
PowerShell 5.1 and pwsh 7.4 are rejected by the canonical validator.

`m10_migration_static.ps1` validates migration invariants and the append-only
checksum entry without requiring a server. `m10_postgres_docker.ps1` probes
Docker and explicitly reports `UNAVAILABLE` with a nonzero exit code when
Docker is not installed, its daemon cannot be reached, or the connection
contract is absent. With a connected PostgreSQL 18 runner, it executes
`m10_postgres_assertions.sql` through `psql --set=ON_ERROR_STOP=1`, so every
assertion and cleanup error remains a failing result. The runner should apply
the normal migrations first and then exercise fresh/upgrade, RLS, replay,
pruning, backfill, and retention assertions. This probe is diagnostic only;
release certification must use the documented `SQLOBSERVER_RELEASE_POSTGRES`
contract and hashed evidence manifest.
