# ADR-0027: Version SQL metric analytics inputs

Status: SQL metric activation implemented, 2026-09-30. Source, rollup/baseline workers, read API, chart references and MCP discovery/read are active. Retention backup/restore and a synthetic SQL baseline fleet benchmark are verified on isolated PostgreSQL; lab and production fleet validation remain open.

## Context

The dashboard now supports baseline references, but the analytics catalog
contains only ten host and replication metrics. SQL Server observations already
include memory, connections, scheduler and batch counters. Their baseline
inputs need explicit provenance: host CPU is not SQL scheduler CPU, and a
cumulative counter cannot be treated as a gauge.

Catalog version 1 is pinned in persisted rollup metadata, worker reads,
backfill cursors, application validation and MCP discovery. Adding keys to
that catalog would change the interpretation of existing results and jobs.

## Decision

Keep `MetricCatalogV1` unchanged and define `MetricCatalogV2` independently.
Version 2 retains the ten existing entries and adds these seven SQL projections:

| Metric | Unit | Inputs |
| --- | --- | --- |
| `engine.user_connections` | count | Same-key gauge |
| `engine.process_physical_memory_bytes` | bytes | Same-key gauge |
| `engine.os_available_memory_bytes` | bytes | Same-key gauge |
| `engine.memory_grants_pending` | count | Same-key gauge |
| `engine.scheduler_runnable_tasks` | count | Same-key gauge |
| `engine.batch_requests_per_second` | batches_per_second | Batch counter delta and engine epoch |
| `engine.sql_scheduler_cpu_percent` | percent | Scheduler CPU counter delta, scheduler count and engine epoch |

SQL projections accept no dimensions. The cumulative batch and scheduler CPU
counters remain inputs, not public output metrics. Keep the five direct gauges
separate from the two derived rates. Label scheduler CPU explicitly so it is
not presented as SQL process CPU or host CPU.

The version 2 SHA-256 digest is
`702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338`.
Its UTF-8, LF canonical representation covers sorted metric definitions,
labels, units, source, enabled state, dimensions, derivation version, sorted
input keys, maximum sample gap and stable scheduler-count requirement.
Changing an algorithm requires a new derivation version. Version 1 retains
its existing digest and ten-entry contract.

## Source and aggregation requirements

Use the existing Overview history calculation in migration 0207 as the
compatibility reference when implementing the repository source adapter.
Read successful, loss-free `engine.core` runs for the exact target revision,
with empty dimensions and source/run completion before the job's snapshot
cutoff. Preserve run identity when grouping inputs. Do not combine inputs
from unrelated runs or turn missing evidence into zero.

Pair runs in timestamp and run-ID order. Rate inputs require matching,
non-null `engine.start_time_key`, positive elapsed time of at most two
minutes, nondecreasing counters and values within the existing exact-double
counter bound of 9007199254740991. Batch rate is the delta divided by elapsed
seconds. Scheduler CPU also requires a positive, unchanged scheduler count.
Reject its delta above elapsed milliseconds times scheduler count times
1.05; cap otherwise-valid percentages at 100. Missing or incompatible inputs
produce a gap. One rate can be unavailable while another remains valid.

Retain a preceding sample up to two minutes before the requested window for
the first delta. Emit only observations inside the requested half-open window.
For derived-rate rollups, weight valid values by elapsed seconds; direct gauges
use sample weights. The current sample-weighted `RollupV1` must not silently
process those rates as version 1 results.

## Repository source implementation

Migration 0292 extracts the common run-level calculation into the private
`reporting.sql_core_metric_inputs` function. Both Overview workload readers
use it, preserving their existing public routing, Viewer check and aggregation.
The helper supports the legacy and partitioned run stores; each caller keeps
its existing fleet rollout switch. It has no PUBLIC, server, collector or
auditor execute grant.

The collector-only `reporting.read_sql_metric_analytics_inputs` function
returns run identity, UTC observation time, closed metric key, nullable value,
nullable aggregation weight and completeness. Its bounds are one day,
100,000 rows and 8 MiB of PostgreSQL row JSON, including completeness. It
fails on row or byte overflow, so callers cannot mistake a limited read for
complete input. The returned rows have a stable timestamp, run-ID and metric
order. This function has no pagination contract: worker integration must use
bounded windows and metric-scoped jobs, rather than silently discard rows.

