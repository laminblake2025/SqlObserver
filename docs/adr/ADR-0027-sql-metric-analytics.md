# ADR-0027: Version SQL metric analytics inputs

Status: proposed, 2026-09-30. Catalog definitions and repository source implemented; analytics worker activation pending.

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

## Activation work

The new definitions have no production callers. SQL baselines are not yet
available through the API, MCP or dashboard. Activation requires the following
coordinated changes:

1. Add forward migrations for versioned catalog metadata and connect the
   repository source to a typed worker adapter. Preserve version 1 reads and
   grants, forced row-level security, target-revision fences and job snapshot
   cutoffs. Do not add SQL entries to the unversioned catalog used by v1 reads.
2. Persist the selected catalog version in new jobs, results and replay
   identities. Resume existing version 1 jobs and cursors against version 1;
   reject attempts to resume a cursor under a different catalog.
3. Carry derivation validity and elapsed weights into version 2 rollups,
   preserving missing evidence, expected coverage and generation identity.
   Keep baseline computation scoped to matching versions, metrics, dimensions
   and target revisions.
4. Update application validation and catalog discovery, then MCP schemas and
   their approved digest, using the same active version. Bind UI references to
   matching keys and units only after the source and worker are active.

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

The SSH VM is reachable and its PostgreSQL 18 service is running, but this
session has no configured PostgreSQL login for that VM. These checks ran
against the isolated local container. The Windows SQL Server lab remains
necessary to confirm actual collector inputs; that source validation and
versioned worker/result persistence are not proven by these tests.
