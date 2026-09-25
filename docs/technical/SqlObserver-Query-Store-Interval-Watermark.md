# Query Store interval watermark contract

Status: implementation design, 2026-09-25. The existing M7 collector still
persists cumulative `query_store_interval` observations and per-plan wait totals.
This document defines the cutover needed before either is treated as an additive
time series or used for plan-regression detection.

## Observed problem

The collector reads Query Store every 30 seconds over an overlapping window.
Within an active Query Store interval, the same plan's execution and wait totals
are cumulative. Consecutive collection runs therefore can contain the same work.
The Queries UI currently labels wait values as interval totals and warns that
snapshots overlap; summing those rows would be wrong.

The disposable SQL Server 2022 probe in
`tools/lab/verify-query-store-text-local.ps1` held a table lock, recorded a
3,042 ms lock wait for one plan, then read the pinned wait asset again without
running that plan. Both reads returned the same total. The source rows grouped
to interval ID 1, execution type 0, category 3, totaling 3,042 ms. The probe
removes its database in `finally`. Microsoft documents that active intervals
can have separate flushed and in-memory rows; the source query must aggregate
these by `(plan_id, runtime_stats_interval_id, execution_type)` for runtime
stats and additionally `wait_category` for waits.

A second disposable SQL Server 2022 run called
`sp_query_store_reset_exec_stats` for one workload plan, then executed that
same plan until its count exceeded the pre-reset count. In the same interval
ID, the count grew from 2 to 6, while the earliest `first_execution_time`
after reset was later than the previous `last_execution_time`. A counter-only
comparison would invent a positive delta for this reset. The earliest
execution time is a candidate reset epoch: a quiet flush left that time and
the count unchanged. The probe uses a 1440-minute
interval to keep both reads in the same source group and removes its database
even on assertion failure.

- [Query Store runtime stats](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-query-store-runtime-stats-transact-sql)
- [Query Store wait stats](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-query-store-wait-stats-transact-sql)

## Source and identity

The next versioned SQL assets should emit bounded, complete groups rather than
one rolling-window sum per plan. Each runtime group needs database identity,
query and plan identities, `plan_id`, interval ID and UTC start/end, execution
type, execution count, and the bounded weighted CPU/duration/read/write/row
totals. It must also emit the group's earliest `first_execution_time` and latest
`last_execution_time`; the earliest value is a candidate counter epoch. Each
wait group adds category and wait milliseconds. Aggregate source
rows for the active interval before applying row limits. A plan's group must not
be partially returned: if the source cap is reached, mark the collection
truncated and leave omitted groups' watermarks unchanged. Query Store may reuse
local numeric IDs after a clear; an interval ID alone is never an identity.

Watermark identity is target revision, stable database identity, opaque query
and plan fingerprints, plan initial-compile time, interval start/end, execution
type, and metric kind (runtime or wait category). The current opaque plan
fingerprint hashes `plan_id`, so it alone cannot distinguish an ID reused after
Query Store is cleared. Source numeric IDs are transient lookup aids and must
not be exposed by the API. Where a stable database GUID is unavailable, a
changed database incarnation must start a new baseline rather than compare
counters across incarnations.

## Atomic comparison and publication

Persist the previous cumulative values in a target-scoped repository table.
Compare and update them under the existing fenced collector lease in the same
transaction that commits the run and any derived delta rows. Replaying the same
run and digest must leave both watermarks and published deltas unchanged. A
failed or partially validated source read must advance neither.

For a matching group with nondecreasing counters, publish the component-wise
difference. Preserve a known zero as zero. When no prior complete snapshot is
available, publish `baseline_unavailable`, with a null delta, while recording
the first cumulative values. A decrease, Query Store reset, changed database
incarnation, or incompatible target revision starts a new baseline and emits
explicit reset evidence; never clamp a negative difference to zero. If a wait
category was absent in a **complete** previous group, its previous value is
known zero; absence in a truncated or failed read is unknown.

SQL Server can reset execution stats for an existing plan between polls. A
decrease is detectable, but a reset whose new count grows beyond the prior
count is not detectable from two cumulative values alone. The local reset
probe showed `first_execution_time` advancing even when the count refilled
above its old value. A source-group comparison should therefore start a new
baseline if the earliest execution time advances past the prior group's
latest execution time, even when every counter is nondecreasing. The same
reset experiment and grouping must still be validated on SQL Server 2019 and
2025, and the behavior of flushes and other reset paths needs proof before
active-interval deltas are marketed as exact. Closed-interval publication is
an alternative, but it needs a flush/late-correction policy before it can be
called exact.

Use one fixed source cutoff for the runtime and wait reads and record their
individual coverage. They are separate SQL statements and cannot be assumed to
be one instantaneous SQL Server snapshot. The repository must not infer a wait
delta from runtime coverage, or the reverse. Source windows and collection
timestamps must remain available with each derived row.

## API and migration cutover

Keep historical `query_store_interval` rows and current plan-wait snapshots
under their existing cumulative semantics. Introduce a distinct delta
semantics value and read shape; do not silently reinterpret old rows. Ranking,
charts, forecasts and future plan-regression rules may aggregate only complete
comparable deltas. A baseline/reset/truncated row remains visible as evidence
but does not contribute a zero-valued sample.

Use forward migrations for watermark and delta storage, target-bound RLS,
fenced writes, read grants and catalog pins. The high-volume query-performance
tables and new watermark/history tables need a retention design before a fleet
rollout; the current unpartitioned M7 tables are not a suitable 90-day default.
The source row cap, byte cap, database identity, replay digest, and upgrade
path from the existing cumulative schema must be tested before enabling the
new semantics in the UI.

Required focused proofs: two quiet reads yield one baseline and a zero delta;
new work in the same active interval yields only the increment; flushed plus
in-memory rows are summed once; a new interval starts its own baseline; reset,
database recreation, truncation, lease loss and divergent replay do not
publish invented deltas; target A cannot read target B's state. A disposable
native SQL Server probe and PostgreSQL transaction/replay tests are both
necessary, because neither source-only nor repository-only evidence proves
the full path.
