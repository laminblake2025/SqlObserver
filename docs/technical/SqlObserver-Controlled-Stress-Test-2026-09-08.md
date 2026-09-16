# Controlled SQL stress and Observer validation

## 16 GB repeat — 2026-09-09

The guest reports 16,977,428,480 visible memory bytes after the upgrade. Startup
checks found an absent SQL TLS certificate binding and a PostgreSQL process left
running after its service startup timed out. PostgreSQL was stopped cleanly and
restarted through its service; its lock file was not removed. SQL received a new
validated certificate with a distinct SQL-only subject and the existing hostname
SANs. The SQL service identity has private-key read access. The HTTPS certificate
was retained, and the Observer server identity received the required key read ACL.
An encrypted TCP SQL connection and all four running services were verified.

The Windows PowerShell guardrail calculation now explicitly uses double-precision
Math.Max operands. This prevents an Int32 conversion overflow at the 15% memory
threshold on a 16 GB guest without changing the threshold. Harness checks pass
52 locally and 51 on the lab VM, including exact 16 GB threshold boundaries.

Full run `fb6033b88da345b7ac8f5a3febb9d889` ran from 02:52:27 to 03:45:41 UTC.
Baseline, protected-query retrieval, and all 2/4/8/16-worker ten-minute stages
passed. The run **failed at the blocking-history API read** after successfully
capturing the live blocker and two waiters. Cleanup passed with zero remaining
sessions. All 597 availability probes succeeded; P95 live latency was 26.4985 ms,
maximum live age 10.0013 seconds, maximum inferred collection gap 10.7671 seconds,
peak CPU 57%, and minimum available memory 12,508,700,672 bytes. Its unchanged raw
results are in `TestResults/stress-harness/full-fb6033-original`.

Browser evidence under `TestResults/stress-harness/browser-fb6033b88da345b7ac8f5a3febb9d889`
verifies sixteen sessions split eight per database, database and blocked-only
filtering, pause/resume, valid interval changes, exact historical minute selection,
inert protected SQL rendering, and preserved details after request disappearance.
Health rendered current engine, database and file evidence. Query rankings initially
timed out (5,051 ms API / 6,315 ms diagnostic), then rendered after warm retries at
690–783 ms; the initial failure remains recorded rather than being relabelled passed.

Forward migrations 0063–0065 and matching binaries were deployed after cleanup;
all three migrations applied at 03:47:15 UTC. They preserve permissions, cursors,
observation ordering and existing Overview timeouts. The changes use the latest
completion to bound operational selection, inline existing M5 evidence predicates
inside protected read functions, and use a target/window-specific Overview plan.
Post-deployment API reads succeeded: Overview 2,754 ms, Health 43 ms, Sessions
784 ms, Requests 155 ms, Blocking history 20 ms, TempDB 21 ms, and Rankings 643 ms.
Overview now renders the workload ramp, blocking history, host CPU and operational
cards; no runtime source is unavailable. Backup age remains explicitly unavailable
because its source timezone is unknown. These single reads are not percentile claims.

Matching binaries are under `C:\SqlObserverLab\bounded-reads-20260909`; the staged
bundle SHA-256 is `0982260b0de5b43aba69ab9cd1517ddc92d14c077bf40a9f09a6098d6a49b0fe`.
All four services were confirmed running. The 25 real PostgreSQL Activity/operational
checks, two Overview history checks, and an additional empty-target/unfinished-run
regression pass. The complete local gate passes 1,097 .NET and 111 web tests. An
initial fixture attempt failed because local Docker was stopped; it was started
and the fixture suite rerun successfully.

Focused run `c55a33480a2a4c0aae9a9ebd83cab071` ran from 03:48:40 to 04:06:55 UTC.
Blocking history, deadlock capture and both viewer comparisons passed. One viewer
had P95 10.1403 ms across 13 requests; five viewers had P95 14.0581 ms across 65
requests. Each 140-second window contained fourteen collection timestamps. The
browser verified participants 76 and 77, victim 76, and the two opposing key/X
relationships for the 04:00:34.410 UTC deadlock. The run then **failed at alert
setup** because the rule never acquired its first normal state. PostgreSQL recorded
three five-second timeouts in evidence reconciliation. Cleanup passed. All 206
availability probes succeeded; P95 was 11.8942 ms and maximum live age 9.8856 seconds.
The unchanged report is in `TestResults/stress-harness/functional-c55a33-original`.

