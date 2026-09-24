# Milestone 2: PostgreSQL repository foundations

- Status: Implemented repository slice; release certification is not claimed
- Repository engine: PostgreSQL 18.x only
- Governing decisions: [ADR-0002](../adr/ADR-0002-postgresql-repository.md),
  [ADR-0004](../adr/ADR-0004-sql-first-migrations.md),
  [ADR-0008](../adr/ADR-0008-partitioning-and-retention.md), and
  [ADR-0012](../adr/ADR-0012-postgresql-worker-leases.md)

## Scope

M2 establishes the repository substrate needed before a production collector exists:

- nine least-privilege schemas: `control`, `security`, `telemetry`, `events`,
  `analytics`, `alerting`, `reporting`, `audit`, and `system`;
- immutable, ordered, LF-normalized SQL migrations with an external SHA-256 manifest
  and an in-database applied-migration ledger;
- NOLOGIN group roles for migrator, server, collector, and auditor access;
- a minimal observation-target identity that intentionally contains no endpoint,
  credential, or connection string;
- daily native range partitioning for raw metric samples and monthly native range
  partitioning for diagnostic events;
- time BRIN indexes and the measured primary per-instance/time B-tree access path;
- the typed raw-metric column order used by bounded PostgreSQL binary `COPY`;
- protected query-text/execution-plan deduplication using ciphertext, nonce,
  authentication tag, external key identifier, and an opaque 32-byte fingerprint;
- an append-only audit activity envelope with bounded, allowlisted safe metadata;
- a partition registry and retention policies that are disabled and unconfigured by
  default, plus a read-only retention preview;
- concurrency-safe partition functions and repository-clock fenced worker leases.
- bounded typed replay-validation functions that detect divergent metric/event
  identities without granting the collector broader table reads.

M2 does not add a production SQL Server collector, target query, credential, ORM schema
generator, automatic retention executor, database scheduler, message broker, or MCP
database path. The `analytics`, `alerting`, and `reporting` schemas are intentionally
created before their later milestone objects so their privilege boundaries are stable.

## Assumptions

The installation starts with a dedicated empty database on PostgreSQL 18.x. Migration
`0001` runs once under a controlled bootstrap LOGIN that can create roles and schemas;
it creates the four cluster-wide group roles and grants the bootstrap LOGIN membership
in `sqlobserver_migrator`. A same-name role is reused only when every safety attribute
already matches and it is not a member of another role; the migration never rewrites
an unrelated pre-existing role. Service LOGIN roles and all authentication material
are provisioned outside the repository and receive only one appropriate group role.

The migration runner, not the SQL file, owns one transaction per migration. It sets a
bounded command timeout, executes the complete file, inserts the verified filename and
checksum into `system.schema_migration`, and commits both changes atomically. Every
file uses `SET LOCAL`, so executing it as unrelated autocommit statements is invalid.
The runner must reject a PostgreSQL major version other than 18 before applying any
file and must also honor the server-side assertion in migration `0001`.

Persisted instants are `timestamptz`, which represents absolute instants independent
of display timezone. Migrations and programmable functions explicitly select UTC;
clients must send typed UTC instants and set UTC for operational display and tests.
Partition dates are accepted only in `[2000-01-01, 2100-01-01)`, a deliberate bound
covering supported SQL Server history while preventing arbitrary identifier ranges.

## Migration and provenance policy

`database/migrations/*.sql` is the only production DDL authority. Filenames are a
gap-free four-digit sequence and `checksums.sha256` contains exactly one lowercase
SHA-256 followed by two spaces and the filename for every migration. Checksums are
computed from committed LF bytes. A released file and its historical checksum are
never edited; repair uses a new numbered forward migration.
The catalog and ledger can hold the full four-digit sequence (up to 9,999
migrations); each apply request still returns at most 256 migration results.

Files under `database/functions` and `database/views` are non-deployable review indexes
pointing to the matching numbered migration, not a runtime discovery or execution
path. The complete definitions are not duplicated. There is no automatic schema
generation or startup-time schema repair.

These SQL assets were authored clean-room from the SqlObserver plan, accepted ADRs,
domain contracts, and documented PostgreSQL 18 primitives. They contain no copied
incumbent product source, reverse-engineered behavior, monitored-target SQL, customer
data, credentials, or production connection material.

## Role and privilege matrix

All four repository roles are NOLOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
NOINHERIT, NOREPLICATION, and NOBYPASSRLS. They are groups to which separately managed
LOGIN identities may be granted membership. `PUBLIC` loses repository schema access,
database-wide default object rights, and function execution. No application role gets
schema `CREATE`.