Null rate observations remain present and incomplete; a missing direct gauge
also remains an incomplete observation for a run with other core inputs.
The wrapper uses the existing M10 physical-storage route and the same target,
revision and snapshot checks as the shared calculation. This source is not
compatible with `MetricPoint` or `RollupV1`, which cannot retain nullable
observations and elapsed weights together.

## SQL rollup worker implementation

Migration 0293 adds the selected source catalog and captured collection cadence
to jobs, and checksum, weights and summary statistics to canonical rollup rows.
The production derivation worker now claims catalog version 2 SQL rollup jobs,
reads typed nullable inputs, computes one complete UTC bucket and persists it
under the job's target revision, snapshot cutoff, generation and lease fence.
Apply this migration before deploying the matching worker binary.

The SQL scheduler creates jobs for all seven keys and the latest completed
five-minute, hourly and daily buckets of active targets with enabled
`engine.core` collection. Each deterministic job captures its cadence once.
Each call inserts at most 2,688 missing jobs; later calls advance through the
remaining targets. Successful worker batches drain continuously within the
existing concurrency limit of two. Empty or failed batches retain the polling
delay. This changes draining behavior, not the overall collector concurrency
design. The synthetic baseline benchmark below measures baseline draining;
it does not measure collection or SQL rollup throughput.

Direct gauges use sample weights; rates use elapsed seconds. Expected samples
are the bucket duration divided by captured cadence, rounded up. Missing or
invalid observations reduce coverage, and an empty bucket persists explicitly
as unavailable. This coverage measures sample counts, not elapsed-time coverage.
Catalog checksum and algorithm identity accompany the weighted mean, weight
sum, weighted value sum, minimum, maximum, raw value sum and last valid value.

The legacy claim function still returns its original shape and claims only
version 1 jobs. Its writer also rejects version 2 jobs. New claims and writes
are collector-only; their private helpers have no runtime-role or PUBLIC
execute grants. Version 1 host and replication interpretation remains intact.
Existing metric-series, comparison, forecast and `list_metric_catalog` contracts
still use version 1. Separate SQL discovery and baseline reads use version 2.

Replay binds the versioned payload and lease identity. A replacement lease
can accept an identical persisted result after a crash before job completion;
divergent content is rejected without rewriting history. The existing physical
rollup key does not include catalog version. This activation uses SQL keys
absent from version 1; activating overlapping host or replication keys under
another catalog requires an explicit identity design first.

## SQL baseline worker implementation

Migration 0294 adds collector-only baseline input and persistence functions and
daily SQL baseline scheduling. New jobs capture the exact target revision,
28-day UTC window, catalog version, source cutoff and generation. Scheduling
waits until a matching SQL hourly rollup exists for the previous complete day's
last hour, including an explicit unavailable rollup. It inserts at most 896
missing jobs per call and preserves the initial snapshot on repeat calls.

The typed input reader accepts only matching version 2/checksum/algorithm,
generation, empty dimensions and hourly interval. Both source cutoff and
computation time must precede the baseline job snapshot. Its hard bounds are
672 rows and 8 MiB; it does not page or silently truncate. The computation
rejects duplicate, misaligned, foreign-generation or future hourly inputs.
It preserves the existing median, MAD and percentile method over 28 complete
UTC days with at least 80% sample coverage for every hour of an included day.
Hourly means already include elapsed weights for SQL rates. All 168
hour-of-week rows are emitted: missing hours carry null statistics and an
unavailable state, while incomplete history remains partial with explicit
coverage and confidence. Confidence retains the existing v1 formula.

The pinned version 1 baseline table references its ten-entry catalog, so SQL
results use `analytics.sql_metric_baseline` instead of changing that catalog or
weakening its foreign key. Results retain catalog checksum, baseline and source
algorithm identities, generation and snapshot cutoff. This table has forced
row-level security, no direct runtime-role grants, append-only history and UTC
daily partitions registered at persistence. Partition bounds, ownership and
registry identity are verified. The new `sql_metric_baselines` retention policy
defaults to 90 days and participates in the existing scheduled retention
workflow and administrator policy controls. The recovery check below exercises
actual detach/drop under recovery attestation for this parent.

The fenced writer persists all hours atomically, validates their identities and
statistics, and accepts identical replay after lease replacement. Divergent
content, duplicate hours and expired leases are refused. The legacy baseline
writer now accepts only version 1 jobs; existing version 1 results and grants
remain intact. Apply migration 0294 before deploying this worker version.

## Remaining activation work

SQL baselines are available through the explicit version 2 read API, shared
dashboard/Resources chart references and separate MCP tools. Existing jobs,
cursors and host/replication reads retain their original catalog.