Migrations 0066–0067 and matching binaries were deployed at 04:16:53 UTC after
cleanup. Ranking now streams the bounded intermediate window instead of writing
about 25 MB of temporary data; a lab diagnostic completed in 524 ms with zero
temporary writes. Alert reconciliation bounds metric candidates by activation time
before joining retained run history, retaining the existing freshness, ownership,
lease and replay checks. Its diagnostic improved from timeout to 57.666 ms. All 24
real PostgreSQL alert tests pass, including 20,000 older samples in the activation
regression. The ranking scope/order/pagination checks also pass.

Current binaries are under `C:\SqlObserverLab\final-stress-20260909`; bundle SHA-256
is `1d6e9b1e37f23f56f2679abb6206b66f8795a345d8a9a6682550b9e4e30af61c`. All four
services were confirmed running and preflight passed.

Alert-only run `217fdf3788294747aa8e7ffaaf9d78e3` completed at 04:31:15 UTC.
All automated checks passed: pending/firing/acknowledged/resolved, a distinct new
episode, maintenance suppression, all recovery APIs, collector non-overlap and
resource recovery. Firing was observed 46.78, 51.94 and 53.37 seconds after the
three threshold crossings. Five expected Windows Event Log records (5633–5637)
matched the eligible committed events, with delivery delays of 0.25–4.89 seconds
and no duplicate stable-firing deliveries. Maintenance state transitions occurred
with delivery suppressed. The raw status remains `inconclusive` solely because
its browser-verification placeholder is not rewritten by the controller.

The browser verified acknowledgement, the new firing episode, no active alerts
after recovery, retained 04:20:07.155 UTC session history, and historical details
preserved while current run-filtered Live rows were empty. Earlier full-run browser
evidence verifies protected executing SQL, valid interval changes, pause/resume,
all sixteen sessions and database filtering. Typed deadlock browser evidence is
retained with the Functional run. Original raw reports remain unchanged.

The final alert run had zero failed probes out of 153, P95 live latency 12.7652 ms,
maximum live age 9.9903 seconds, fourteen minute snapshots and repository growth
of 19,800,064 bytes during that run. Final cleanup independently verified zero
owned worker processes and tagged SQL sessions, disabled temporary rules and
destinations, cancelled maintenance, and all four services running.

Read-only recovery verification for the earlier Full run initially timed out in
the harness's query-coverage aggregation. Selecting the query collector's bounded
run set before observation lookups preserves exact counts and ownership checks;
the corrected read completed in 214.763 ms. The harness passes 52 local and 51 lab
checks after this reporting fix. Repeat Full recovery at 04:33:46 UTC passes all
automated checks, with 53 historical minutes, 921 Sales Query Store observations
and 50 Warehouse plan-cache observations in the original collection window.
The later readback's 149,504,000-byte growth includes subsequent retests; it is
not represented as growth measured at the original Full run's completion.

Final local validation passes 1,097 .NET and 111 web tests; the focused real
PostgreSQL suites pass 25 Activity/operational, one empty-header, two Overview,
two ranking and 24 alert tests. No SQL memory setting, collection schedule,
existing alert rule or migration history was changed.

**Acceptance qualification:** all requested scenarios were exercised successfully
across the Full run and focused retests after the fixes. This is not a clean
single-build Full pass, ten-server capacity proof, outage test, external-delivery
test or release certification. Machine-readable evidence and explicit per-run
classifications are in `TestResults/stress-harness/validation-summary-20260909.json`.

## Earlier delivery status (2026-09-08)

The harness is implemented under `tools/lab` and deployed at
`C:\SqlObserverLab\StressHarness` on `WIN-QNGOV5GDM24`. The expanded disk passes
preflight: 99.68 GiB total and approximately 36.5 GiB free.

Full run `733c0b486db246d9bf0466d71adc21c2` ran from 19:13:41 to 19:55:31 UTC
on 2026-09-08 and **failed at the memory guardrail during the sixteen-worker stage**.
Baseline and the 2/4/8-worker ten-minute stages passed. The watchdog recorded
1,205,047,296 available bytes, below 15% of 8,488,157,184 total bytes. Automatic
cleanup passed with zero owned sessions remaining. P95 live latency was 23.149 ms,
maximum observed live age 29.785 seconds, and one of 461 probes failed. The maximum
inferred collection gap was 34.628 seconds. No full pass or sixteen-worker capacity
acceptance is claimed. The raw failed report is preserved in
`TestResults/stress-harness/full-733c-original`.

