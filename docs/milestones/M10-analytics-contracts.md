# M10 analytics and retention contracts

The analytics slice is deterministic and target/revision scoped. All reference
times are explicit UTC inputs; no algorithm reads the local machine clock or
timezone. `MetricCatalogV1`
publishes a checksum-pinned catalog; `RollupV1` uses half-open UTC five-minute,
hour, and day buckets, preserves nulls for missing data, and records counter
resets, gaps, truncation, expected samples, coverage, source cutoff, and
algorithm version. Dimensions are sorted by ordinal key and hashed with
SHA-256.

Baseline-v1 consumes complete prior UTC days only, with a cutoff derived from
the supplied UTC reference time. Forecast-v1 uses a bounded Theil–Sen slope,
clamps estimates to capacity, then revalidates finite values, horizon,
confidence, and ordered bounds; invalid results are unavailable. It refuses
unknown/non-positive capacity, reset/shrink segments, insufficient points, or
low confidence. It never infers a path or volume. Evidence-v1 applies a fixed [-15 minute,+5 minute) window, typed
allowlisted references, tombstones, source cutoffs, deterministic identity,
and a 256-reference cap. Incident-v1 sessionizes in deterministic order with
15-minute idle and six-hour/256-item hard limits.

Worker limits are 100,000 rows, 8 MiB, 45 seconds, concurrency two, and at most
90 daily backfill days. Replay identities reject divergent request digests.
Retention remains disabled/null by default; execution requires a global
SecurityAdministrator and repository-side recovery, lease, registry, backfill,
dependency, floor, and UTC checks before detach, a 24-hour grace period, and a
fresh lease check before drop.

The Server analytics routes and the Collector's derivation/backfill ownership
are also verified in-process. API coverage exercises target authorization,
request bounds, opaque cursor terminal pages, cancellation propagation, and
the 1 MiB response envelope; Collector composition verifies that analytics
workers resolve only the restricted repository ports. These are local runtime
contracts, not Docker, live SQL Server, or release certification.

The PostgreSQL adapter calls fixed migration-0014 SECURITY DEFINER functions for
rollups, baselines, forecasts, evidence, and incident rows. Writes are fenced by
target scope, target revision, worker lease, replay digest, and row/byte limits;
reads are repository-only and never open a target connection. Policy updates use
an optimistic revision and append-only history. Docker, SSPI/WMI, and live target
certification remain outside this milestone's local gates.