Collector source validation on the SQL Server lab, a connected browser and
production fleet throughput remain open. The legacy catalog-v1 scheduler still
selects the first 128 targets before eliminating existing jobs; advancing that
scheduler through the fleet remains separate work. The SQL baseline
retention lifecycle now has a real backup/restore check on isolated PostgreSQL;
production recovery procedures and the Windows lab remain separately unverified.

## SQL baseline read API and chart references

Migration 0295 adds the server-only `reporting.read_sql_metric_baselines`
function. A read selects one exact target revision, SQL metric, 28-day UTC
source window and generation. It returns all 168 hours or no rows, with no
pagination. A snapshot before the last row's computation time returns no
bundle, even if some hourly row timestamps precede it. PUBLIC, collector and
auditor execute grants remain denied, as do direct runtime-role table reads.

`GET /api/v1/observation-targets/{instanceId}/analytics/sql-baselines` requires
`metricKey`, `targetRevision`, `fromUtc` and `toUtc`. The window must be exactly
28 days and end at UTC midnight. `catalogVersion` defaults to 2 and rejects
other versions; `generation` defaults to 1; `snapshotUtc` defaults to the read
time and cannot precede the source window or exceed current time by a minute.
Application authorization checks a read-capable role on this target, and the
five-second read deadline is enforced independently of caller settings.

The response retains target revision, catalog checksum, baseline/source
algorithms, source and read windows, generation, canonical metric unit and
typed hourly results with their cutoff. It distinguishes no stored bundle,
unavailable hours, partial history and complete history. The application and
web client reject mixed metric, window, generation or snapshot identities,
duplicate/incomplete hours, invalid coverage and nonfinite statistics.
The legacy host/replication baseline API remains unchanged.

The shared dashboard and Resources reference hook requests only SQL metrics
present in the selected evidence, at most seven requests, alongside the
existing collector-host memory reference. Each SQL band matches target,
metric, units and UTC hour of week; bytes convert to GiB. Partial history stays
labelled with sample count, coverage and confidence. Unavailable hours remain
gaps. Host and SQL results merge independently so asynchronous completion does
not erase other reference bands. Existing matching logic prevents host memory
references from attaching to SQL memory series. Reads use the exact source
window preceding the selected window; a later read snapshot is a descriptive
comparison and does not prove what was known at the selected Rewind time.

## Validation

The focused `SqlMetricCatalogV2Tests` and `M10AnalyticsContractTests` passed
25 checks with no skips on 2026-09-30. They cover version 1 compatibility,
closed version 2 keys, units and provenance, restart-marker inputs, bounded
sample-gap requirements, read-only definition/input collections and the
independently pinned digest. They do not exercise SQL derivation or prove
database activation.

Nine PostgreSQL integration checks passed with no skips in an isolated
PostgreSQL 18.4 container. Six source tests cover the 291-to-292 upgrade,
equality of both Overview readers before and after extraction, legacy and
partitioned source equality, sixteen independent delta cases, duplicate
timestamps, missing gauge evidence, late source/completion cutoffs, failed
and foreign runs, half-open windows, exact row and byte bounds, scope and
revision refusal, private helper/table refusal and function ownership/grants.
The other three checks exercise existing Overview startup-marker and M10
shadow-reader behavior. The evidence is
`artifacts/sql-metric-analytics-source/sql-metric-analytics-source.trx`.

The separate PostgreSQL 18.4 fresh-install/idempotence check and migration
preamble check also passed. Their evidence is under the same artifact
directory in `sql-metric-source-fresh-install.trx` and
`sql-metric-migration-preamble.trx`. No broad suite was run for this change.

SQL rollup validation adds 21 focused unit checks for weighted aggregation,
missing evidence, invalid inputs, version 1 contracts and migration preambles.
The PostgreSQL selection passed 25 checks initially; its remaining replacement
lease check passed after correcting the disposable fixture's lease timestamps
to respect the database constraint. These 26 checks include all source tests,
the real background worker processing more than one batch, scheduler progress
across 129 targets, unavailable results, scope/revision fences, cadence capture,
weighted persistence/replay and existing job-reclaim behavior. The main run is
`artifacts/sql-metric-rollup-worker/sql-metric-rollup-worker.trx`; the corrected
check is `sql-rollup-replacement-lease.trx` in that directory. No checks skipped.