Browser evidence from this run independently verifies protected executing SQL,
preserved details after request disappearance, pause/resume, exact historical
selection, and sixteen tagged connections split eight per database. The automated
protected-query single sample remains inconclusive because it caught no active
statement; its result has not been rewritten.

Migrations 0059–0061 and matching binaries were deployed at 20:01:52 UTC after
cleanup. Initial authenticated API reads succeeded: Health 635 ms, Waits 799 ms,
and default 24-hour query rankings 613 ms. Browser Health and Query performance
rendered successfully. Overview still exhibited missing-source panels during its
combined reads and remains an open performance finding. Its five-second source
and twenty-second overall safeguards are unchanged.

The lower-load `Functional` retest profile omits the ramp and records that omission
as inconclusive. It retains blocking, deadlocks, one/five viewers, alerts,
maintenance, recovery and every guardrail. Preflight currently holds the retest
because the deadlock collector is in timeout backoff and a subsequent SQL TLS
pre-login handshake timed out. Migration 0062 and matching binaries were deployed
at 20:18:16 UTC to give its unchanged bounded reader twenty seconds. The collector
still reported a timeout at 20:22:46 UTC, so this change has not established
recovery. No Functional workload was started. More VM memory has been requested
for a later Full repeat. Both services remain running; no stress workers remain.
No ten-server capacity or release certification is claimed.

Local validation passes 1,097 .NET tests and 111 web tests. Real PostgreSQL tests
pass 24 alert checks, three query-window checks and 17 operational-health checks.
The harness passes 49 local checks and 48 on the intended lab host. The final PostgreSQL read-projection suite passes 14 tests, including eleven
Activity/Health checks. The passive deadlock suite passes 13 tests. Local validation intentionally omits external
release-certification lanes.

## Defects found and corrected

All repository changes use forward-only migrations. Migrations already installed
on the VM were preserved byte-for-byte; migration/installer/certification pins
were updated for new files. Runtime SQL collection remains passive and bounded.

| Change | Problem and validation |
|---|---|
| 0043–0044, 0055–0057 | Query rankings exceeded the five-second repository deadline as history grew. Window-first planning and cheaper request-wide scope checks preserve forced RLS, ownership, sorting and cursor semantics. The measured 24-hour query fell from about 6.2 seconds to 0.95 seconds; API checks were 3.16 seconds cold, 0.99 seconds warm and 14 ms for the current-run interval. |
| 0045 | Capability evidence expired in step with the five-minute query collector. Renewal becomes due up to 90 seconds early, without changing target revisions or collector schedules. Lab renewals occurred about four minutes apart. |
| 0046–0053 and alert repository/evaluator | Fixed scoped delivery reads, initial audit identities, dispatch permits, oldest-first claims, sample identity typing, configured connection rules, sustained confirmation and discovery after a pooled connection resets its scope. New rules admit evidence only from their configuration time. Scoped authorization, exact identities/revisions and replay remain enforced. |
| 0054 and matching collector manifest | Bounded system_health reads took 5.1–5.5 seconds and exceeded the old five-second execution limit. The deadline is ten seconds; SQL, 30-second schedule, non-overlap, file window and payload bounds are unchanged. |
| 0058 | The operational latest-run header used a slow generic plan: a TempDB header took about 4.5 seconds and a parameterized diagnostic exceeded 15 seconds. The same fixed, parameter-bound SQL is planned for each target/collector while preserving scope, privileges and ordering. Deployed at 19:07:53 UTC; warm TempDB API readback was 119 ms with complete evidence. |
| 0059 | Wait reads now choose the exact current/adjacent baseline pair and bound the current page before joining counters. Explicit target, revision, outcome, cursor and reset predicates are preserved. A pinned-page diagnostic measured 3.5 ms; deployed API readback was 799 ms. |
| 0060 | A covering query-identity index retains existing ownership/RLS predicates while avoiding repeated heap reads. A 24-hour ranking diagnostic had spent most of 15.3 seconds in 106,583 ownership checks. Deployed ranking API readback succeeded in 613 ms; scope/order/final-page regressions passed. |
| 0061 | The existing instance-health function uses a custom plan for its selected target. A diagnostic took 21 ms; deployed API readback took 635 ms and the Health browser page rendered. Prepared-call regression checks retain the latest evidence for each explicit target. |
| 0062 | The unchanged passive deadlock query took about eight seconds when parameterized under the collector login. The end-to-end deadline is now twenty seconds, retaining the thirty-second schedule and all source/payload limits. Deployed at 20:18:16 UTC. Subsequent collector timeout and SQL TLS handshake failure leave runtime recovery unverified. |
| Lab HTTPS listener | The VM hostname resolved to IPv6 and IPv4, while Kestrel listened only on IPv4. New connections periodically took about two seconds. Changing the existing HTTPS endpoint to `https://[::]:5443` enabled both address families without weakening certificate validation. Fifteen requests spanning connection renewal had P95/max 68.2 ms. |
| Harness assertions/reporting | Database query coverage is checked by collection time independently of the first 200 ranked rows, whose metric intervals may start earlier. Missing final worker records remain visible with unavailable batch counts. A read-only recovery verifier preserves original reports and uses the completed run's measurement window. |
| Harness readiness | Windows safety queries now have explicit three-second operation bounds. Functional retests omit the load ramp and explicitly report that omission; they cannot substitute for Full acceptance. |
| Browser labels | Health timestamps now use explicit UTC. Blocking table columns identify root resolution so a graph marked resolved is not mistaken for a finished wait. |