| Capability | Migrator | Server | Collector | Auditor |
| --- | --- | --- | --- | --- |
| Apply reviewed DDL and own objects | Yes | No | No | No |
| Read target identity and observation parents | Owner | Yes | Target only | No |
| Insert raw samples and diagnostic events | Owner | No | Yes; SELECT is limited to returned time/ID columns for retry accounting | No |
| Create transaction-local binary-COPY staging | Owner | No | Yes | No |
| Insert/deduplicate protected ciphertext | Owner | No | Column-bounded insert and fingerprint lookup | No |
| Read protected ciphertext | Owner | No | No | No |
| Invoke partition, lease, and replay-validation functions | Owner | No | Yes | No |
| Append audit records | Owner | Column-bounded insert | Column-bounded insert | No |
| Read audit records | Owner | No | No | Yes |
| Preview retention | Owner | Yes | Yes | No |

Column-bounded audit INSERT excludes `occurred_at`, forcing repository time. No server
or collector UPDATE/DELETE privilege is granted on audit records. Protected payload
access remains fail-closed for the server until the authorization and query-diagnostic
milestones add a reviewed retrieval service.

## Partitioning and ingestion

`telemetry.raw_metric_sample` has no default partition and routes on `observed_at` to
`raw_metric_sample_pYYYYMMDD`. `events.diagnostic_event` routes on `occurred_at` to
`diagnostic_event_pYYYYMM`. Missing partitions make ingestion fail visibly. The only
runtime DDL authority is the fixed-purpose pair:

- `control.ensure_daily_metric_partition(date)`;
- `control.ensure_monthly_event_partition(date)`.

Each function accepts a typed date, derives a fixed-length name, quotes identifiers and
UTC bounds, takes a transaction-scoped advisory lock for that exact parent/boundary,
cross-checks the partition registry and `pg_inherits`, and fails closed on catalog
drift. Callers cannot supply a schema, table, identifier, or SQL fragment. Creating a
child of the already indexed parent creates matching child indexes: BRIN supports
large append-oriented time scans; `(instance_id, time DESC)` supports the primary
bounded instance-history query. Additional indexes require measured query evidence.
Each call returns the attached relation, an advisory-lock-protected `created` flag, and
the captured repository time, so concurrent callers cannot miscount existing work.

Raw metric writers use typed, bounded binary `COPY` into transaction-local temporary
staging in the column order documented in [the database README](../../database/README.md),
then perform a fence-protected, conflict-safe insert into the parent. `TEMPORARY` is
granted only to the collector group; staging is `ON COMMIT DROP`. Writers create the
UTC day first and surface missing partitions, constraint rejection, truncation, and
sample loss. This migration does not hide an unbounded fallback partition.

The persistence identities are `(observed_at, sample_id)` for metrics and
`(occurred_at, event_id)` for events. An exact replay of caller-owned content is counted
as a duplicate. After PostgreSQL has arbitrated each unique constraint, the adapter
passes bounded typed arrays to collector-only SECURITY DEFINER validators. Those
functions have fixed search paths, read only schema-qualified parents, and do not
inspect caller-controlled temporary relations or expand the collector's table reads.
A missing or divergent identity returns false and rolls back the entire batch,
including otherwise insertable rows; malformed or oversized arrays fail with SQLSTATE
`22023`. Metric `collected_at` is assigned by the repository and is not replay content,
while caller-supplied event `collected_at` and the fixed severity/metadata envelope are
compared.

## Sensitive payloads and audit

`security.protected_diagnostic_payload` can represent `query_text` or
`execution_plan`. The application encrypts before persistence and supplies a bounded
algorithm name, external protected-key identifier, nonce, authentication tag,
ciphertext of at most 1 MiB, and an opaque 32-byte fingerprint. A unique constraint on
kind and fingerprint provides deduplication. The schema has no plaintext column and
contains no key material. Equality fingerprints and length metadata still reveal some
information, so access remains restricted and transport/storage encryption is still
required.

The application computes the deterministic cryptographic fingerprint before this
boundary, while encryption may be randomized. A kind/fingerprint match therefore uses
immutable first-committed-write semantics: exact retries and concurrent alternative
protected representations return one identifier without updating nonce, tag,
ciphertext, algorithm, or key identifier. PostgreSQL constraints independently reject
out-of-contract nonce, authentication-tag, and ciphertext sizes. A suspected digest
collision must be investigated outside the repository; it never authorizes overwrite
or plaintext comparison here.

The domain taxonomy reserves six additional untrusted-content kinds for later
collector milestones. M2's repository adapter rejects those kinds before SQL rather
than persisting an undocumented mapping; a forward migration and owning collector
tests must add each mapping deliberately.

