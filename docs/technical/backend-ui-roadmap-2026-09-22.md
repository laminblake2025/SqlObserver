# Backend and UI roadmap implementation — 2026-09-22

This change targets deployments with 1–10 monitored SQL Servers. It preserves
the ten-target Live Activity limit and makes that limit visible in the UI.

## Delivered behavior

- Deadlock continuation reuses the cursor's original window when dates are
  omitted. Explicitly conflicting dates remain invalid. Browser navigation
  sends the same window on subsequent pages.
- Query selection retains the clicked observation, including plan, source,
  semantics and observation time. Metrics, highlighting, plan metadata and
  history follow that selection. Ranking accepts optional `databaseId` and
  `source` filters before pagination; cursors bind those filters. Existing
  unfiltered requests remain supported. First and Previous navigation are
  available alongside Next.
- Overview uses a named 30-second HTTP timeout, a 20-second composition budget
  and a five-second inventory budget. The duplicate endpoint timer is removed.
- Same-scope refresh retains evidence, selections and local investigation
  state. Loading and failed-refresh messages identify last-known evidence.
  Target/scope changes cancel or isolate old requests. Health, database and
  file sections fail and retry independently; fleet errors are displayed.
- Investigations share visible UTC context across Queries, Deadlocks,
  historical Activity and Reports. Reports preserve millisecond precision and
  enforce their existing range limits. Refresh retains the displayed window;
  “Move window to now” explicitly starts a new relative investigation. Current
  snapshots are labeled as current.
- Alert rule names replace opaque IDs as primary labels, selection is exposed
  programmatically, broad live announcements are replaced with status messages,
  and shared diagnostic/status components are reused. Error references are
  copyable.
- Sanitized structured failures correlate with API error references. HTTP
  duration/error metrics and analytics failure/queue-age metrics use bounded
  labels. Raw exceptions, query text and connection strings are not metric
  labels or public error messages.

## Database and rollout order

Apply the immutable, checksum-verified forward migrations before upgrading the
Server and Collector:

1. `0078_query_ranking_filters.sql` adds filtered ranking while retaining the
   existing function signature.
2. `0079_analytics_derivation_queue_age.sql` adds the claim projection needed by
   the updated Collector to measure original request age.
3. `0080_mcp_audit_append_bounds.sql` fixes an existing PostgreSQL regular
   expression bound that rejected valid audit writes, and disambiguates replay
   conflicts. Actor byte/control bounds and role privileges are preserved.

The installer assessment catalog and dependent source/checksum pins include
all three migrations. Earlier migration files remain unchanged. Deploy the
updated Server with its matching web assets: the new query-filter UI requires
the updated endpoint, and an older Server can ignore the added filter
parameters.

On September 22 at 18:41 UTC, the checksum-verifying runner applied 0078–0080
to the existing WIN-QNGOV5GDM24 lab repository. A separate read-only ledger
check confirmed all three filenames and hashes. Both application services
remained running while the database was upgraded. Later that day, commit
`9ab5fc0` was published to the disposable lab as a matched Server, Collector
and web candidate under `C:\SqlObserverLab\roadmap-9ab5fc0-20260922`.
The deployment retained the existing configuration directories and service
identities. Its switch script checked archive hashes, executable paths,
service state, authenticated HTTPS and the candidate web asset, and keeps
the former paths in `deployment-state.json` for rollback. Both services were
still running on the candidate paths after the validation run.

## Verification

The normal Local validator, Chromium investigations, live PostgreSQL functional
tests and passive SQL Server tests are separate evidence sets. A Local pass is
not live database or release certification.

- Chromium covers two-page deadlocks, multiple plans for one query, refresh
  retention, server filters, independent failure/retry, rapid target changes,
  keyboard selection, relative-window behavior and acknowledgement during load.
- HTTP tests exercise partial Overview evidence beyond the old 15-second
  deadline, the five-second inventory deadline and the configured 30-second
  HTTP deadline, with correlation references preserved.
- PR CI now has a disposable PostgreSQL 18 lane and a separate Chromium lane.
  SQL Server runs only on an explicitly enabled, protected, default-branch lab
  lane; PR code does not execute on that privileged runner.
- Live validation used WIN-QNGOV5GDM24. PostgreSQL 18.6 ran in a new loopback-only
  cluster under this task's directory with temporary credentials. The existing
  repository and Windows services were not reconfigured. The Docker-owned
  restart test and external release certification are excluded from this
  external-cluster run; the PR container lane includes the restart test.
- Four passive SQL Server tests passed using Windows Integrated Security,
  encryption and certificate validation. This checks the tested administrator
  identity, not the full deployment identity matrix.
- Running the previously omitted PostgreSQL suite exposed stale fixtures:
  obsolete batch limits, invalid synthetic values/circuit states, timestamp
  ordering races, timezone-dependent verification and direct reads forbidden
  by the intended role boundary. Repairs retain the permission, replay,
  target-scope and partition-boundary assertions. Ordinary migration setup now
  has its own two-minute test budget; runtime deadlines and deliberate timeout
  tests are unchanged. Migration rollback cleanup also preserves its original
  exception when the transaction is already completed or disposed.

