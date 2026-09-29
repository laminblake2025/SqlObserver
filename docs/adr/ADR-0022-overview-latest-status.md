# ADR-0022: Precompute latest Overview status per target

Status: proposed, 2026-09-29

## Context

`OverviewQueryService` currently composes health, alerts, blocking, waits,
database state, TempDB, backups, Agent, availability groups, deadlocks, and
several histories for every selected target. It limits concurrent source reads
to eight, but a fleet request still scales with the number of targets and
source projections. The same route serves the fleet page and a server
dashboard. A selected time window changes some of its results, while other
values describe the latest observation regardless of that window. The
September 2026 repository review recommends a precomputed latest-status table.

The existing response carries source states and gaps. A faster read must not
turn missing, stale, truncated, or unauthorized evidence into a healthy zero.
There is no common collection instant across the independent sources, so a
precomputed row cannot honestly claim to be a synchronized SQL snapshot.

## Decision

Create a bounded, persisted **latest-status projection per target revision**.
A repository-side materializer reads committed projections and publishes one
replacement row atomically. It records a publication time and separate source
observation times, states, and coverage. A failed or incomplete source keeps
an explicit unavailable/partial state; the materializer never fills it with
zero. Publication is fenced by the current target revision and a monotonically
increasing generation so an older attempt cannot overwrite newer evidence.

The row contains only current evidence:

| Source | Precomputed output |
| --- | --- |
| Core health | Collection state, last success, five bounded SQL resource values, and their observed times. |
| Active alerts | Exact count by state and at most five ranked issue summaries; a separate completeness state if an exact count cannot be obtained. |
| Current blocking | Distinct blocked-session count from one coherent activity run, its completion time, loss state, and one issue summary. |
| Waits | At most five non-idle comparable deltas with source run and completeness state. |
| Database state | Bounded critical-state issues and an explicit scan-completeness state. |
| TempDB, backups, AG | Current resource/issue summaries with each source's observed time, state, and truncation. |

This is a read model, not a second source of truth. It stores no raw diagnostic
text, plans, connection secrets, or user-specific authorization result. A
bounded JSON representation for issues and resources is acceptable only with
versioned validation at write and read boundaries and byte/count caps; stable
counts, revision, generation, source states, and timestamps remain typed
columns. The projection must not keep every prior generation indefinitely.

Deadlocks, Agent failures, workload and database histories, blocking history,
and six metric series depend on `fromUtc`/`toUtc` and stay on the windowed read
path. Host metric **latest status** may be added later as a separate current
source; the existing host resource values are picked from the requested window
and must not be silently relabelled as current. Issue ranking merges latest and
windowed issues after both paths finish and preserves the existing ten-item
response bound. Disabled or retired targets keep the existing explicit
disabled response and do not expose an old cached row.

Authorization remains at two boundaries. `OverviewQueryService` first builds
an inventory limited to Viewer, Operator, or TargetAdministrator grants and
checks the selected target before any evidence read. The new repository read
accepts an explicit target ID and revision, sets the matching target scope,
and uses a server-executable, target-claim-checked PostgreSQL function. It
returns no row on a revision mismatch. Fleet reads may batch the already
authorized IDs, but must never return or count targets outside that set. No
cache key or stored row includes a caller's role union as a substitute for
target-specific authorization.

Each source has its own freshness rule. Reading a row recomputes stale state
against the request cutoff; a recent publication time does not make an old
source current. A missing row or expired publication is an explicit gap. A
materializer failure leaves the last row readable with its old publication
time and a visible stale/unavailable state. The response's `RefreshedAtUtc`
continues to mean request cutoff, not source collection time.

## Delivery and verification

1. Add a forward migration for the private table, forced row-level security,
   restricted writer, and target-scoped reader. Keep grants limited to the
   collector/materializer and server identities. Add a catalog test for owner,
   privileges, RLS, and security-definer settings.
2. Implement an idempotent, leased materializer with a bounded fleet batch.
   Read committed source generations in one repository transaction and fence
   publication by target revision and generation. A lagging or failed target
   must not delay other targets. Expose materialization age and failure counts
   in operational health.
3. Introduce an application port for latest status and merge it with the
   existing windowed reads. Start with an opt-in server route switch, retaining
   the current composition as rollback. Compare the two paths on populated
   fixtures, including more than 4,000 alerts, blocking loss, stale source
   times, no-backup databases, disabled targets, and mixed role grants.
4. Measure p50/p95 endpoint latency and repository calls at one and 100
   targets on the lab database. Enable the new path only after response parity
   for current evidence and a demonstrated reduction in fleet read cost.
   Windowed reads still need separate set-based aggregation for large fleets;
   this latest-status change alone does not establish a fleet latency target.

Migration 0235 stages step 1's table and restricted functions. It leaves the
existing route untouched. A typed application read port and PostgreSQL adapter
now validate the target and JSON boundary. Migration 0236 and the collector
worker publish core health, active alerts, and coherent current blocking from
committed repository data under a lease. The due-target list records attempts
so a repeatedly failing target cannot monopolize its bounded batch. Waits,
TempDB, backups, and AG remain explicitly unavailable in this row. Migration
0237 adds up to five critical database-state issues from the current inventory
run, marks a longer issue list partial, and withholds issues when inventory
collection is not current. Other source materialization, full parity work, and
the read switch remain to be built.

The read adapter accepts the request's UTC cutoff. A publication older than
two minutes makes its current states stale, preserves observed counts and
timestamps, and removes cached issues from the actionable list. Alert and
blocking observations also expire independently after two minutes, even when
the row was published recently. Their stale issues are withheld while other
current issues remain visible. Health freshness otherwise follows the
collector-health projection state; its collection interval is not assumed to
be the alert or blocking interval.

Rollback switches reads to the existing composition while leaving the new
table and writer inert. A later migration can remove them after verification;
no applied migration is edited.
