# ADR-0027: Version SQL metric analytics inputs

Status: proposed, 2026-09-30. Catalog definitions, source, SQL rollup and baseline workers implemented; API activation pending.

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
design, and fleet throughput has not been benchmarked.

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
Existing API and MCP catalog discovery still uses version 1.

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
workflow and administrator policy controls. Actual detach/drop under recovery
attestation has not been exercised for this new parent in these checks.

The fenced writer persists all hours atomically, validates their identities and
statistics, and accepts identical replay after lease replacement. Divergent
content, duplicate hours and expired leases are refused. The legacy baseline
writer now accepts only version 1 jobs; existing version 1 results and grants
remain intact. Apply migration 0294 before deploying this worker version.

## Remaining activation work

SQL baselines are not yet available through the API, MCP or dashboard.

1. Add version-aware application/API reads and catalog discovery, then MCP schemas and
   their approved digest, using an explicit active version. Resume existing
   jobs and cursors under their original catalog, refusing version changes.
2. Bind UI references to matching keys and units after read API activation.
3. Validate the SQL baseline parent's retention lifecycle with real recovery
   attestation, and benchmark fleet derivation and history coverage.

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
class in the bounded update client. SQL baseline reads remain internal to the
worker; these checks do not prove API, MCP or dashboard activation.

The strengthened catalog/snapshot check also passed separately after seeding
version 1 rows with an otherwise matching SQL key and window. The reader
excluded them. Evidence is `sql-baseline-catalog-snapshot.trx` in the same
artifact directory.

The SSH VM is reachable and its PostgreSQL 18 service is running, but this
session has no configured PostgreSQL login for that VM. These checks ran
against the isolated local container. The Windows SQL Server lab remains
necessary to confirm actual collector inputs; that source validation and
production fleet performance are not proven by these tests.