The operational PostgreSQL fixtures were repaired to establish prerequisite
collector evidence, keep unrelated schedules out of bounded due pages, read
catalog data through the fixture administrator, dispose readers, use UTC dates,
and supply valid fingerprints and bounded source/byte accounting. Collector
permissions were not expanded to accommodate tests. The new header regression
checks large older history, prepared calls, unsupported collector IDs and denied
cross-target/absent scope.

Query Store/plan-cache truncation and fallback states remain explicit. These
performance fixes do not establish complete query coverage or convert cumulative
measurements into workload-only totals.

## Earlier evidence retained

- `d70322f792b445d588f8819dcb445c9e`: all four ten-minute ramp stages passed;
  blocking-history timestamp formatting stopped the run. Corrected to microsecond
  UTC API timestamps; cleanup succeeded.
- `96caa43760434b0798714b5544f6dd48`: stopped after two ramp stages to deploy alert
  fixes; cleanup succeeded. Browser checks verified live/history behavior.
- `ffbcc0fbf2eb4a2ea092d4e8c8b25856`: all alert lifecycle, new-episode, local delivery
  and maintenance-suppression assertions passed. Recovery exposed the ranking
  timeout subsequently fixed by 0055–0057. Cleanup succeeded.
- `cdf1e16e868a4847ae711a1beaa0aea8`: all 2/4/8/16-worker stages, blocking and deadlock
  assertions passed. One of 13 successful one-viewer requests took 2.024 seconds,
  failing that short sample's P95 limit. The overall run had 618 guardrail samples,
  no failed probes, P95 live latency 321 ms, maximum live age 10.06 seconds and peak
  CPU 80%. Automatic cleanup reported zero remaining sessions. Its raw failed
  report is preserved; the listener correction was validated separately before
  starting the current full repeat.

Browser evidence from `cdf1...` confirms both database filters, sixteen tagged
sessions split evenly between fixtures, inert protected SQL, pause/resume,
retained request details, exact historical minutes, valid interval deltas,
a root with two waiters and the captured deadlock victim/participants. Evidence
from earlier runs is kept separate from the current acceptance run.

## Operator commands

Run as the lab administrator in Windows PowerShell 5.1. The harness, watchdog and
workers use this runtime explicitly. No Pester module or third-party workload
generator is required. The existing SQL fixtures must already be installed.

```powershell
Set-Location C:\SqlObserverLab\StressHarness
.\test-stress-harness.ps1
.\run-stress-test.ps1 -Operation Preflight
.\run-stress-test.ps1 -Operation Run

# Use the exact run ID printed by Run:
.\run-stress-test.ps1 -Operation Status -RunId <run-id>
.\run-stress-test.ps1 -Operation Stop -RunId <run-id>
.\run-stress-test.ps1 -Operation Cleanup -RunId <run-id>

# Read-only focused repeat after a completed run and successful cleanup:
.\verify-stress-recovery.ps1 -RunId <run-id>
```

