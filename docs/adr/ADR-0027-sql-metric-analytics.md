# ADR-0027: Version SQL metric analytics inputs

Status: proposed, 2026-09-30. Catalog definitions implemented; production activation pending.

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

## Activation work

The new definitions have no production callers. SQL baselines are not yet
available through the API, MCP or dashboard. Activation requires the following
coordinated changes:

1. Add forward migrations for versioned catalog metadata and a bounded SQL
   source adapter. Preserve version 1 reads and grants, forced row-level
   security, target-revision fences and job snapshot cutoffs.
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

Use the SSH test VM's PostgreSQL for source-adapter behavior and persistence
tests when that integration is implemented. Exercise restart, counter reset,
missing inputs, scheduler-count change, two-minute boundary, unequal sampling
intervals, snapshot cutoff, revision changes, RLS and page continuation without
duplicates or gaps. The Windows SQL Server lab is needed to confirm actual
collector inputs. Neither VM integration test has run for this catalog-only
change.
