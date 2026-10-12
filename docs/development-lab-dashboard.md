# Test the local dashboard against the SQL Server lab

The redesigned local UI is served at `http://localhost:4322/#/overview?range=1h`.
Its API requests go to the existing `https://WIN-QNGOV5GDM24:5443` service,
which reads the lab's PostgreSQL repository and the existing Collector's SQL
Server observations. The registered target is `lab-sql-01`, ID
`dfb2b72c-4ad4-4765-9523-528c798482f0`. No synthetic API responses are used.

From the working copy, using PowerShell 7.5 or newer:

```powershell
pwsh ./tools/start-lab-dashboard.ps1
# Reuse the verified build:
pwsh ./tools/start-lab-dashboard.ps1 -NoBuild
# Stop only the owned local dashboard; leave lab services running:
pwsh ./tools/start-lab-dashboard.ps1 -Stop
```

The host binds only to loopback and uses the starting process's existing Windows
identity for the fixed lab destination. It stores no passwords or SQL connection
strings, preserves certificate validation and lab API authorization, rejects
foreign Host/Origin/fetch-site and forwarding headers, and does not pass browser
authentication or role headers upstream. This is a single-user workstation test
host. It does not represent each browser user's Windows identity.

The launcher refuses occupied ports and checks recorded PID, executable and start
time before controlling its own process. Logs and process records live under
`.artifacts/lab-dashboard-20261009`. The previous fixture on port 4322 was replaced
after identity checks; `replaced-fixture.json` records that process.

Run the connected checks while the host and lab are running:

```powershell
pnpm --dir web exec playwright test --config playwright.lab.config.mjs
```

These use actual API responses, verify fresh collector evidence and captured
sessions, render Fleet/Summary/Activity, verify target-scoped QueryTextReader
permissions, and check local relay isolation. They
create no SQL workloads or application records. Reports and screenshots are in
the same artifacts directory.

## Current compatibility limits

The lab API now provides `/api/v1/me`, backported from the current working copy
to its deployed `9ab5fc0` version. The existing lab Windows group already grants
QueryTextReader and server read access; those bindings were preserved. The local
UI now resolves the real grants, and Activity's deliberate **Load captured SQL**
action was verified against a controlled, read-only SQL Server statement.

The native test also found and corrected capture restoration rejecting system
sessions with database ID 0. User-session bounds, duplicate identities, snapshot
binding and timestamp precision remain checked.

Query Performance on this older deployment collects metadata only. Its protected
Query Store text/plan and qualified plan-cache content routes are absent; the
QueryTextReader grant does not add those capabilities. The full current API and
Collector were not deployed: the repository history diverges from the current
migration catalog after migration 77. The compatibility patch changes only the
Server assembly and symbols, preserving all dependencies, collector, database,
configuration, protection keys and service identity. Other newer feature routes
still need a separately qualified repository and Collector upgrade.

The controlled capture qualification is separate from the ordinary, read-only
smoke suite. While its bounded native SQL probe is running, set
`SQLOBSERVER_QUERYTEXT_VERIFICATION_RUN` to the probe run ID and run:

```powershell
pnpm --dir web exec playwright test --config playwright.lab-querytext.config.mjs
```

This verifies an exact captured-session deep link, no automatic SQL-text read,
and one deliberate protected read displaying the currently executing statement.
SQL text is not stored in the JSON receipt. Native deployment and authorization
evidence lives under `.artifacts/lab-api-querytext-20261009`; the controlled UI
screenshot and capture receipt live under `.artifacts/lab-dashboard-20261009`.

The lab core collector currently returns schema/version 1 with nine core metrics.
The newer SQL scheduler CPU and OS-free-via-SQL evidence is absent and remains
unavailable in the UI. Some newer history/baseline endpoints are also missing.
Existing workload, waits, inventory, query metadata and live sessions can be
tested immediately. Discovery reports `authentication_scheme_fallback` and
degraded capability. Wiring the dashboard does not qualify that authentication
fallback or upgrade the lab Server, Collector or repository migrations.