Three additional PostgreSQL checks passed for fresh-install idempotence, the
292-to-293 upgrade with function ownership/privileges, and the existing version
1 rollup writer's dimension-scoped replay. Their evidence is
`artifacts/sql-metric-rollup-worker/sql-rollup-upgrade-compatibility.trx`.

SQL baseline validation passed nine focused unit checks and eight PostgreSQL
18.4 integration checks with no skips. They cover all 168 hours, full and partial
history, weighted input statistics, unavailable hours, duplicate/identity/window
refusal, snapshot/generation filtering, the actual baseline worker, atomic
persistence and lease replacement replay, upgrade and privilege separation,
registered retention defaults, v1 baseline compatibility, the adjacent SQL
rollup worker and fresh-install idempotence. Evidence is
`artifacts/sql-metric-baseline-worker/sql-baseline-unit.trx` and
`sql-baseline-worker.trx` in the same directory. The focused web analytics
contract checks also passed nine cases, including the new retention policy
class in the bounded update client. At that checkpoint, SQL baseline reads
were internal to the worker; those checks do not prove API, MCP or dashboard activation.

The strengthened catalog/snapshot check also passed separately after seeding
version 1 rows with an otherwise matching SQL key and window. The reader
excluded them. Evidence is `sql-baseline-catalog-snapshot.trx` in the same
artifact directory.

SQL baseline read activation passed three isolated PostgreSQL 18.4 checks for
production port/service reads, all hourly identities, mixed-grant denial,
revision refusal, version/generation separation, function grants, atomic
snapshot visibility and fresh-install idempotence. The real HTTP pipeline
check passed for serialization, target-role refusal, invalid catalog/key/
window rejection and server-failure classification of malformed repository
statistics. Migration preamble validation passed. Evidence is under
`artifacts/sql-baseline-read-api` in `sql-baseline-read-postgres.trx`,
`sql-baseline-http.trx` and `sql-baseline-read-preamble.trx`.

Thirteen focused web checks passed for SQL client identity binding, source
windows, all-hour completeness, units, percentiles, partial/unavailable history
and reference matching, together with existing host/rule/chart rendering
behavior. TypeScript typechecking passed. These checks exercise the API,
PostgreSQL and web layers independently, not a browser connected to the SQL
Server lab or a complete MCP flow. No broad suite was run for this checkpoint.

The SSH VM is reachable and its PostgreSQL 18 service is running, but this
session has no configured PostgreSQL login for that VM. These checks ran
against the isolated local container. The Windows SQL Server lab remains
necessary to confirm actual collector inputs; that source validation and
production fleet performance are not proven by these tests.

## MCP SQL discovery and baseline reads

`list_sql_metric_catalog_v2` is a no-argument metadata tool for the seven
activated SQL baseline keys. It returns the full catalog-v2 checksum, canonical
units, derivation versions, comparable-sample requirements and the baseline
tool name. This subset does not expand `list_metric_catalog` or the v1 series,
comparison and forecast tools. Metadata requires an active Viewer, Operator or
TargetAdministrator grant and is audited without a target ID.

`get_sql_metric_baselines_v2` calls the same `SqlMetricBaselineQueryService`
as the read API. Required inputs are instance ID, current target revision,
SQL metric key and an exact 28-day source window ending at UTC midnight.
Generation defaults to 1 and the publication snapshot defaults to now. All
timestamps use UTC ending in Z. Revisions and generations retain Int64 bounds.
Authorization binds the read role to this target before repository access.

The result includes revision, generation, unit, catalog/checksum, source and
baseline algorithm versions, source window and publication snapshot. A missing
or unpublished bundle returns `no_data` with no rows. A published bundle returns
all 168 UTC hours together, retaining unavailable hours and nullable statistics.
Sunday 00:00 is hour 0. Each hour includes its source cutoff, complete days,
sample count, median, MAD, p10/p90, coverage, confidence and visibility. This
bounded read has no limit or cursor and needs no signing key. Corrupt repository
statistics fail without returning the payload; missing audit also withholds it.

The discovery catalog now has 32 tools. Its approved identity is
`ABEFA5ADA97EFDA91714112AD9F6FCDB97A6159FF537C43C4D2B0674FEB3E7F3`.
Runtime, stdio bridge, release contract/schema, their checksums and certification
pins change together. Closed output schemas use explicit field mapping; no
query text, plan XML, credentials or administrative action is added.

