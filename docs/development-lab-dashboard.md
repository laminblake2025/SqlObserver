# Test the local dashboard against the SQL Server lab

The local UI runs at `http://localhost:4322/#/overview?range=1h` and forwards
requests to the qualified candidate at `https://WIN-QNGOV5GDM24:5444`. The
SQL Server 2025 target is `lab-sql-63277404`, ID
`5a3acf12-2722-4f87-b751-a7b210fe27ce`, displayed as
**WIN-QNGOV5GDM24 - package test**. Other compatibility targets may also appear
in inventory. No synthetic API responses are used.

Port 5443 hosts the older compatibility service. Its nine-metric core schema and
missing feature routes do not represent the qualified candidate. Leave that
service running independently; the launcher does not modify VM services, tasks,
accounts, repository migrations or grants.

From the working copy, using PowerShell 7.5 or newer:

```powershell
pwsh ./tools/start-lab-dashboard.ps1
# Reuse the verified build:
pwsh ./tools/start-lab-dashboard.ps1 -NoBuild
# Stop only the owned local dashboard:
pwsh ./tools/start-lab-dashboard.ps1 -Stop
```

The host binds only to loopback and uses the starting process's Windows identity
for its fixed HTTPS destination, connecting directly to the private VDI without
the workstation proxy. It forwards both `/api/v1` and `/api/v2`,
including backup APIs. It stores no passwords or SQL connection strings,
preserves certificate validation and upstream authorization, rejects foreign
Host/Origin/fetch-site and forwarding headers, and does not forward browser
credentials or role headers. Every local request must authenticate with Windows
Negotiate and match the launching account's SID. Anonymous callers receive an
authentication challenge; other accounts receive 403.

Use Edge or Chrome under the launcher's Windows account. If browser policy does
not negotiate loopback automatically, allow Windows authentication only for
`localhost` and `127.0.0.1`. The lab Playwright configuration applies that narrow
allowlist. Owner API checks use same-origin browser fetch; native probes use
`-UseDefaultCredentials`.

The launcher refuses occupied ports and checks its recorded PID, executable and
start time before controlling its own process. Logs and process records live
under `.artifacts/lab-dashboard-20261009`. After rebuilding the relay, stop and
restart an existing owned relay so it uses the new destination.

Run the connected smoke checks while the candidate and local host are running:

```powershell
pnpm --dir web exec playwright test --config playwright.lab.config.mjs
```

For an alternate local port, pass `-Port` to the launcher and set
`SQLOBSERVER_LAB_DASHBOARD_PORT` to the same value for Playwright. The checks
verify inventory, fresh collection, captured activity and relay isolation. They
create no SQL workloads or application records. A passing smoke suite does not
qualify every feature or an offline compatibility target.

Protected captured SQL requires a deliberate user action and the target-scoped
QueryTextReader grant. The separate `playwright.lab-querytext.config.mjs` suite
requires an already running bounded native probe, identified by
`SQLOBSERVER_QUERYTEXT_VERIFICATION_RUN`. The activity workload suite similarly
requires its native workload inputs. Those suites now use the qualified target;
old probe receipts for port 5443 are historical evidence only.

The complete QA findings are in `artifacts/qa-20261026/QA-Report.html`; follow-up
fix and VDI verification evidence is in `artifacts/qa-fixes-20261026`. MCP remains
outside this QA scope.

## Patch deployment checkpoint

On November 14, 2026, the maintainer confirmed that the patch installed
successfully and the application was running correctly in the test environment.
This is operator confirmation; an installed build identifier and migration-ledger
receipt were not supplied with that confirmation. It does not establish fresh
install, rollback, restore, sustained-load or MCP qualification.

The offline read-patch builder packages immutable migration 0380 for an exact
379-migration baseline. Its default database is `sqlobserver_test`; the existing
identity-fix deployment requires `-RepositoryDatabase sqlobserver_test_identityfix`.
Use the package's compatibility check before applying it. The wrapper preserves
TLS validation, verifies package and ledger hashes, creates a private backup,
and restores the services' original running/stopped states. It does not replace
application binaries. Do not run an older migration host against a repository
that has advanced to migration 0380.

The next qualification checkpoint is to record the deployed package identity and
migration ledger, then run a sustained monitoring pilot and a separate MCP
end-to-end check. The operator confirmation does not change M12 certification
status.