`audit.activity` is an append-only envelope for later administrative-write and MCP
auditing. Actor, action, authorization, outcome, correlation, safe digests, duration,
response size, and bounded JSON metadata are supported. Unrestricted response data,
query text, plans, tokens, keys, and connection strings are prohibited from audit
fields even when encrypted elsewhere.

## Leases and fencing

All lease operations serialize the bounded work key with the same transaction-scoped
advisory lock and then capture `clock_timestamp()` once in PostgreSQL, so lock wait
cannot consume a newly returned TTL. TTL input is limited to 5 seconds through 10
minutes. Acquire is one `INSERT ... ON CONFLICT` and
can replace only a repository-clock-expired or explicitly released owner. Each
successful replacement increments a persistent `bigint` fence. Release marks the row
instead of deleting it, so reacquisition cannot reset the fencing sequence.

Acquire returns success, fence, acquisition/renewal/expiry instants, and repository
time. Renew returns ownership status, all lease instants, and repository time. A
contention or ownership-loss result still returns repository time but never reveals
the competing owner's identity or lease. Release and renew match the exact work key,
owner execution UUID, and fence.

`control.assert_worker_lease` raises on uncertainty and takes a row lock. It must be
called inside the same database transaction as the protected writes. The lock prevents
a replacement owner from advancing the fence until that transaction commits or rolls
back; an assertion in an earlier transaction is not a valid fence. Leases provide
ownership, not durable work transport, fairness, or an excuse for unbounded work.

## Retention behavior

M2 installs one policy row for raw metrics and one for diagnostic events. Both have
`enabled = false` and `retain_for = NULL`; no retention duration is invented. The
partition registry records exact UTC bounds and lifecycle state. The reporting view
returns bounded catalog estimates, preserves a configurable minimum number of newest
partitions, and exposes `recovery_prerequisite_satisfied = false`; consequently its
eligibility flag remains false and it performs no write.

Enabling retention remains prohibited until a forward migration and collector-service
workflow provide policy authorization, dry-run/preview confirmation, lease
coordination, backup/recovery prerequisites, detach-before-drop behavior, audit,
cancellation, and representative-volume tests. There is no database cron side effect.

## Risks and limits

- PostgreSQL roles are cluster-wide; installation must detect naming conflicts and use
  a dedicated repository cluster/database policy.
- Security-definer functions are intentionally powerful. Their fixed search paths,
  typed inputs, grants, catalog checks, and dynamic identifier quoting require review
  on every change.
- BRIN selectivity and B-tree write cost depend on real volume and ordering. M2 defines
  the justified baseline but performance certification requires representative data.
- Deduplication fingerprints leak equality and ciphertext does not remove the need for
  key isolation, rotation, TLS, backup protection, and RBAC.
- Persistent lease rows trade small bounded metadata growth for non-resetting fences;
  any later cleanup design must preserve the high-water fence independently.
- Repository availability is scheduler availability. Callers must expose degraded
  state and must not fall back to process-local ownership.
- This slice is not a PostgreSQL installation, backup, restore, HA, or release-support
  certification.

## Acceptance gates

M2 is complete only when the canonical validator and PostgreSQL 18 integration suite
demonstrate all of the following without hidden skips:

1. migration filenames and manifest entries are gap-free and bijective; checksum
   tamper, missing, unknown, reordered, and already-applied drift fail closed;
2. each migration plus its ledger insert is atomic, upgrades from an empty database,
   and a non-18 server is rejected;
3. exactly nine schemas and four NOLOGIN least-privilege roles exist, `PUBLIC` has no
   repository access, and expected-denial tests cover every role;
4. concurrent daily/monthly ensure calls produce exactly one attached partition with
   exact UTC bounds, registry row, BRIN index, and instance/time B-tree index;
5. missing, late, duplicate, boundary, DST/locale, malformed JSON, non-finite metric,
   and payload-bound failures are visible and atomic;
6. bounded binary `COPY` is correct and representative ingest/range-query plans meet
   documented performance thresholds before a production claim;
7. ciphertext deduplication, nonce/tag/size constraints, collision behavior, and
   forbidden plaintext/server/auditor access are tested;
8. audit inserts receive repository time, and server/collector UPDATE/DELETE plus
   unauthorized audit reads are denied;
9. retention remains disabled with no detach/drop executor, while preview reasons and
   minimum-partition floors are deterministic;
10. lease races cover contention, renewal, release/reacquire, monotonic fences, stale
    owner/fence rejection, expiry, cancellation, clock skew, assertion locking,
    database restart, and bounded timeouts;
11. repository assets contain no target SQL, production credential, connection string,
    automatic ORM generation, or direct MCP database access.

The next dependency is M3 observation-target onboarding and externally protected
credential/capability handling. Production collection remains blocked until that work
and the M4 bounded collector contract are complete.