Focused verification passed: 62 MCP contract checks, three additional boundary
cases for Int64 identities and audit withholding/target handling, release
catalog integrity, and official SDK discovery under both supported protocols.
Three PostgreSQL 18.4 checks exercised the real server pipeline through the
official SDK: every tool with and without a cursor signer, plus a persisted
168-hour SQL baseline, knowledge-cutoff and generation misses, mixed Viewer/Auditor
scope denial and exactly one audit row per call. Results are under
`artifacts/sql-baseline-mcp`. These are isolated PostgreSQL tests with synthetic
source rows; they do not validate the Windows SQL Server collector or browser.

## SQL baseline retention and recovery verification

The focused `SqlBaselineRetentionRequiresVerifiedRecoveryAndKeepsFencesHoldsAndPolicyReattachment`
test writes four 168-hour bundles through the real fenced SQL baseline writer
and completes their jobs through the repository adapter. One source window
ended 100 days ago; three newer partitions satisfy the default retention floor.
The bundles explicitly preserve unavailable statistics, rather than manufacturing
observations to pass the test.

The test takes a full custom-format `pg_dump` archive of its owned repository
database, computes its SHA-256 digest, and restores it with `pg_restore
--exit-on-error` into a fresh isolated database. It compares every stored SQL
baseline field and reads the restored old bundle through the server-role
application service. Only after that verification does it record a recovery
attestation through the normal server repository mutation, binding its digest
and reference to this archive. An identical mutation replays without duplication.

The default retention worker refuses to detach without the attestation. It
also honors overlapping analytics jobs, active reader leases, incomplete
backfill and the minimum partition floor, while a job in another window does
not block this partition. A stale worker fence is refused. Detach keeps the
168 rows in the physical child during the 24-hour grace period and hides them
from the baseline API; disabling the policy reattaches the child and restores
the exact original rows.

After resuming the policy, fixture-only advancement of the grace deadline
avoids waiting a day. An active reader still blocks deletion. A dependent view
causes a bounded retry without losing the child. Removing that view and
appending an elapsed retry permits the worker to drop the old partition,
retain the three newer partitions and record the successful retention audits.
The same pre-deletion archive then restores the deleted baseline rows into a
second fresh database, with identical content and a readable 168-hour bundle.

This check passed on digest-pinned PostgreSQL 18.4. Its terminal result is in
`artifacts/sql-baseline-retention/sql-baseline-retention.trx`. It exercises real
backup and restoration within the same isolated PostgreSQL cluster, whose
repository roles already exist. It is not a fresh-cluster or Windows-lab disaster
recovery certification, and it does not cover production backup storage,
encryption, off-site copies or fleet volume. No production migration was needed.
The GitHub PostgreSQL job is configured to expose its service container ID to
this test; that remote job has not been executed in this session.

## SQL baseline fleet benchmark

The opt-in `SqlBaselineFleetBenchmarkDrainsEveryTargetAndPreservesFullAndPartialHistory`
check passed on isolated PostgreSQL 18.4 with 129 targets and all seven SQL
metrics. It seeds 603,288 canonical hourly rollups: 28 complete UTC days for
128 targets and seven days for the final target. The fenced baseline scheduler
inserts 896 jobs, then seven, then zero on successive calls, crossing its
128-target batch boundary without duplicates or omissions.

The real derivation worker completed all 903 baseline jobs in 94.89 seconds
(9.52 jobs/second), with at most two running at once. It persisted 151,704 rows,
168 unique hours for every target/key pair. Every median, MAD and percentile
matches the seeded series. The target with seven days retains 25% coverage
and partial visibility; it does not acquire invented complete history.

Seeding took 18.05 seconds and the three scheduling calls took 0.105 seconds.
Leaf tables and indexes occupied 468,418,560 bytes for inputs and 197,836,800
bytes for baselines. The whole test process peaked at 164,851,712 bytes of
working set and allocated 10,104,679,200 bytes during worker execution. Those
memory measurements include fixture/test activity and are not worker-only
profiles. The machine exposed 24 logical processors and used .NET 10.0.7.

This is a fixed synthetic baseline backlog, not a production capacity claim.
Collection is disabled after jobs are scheduled, and input rollups are seeded
rather than collected from SQL Server. The worker may also schedule legacy-v1
analytics; the check stops once the 903 SQL baseline jobs finish and does not
assert that the entire analytics queue is empty. No source collector, browser,
concurrent ingestion or production retention load is covered. Evidence is
`artifacts/sql-baseline-fleet/sql-baseline-fleet-benchmark.trx`. CI excludes this
benchmark from normal functional runs and offers it as a manual workflow input.
