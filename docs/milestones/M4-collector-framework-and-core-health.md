# M4 collector framework and core health

## Scope

Milestone 4 introduces the first reusable collection data plane and three ordered passive collectors:

1. `engine.core` records cumulative SQL Server engine and memory counters;
2. `database.inventory` records bounded database identity, state, compatibility, access, and recovery evidence; and
3. `database.files` records logical-file capacity, growth, and cumulative I/O evidence without collecting physical paths.

The existing M3 `capability.connection` worker remains the prerequisite control-plane probe. The M4 scheduler will not execute a collector without a current capability profile for the exact target revision and the manifest's version, platform, edition, capability, and permission requirements.

## Immutable collector contracts

The v2 manifest schema adds dependencies, supported engine editions, connect and command deadlines, resilience policy, and a closed output kind. The three manifests and nine version-selected SQL statements are LF-only embedded resources pinned by `m4-core-health.assets.sha256`. The runtime parser rejects unknown fields, unchecked assets, runtime SQL overrides, unsupported target versions, unbounded queries, target mutations, and physical file paths.

SQL Server 2019 uses the M3 `server.view-state` evidence. SQL Server 2022 and 2025 use `server.view-performance-state`. All queries are parameterized, deterministically ordered, cancellable, and limited by both row and byte contracts. They use documented SQL Server catalog views and dynamic management views and do not modify the target.

## Scheduling, resilience, and visibility

PostgreSQL repository time determines due work, circuit state, and the next deadline. A bounded scheduler acquires a renewable, fenced lease for each target/collector pair and also prevents local overlap. Execution uses the original command deadline across at most two attempts; retry delay does not extend it. Three consecutive transient failures or timeouts open a durable five-minute circuit.

Every due item has a visible disposition. Successful, partial, unsupported, denied, failed, timed-out, and invalid-output runs carry explicit accounting and loss evidence. Sample loss is never inferred away: exact and conservative loss counts are persisted as append-only visibility gaps. Missing collection history projects as pending, stale, unavailable, unsupported, or disabled evidence rather than healthy-by-absence.

Catalog reconciliation, run begin, output ingestion, outcome recording, schedule advancement, and circuit transition are revision- and lease-fenced. A committed run atomically persists its output and outcome. Exact replays are idempotent; divergent run or sample identities fail closed. The repository stores no collector SQL and grants runtime roles only bounded function/view access.

## Health surfaces

The Server exposes role- and target-scoped, bounded read models for instance, database, and file health. Responses contain closed enums, UTC timestamps, stable identifiers, normalized metrics, explicit freshness/state, and paging cursors. They exclude connection details, provider messages, physical paths, arbitrary SQL, and secrets. The React health view renders these safe projections and never translates missing evidence into a success state.

## Assumptions and qualification boundary

- M4 retains Windows integrated target authentication and validated TLS from M3.
- The development SQL Server Express instance supplies functional passive-query evidence, not SQL Server 2019/2022/2025 or Windows Server certification.
- Repository integration tests use the pinned PostgreSQL 18.4 image and exercise migration, privileges, leases, replay, revision conflicts, loss, and projection bounds.
- gMSA, Kerberos/SPN, trusted production certificates, sustained-load qualification, installation, and support-matrix certification remain M12 gates.
- Sessions, requests, waits, blocking, deadlocks, query performance, alerts, and later diagnostic surfaces remain ordered later milestones.