Before the first use, run `provision-stress-credential.ps1` interactively as the
same Windows operator. It prompts without echo and writes a machine-protected
DPAPI credential to `C:\SqlObserverLab\ProtectedStress\repository.dpapi.json`, with
directory access restricted to that operator and SYSTEM. The file records and verifies the operator SID; Windows DPAPI protects the password without depending on an interactive SSH logon key cache. This has already been
provisioned for the lab administrator. Never put its password in parameters,
application configuration, reports, or source control. The repository client uses
the credential only in a private child environment; every evidence query runs in
`BEGIN READ ONLY` with a five-second PostgreSQL statement deadline. It cannot
apply migrations or write synthetic telemetry.

## Workload and acceptance

The controller reads actual schedule intervals. Baseline requires three new
engine samples. Ramp stages run for at least two minutes and two relevant
collection intervals, split evenly between Sales and Warehouse. Queries use
`MAXDOP 1`; updates are rolled back, and temporary tables contain at most 2,000
rows. Blocking uses separate fixture rows; deadlock participants run once.
Application names include the run ID, scenario and worker number.

The temporary `engine.user_connections` rule uses maximum baseline plus four,
hysteresis two, two confirmations, and a confirmation window of three engine
intervals. Idle connections cross the threshold by four, with at most 32 workers.
Unexpected baseline drift makes the scenario inconclusive without increasing the budget.
The harness checks pending, firing, acknowledgement, resolution, a new alert
identity on recurrence, and delivery suppression during maintenance. Local
delivery requires a successful repository attempt and exactly one matching
Windows Application event. Existing approved destinations or maintenance windows
block preflight to prevent interference or external delivery.

Live filtering, query authorization/retrieval, blocking, deadlock details,
historical snapshot retrieval and one-versus-five-viewer tests use Observer's
authenticated APIs. Query text is inspected only in memory and discarded; saved
evidence contains identifiers and measurements. Freshness must stay within 30
seconds, live API P95 below two seconds, and local notification latency within 30
seconds of the committed event. Alert firing and recovery deadlines derive from
the engine interval. Existing collector schedules and SQL memory settings remain
unchanged. Single-instance Availability Group `unsupported` evidence is expected.

After a full run, inspect the same run in the browser: Overview, Health, Activity,
Query performance, Deadlocks and Alerts. Verify pause/resume freezes and resumes
the timestamp, database changes isolate rows, SQL appears as inert text in details,
and the selected observation stays intact after its request disappears. Check an
exact historical minute after returning to Live. Record browser evidence alongside
the run; the automated report deliberately leaves browser verification inconclusive
instead of treating successful API responses as proof of UI behavior.

## Bounds, recovery and evidence

Historical readback at 20:25:28 UTC reopened the exact 19:48:01.664 UTC snapshot
after cleanup, showing all sixteen run-tagged sessions, eight per database.

An independent watchdog samples CPU, available memory, fixed-disk free space and
Observer availability every five seconds, with settlement-based requests. It
stops at six consecutive CPU samples above 90%, memory below max(15%, 1 GiB), disk
below max(15%, 5 GiB), or three failed/over-five-second probes. It also stops work
on a missing controller, heartbeat older than 45 seconds, operator stop, or the
90-minute deadline. Workers check stop/heartbeat/deadline independently; a SQL
command is bounded to 35 seconds. Viewer loops never overlap their own requests.

An exclusive controller lock admits one run. Atomic manifests record worker PID,
creation timestamp and executable; all three must match before forced termination.
Cleanup serializes concurrent callers, closes only owned workers, waits for their
SQL sessions to disappear, and uses current revisions to disable only run-owned
rules/destinations and cancel run-owned maintenance through existing APIs.
Failed cleanup is retained and blocks a new run until retried successfully.
Synthetic databases, collected history, Windows events and reports are retained.

Evidence lives under `C:\SqlObserverLab\Workloads\Stress\<run-id>`:
`manifest.json`, `results.json`, `report.md`, `guardrails.jsonl`, per-worker outcomes, API evidence,
administration journals containing no secrets, and `cleanup-result.json`.
Pass/fail/inconclusive outcomes are distinct. A guardrail stop is a failed run,
not a capacity measurement claimed as successful. This shared single-server VM
does not validate ten physical SQL servers or constitute release certification.

The recovery verifier starts no workload and makes no administration writes. It
refuses a running controller, remaining owned workers, failed cleanup or expired
24-hour live history. It writes a timestamped `recovery-verification-*` directory,
preserving the original manifest and report. Its browser check remains explicitly
inconclusive until a reviewer records the corresponding browser evidence.