The full Local validator passed: 397 unit, 57 PostgreSQL static, 103 SQL Server
static, 176 API contract, 114 security, 13 performance, 13 end-to-end, 77 MCP
contract and 178 release-contract tests; 156 frontend tests, TypeScript and the
web build also passed. Nine Chromium regressions passed separately. The final
Overview/diagnostics subset passed all 12 tests after adding the five-second
inventory regression. The final PostgreSQL static subset passed all 57 tests.
After query-label polish, TypeScript, 20 query tests, all nine browser tests and
the production web build passed again.

All **171 selected PostgreSQL functional cases passed** across the focused lab
runs. The final group passed 133/133 in 22 minutes 24 seconds. A case-by-case
audit of the focused and final TRX reports matched the complete selected test
inventory with no missing cases and no remaining failures. The Docker-owned
restart case was not selected on this Windows external-cluster runner. All
temporary clusters stopped cleanly. Those functional tests preceded the
separate candidate service deployment.
The coverage audit is in `artifacts/roadmap-postgres-coverage.json`, and the
final report is `artifacts/roadmap-postgres-final.trx`.

## Initial measurements

Five independent Chromium contexts per fleet size, synthetic API fixtures,
local Vite development server, September 22, 2026. These are browser rendering
and interaction measurements, not a production API or repository benchmark.

| Fixture targets | First usable p50 | First usable p95 | Filter p95 |
| --- | ---: | ---: | ---: |
| 1 | 182 ms | 189 ms | 29 ms |
| 5 | 183 ms | 191 ms | 22 ms |
| 10 | 193 ms | 216 ms | 24 ms |

The already deployed lab build had one registered target. Ten serial HTTPS
Overview requests all returned 200: p50 **6,147 ms**, p95 **11,952 ms**. During
that interval, repository counters increased by 1,948 committed transactions,
5,476 blocks read and 1,554,234 block hits; temporary bytes did not increase.
Those counters include concurrent collection and are not attributable solely
to these requests. This run overlapped isolated integration testing on the same
host, so it is not an unloaded capacity baseline. Core observation age was
unavailable in these responses; end-to-end collection lag was not measured.

On the deployed `9ab5fc0` candidate with the same single real target, 20
Overview requests spaced ten seconds apart returned **20/20 HTTP 200** over
199 seconds: p50 **185 ms**, p95 **1,111 ms**. Repository counters over the
interval increased by 4,617 commits, 9,899 blocks read, 3,654,772 block hits
and zero temporary bytes; they include concurrent collection and cannot be
assigned to the API requests alone. This paced run used a one-hour fixed
investigation window and authenticated HTTPS with certificate validation. It
is a one-target observation, not a 5- or 10-target capacity result. The
benchmark's original observation-age field was invalid because PowerShell
converted an already parsed UTC date through local-time text. After fixing the
script, a separate three-request sample measured maximum displayed core
observation ages of **9, 20 and 30 seconds**. These are not source-change-to-UI
latencies. Ten recent core collection runs all succeeded and took 0.5–9.6
seconds from scheduled start to persistence in a separate read-only check.

The candidate smoke check returned 200 for Overview, query status, ranking,
a distinct ranking continuation row, a nonempty source filter, a nonempty
database filter, and deadlock list. Reusing the unfiltered ranking cursor with
a conflicting source filter returned 400. Overview showed current evidence. The lab had no
deadlocks in the tested hour, so live deadlock continuation was not exercised;
the browser regression covers that behavior. Both Windows services remained
running under their original service identities and candidate executable
paths after these checks. The TLS check covers this host and certificate at
this point in time, not renewal or the full identity matrix.

Reproduction:

```powershell
pwsh ./tools/validate.ps1 -Profile Local
cd web
pnpm exec playwright install chromium
pnpm run test:browser
pnpm run benchmark:browser
```

`tools/lab/run-roadmap-postgres-validation.ps1` runs the functional suite in an
isolated Windows PostgreSQL cluster. `tools/lab/measure-roadmap-api.ps1` records
latency and optional repository counter deltas without printing credentials;
`tools/lab/smoke-roadmap-candidate.ps1` checks authenticated candidate APIs.
Raw local artifacts live under `artifacts/` and `web/.artifacts/`; lab results
live under `C:\SqlObserverLab\roadmap-validation-20260922` and
`C:\SqlObserverLab\roadmap-9ab5fc0-20260922`. The candidate benchmark and
smoke JSON copies are in `artifacts/roadmap-9ab5fc0-20260922`.

## Remaining production qualification

The lab currently has one real monitored target. Sustained 5- and 10-target API,
repository and ingestion-lag measurements remain unmeasured; browser fixtures
do not substitute for that evidence. Repeat capacity measurements on the exact
candidate build with the intended collection workload before larger performance
refactors or capacity claims.

No signed installer exists in this checkout. Installation/upgrade, backup and
restore, interrupted installer migration recovery, the deployment identity
matrix and TLS lifecycle qualification remain release gates. ADR-0016 and
ADR-0017 are proposed records with unresolved packaging, identity and ownership
decisions. Passing functional tests must not mark these gates certified.
