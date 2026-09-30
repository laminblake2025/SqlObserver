# ADR-0026: Assess backup freshness with explicit policy and replica coverage

Status: proposed, 2026-09-30

## Context

The revamp plan requires backup freshness per database against the configured
RPO, aware of the preferred replica. Current `backups.status` v1 observes local
`msdb` history for 35 days, excludes copy-only full backups, and emits a full
backup placeholder for databases without matching history. It carries neither
current recovery model nor AG membership and backup preference. Log and
differential placeholders are absent, so missing log evidence cannot be assessed
from those rows. Its name fallback for an unknown database GUID is not a durable
policy identity.

The current backup tables, Overview counts, HTTP response and MCP tool are
observations. They cannot safely be relabelled as RPO health. A new versioned
source and persistence contract must deliver the missing evidence together.

## Decision

Implement per-database, opt-in backup-age policy with separate full and log
thresholds. The full requirement explicitly chooses a full image (including
copy-only full backups) or a conventional differential base. Only regular log
backups satisfy the log check. Differential age is not substituted for either
requirement; chain-aware differential evaluation is a separate capability.

Do not ship an assumed business RPO. An enabled policy must have an explicit
full threshold; Full and Bulk-logged databases also need an explicit log
threshold. A recent, complete Simple recovery observation makes the log check
not required. Unknown recovery model, absent log policy and stale observations
remain indeterminate. The policy carries target, target revision, opaque
database identity and policy revision so a recreated database or changed target
cannot inherit a conclusion from unrelated evidence.

The first assessment contract limits backup thresholds to 30 days, below the
35-day source lookback, and observation age to one hour. Both use positive,
whole-second bounds. These are monitor contract bounds, not RPO recommendations.
Threshold equality is within the configured limit; the next second exceeds it.
Assess using an explicit UTC instant so History Browser and live evaluation
can share the same rules.

Preserve the latest full-normal, full-copy-only, differential, log-normal and
log-copy-only slots, at most five per database. Each recorded backup retains
nullable UTC finish, raw offset-free source-local finish, checksum and damage
markers, and metadata-completeness evidence. A missing slot is distinct from
a recorded backup with an unresolved time. Never borrow the monitor machine's
offset, substitute collection time, or clamp a future finish to zero age.
An unresolved eligible timestamp can be newer than known candidates and makes
selection indeterminate. A selected damaged backup is a policy violation;
unknown damage/completeness or a required unknown checksum is indeterminate.
Requiring checksums is an explicit policy option.

Keep full and log verdicts separate: within threshold, policy violated,
indeterminate, not required or disabled. Each has a closed reason, configured
limit, nullable observed age and selected evidence. Known damage and missing
required checksums are also retained as observed issues even when incomplete
coverage prevents a logical-database freshness verdict. Consumers must label
these as observed issues with their source freshness, not reinterpret an
indeterminate result as green or create alerts from a disabled policy.

### Availability groups

Preferred replica is observation context, not proof that a backup job ran.
The preference function identifies the local candidate; it does not read the
history on other instances. Microsoft's
[preference function documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-functions/sys-fn-hadr-backup-is-preferred-replica-transact-sql?view=sql-server-ver17)
also distinguishes non-AG databases from missing database names. Query only
databases enumerated in the same source scan; do not infer membership from the
function's return value.

For an AG database, local-only history remains indeterminate for a
logical-database verdict regardless of whether the local replica is currently
preferred. Backup location and preferences can change. Present the action
to inspect the other replicas and show observed local faults. Do not suppress
a missing backup merely because local preference is false.

An all-replica producer must prove the configured replica inventory and read
permission on every contributing target, retain each source's run/revision and
completion cutoff, validate identity/lineage and freshness, then select latest
slots across that bounded set. Missing or inaccessible replica evidence cannot
be promoted to complete. Authorization must be checked on each target; neither
a primary's grant nor an AG label grants access to another server. Monitor only
explicitly registered replicas; the collector does not open remote connections
discovered from cluster metadata. Keep replica pairing and histories pinned to
one requested time context rather than mixing current and rewound observations.

Copy-only full images need their own slot. Microsoft documents their role on
SQL Server 2019/2022 secondaries and the additional full/differential backup
support in SQL Server 2025 in
[secondary backup support](https://learn.microsoft.com/en-us/sql/database-engine/availability-groups/windows/active-secondaries-backup-on-secondary-replicas-always-on-availability-groups?view=sql-server-ver17).
Qualify source behavior on each supported major instead of treating secondary
capabilities as identical.

### Source, persistence and policy handoff

Stage checksum-pinned backups v2 SQL for majors 15, 16 and 17. Enumerate all
visible databases except TempDB, including those with no backup records. Carry
current recovery model, observed standalone/AG membership, local replica role
and preference, stable database/AG logical identity and backup lineage. Select
bounded history by identity, kind and copy-only slot with deterministic ordering;
preserve unknown identity rather than falling back to a display name as a policy
key. Visibility and permission failures must be explicit and bounded.

SQL Server stores backup metadata in the local `msdb`. Its database GUID changes
on restore while family and recovery-fork identities have different semantics;
AG logical database identity must be separately retained rather than assuming
every replica's local GUID is interchangeable. Match actual source GUIDs and
lineage before hashing. Use typed, keyed, target/revision-bound fingerprints for
published identities; never merge histories by a database name or an ordinary
unsalted hash. See Microsoft's
[backupset schema](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/backupset-transact-sql?view=sql-server-ver17).

Use new forward migrations and partitioned, append-only evidence with forced
target RLS. Publish inventory context, five-slot observations, loss accounting
and the completion receipt atomically under the run lease. Identical replay
must be idempotent and divergent replay rejected. Install retention, current
partition maintenance, versioned schedules and compatible-worker gates before
activation. Preserve v1 historical reads and existing policy revisions.

Add audited, revision-checked policy commands for TargetAdministrator on that
exact target. Reads require a target read role and use the existing signed
fixed-cutoff paging contract. Retired or recreated target/database identities
must not silently reuse a policy. Wire the assessment to Health checks,
fleet status, duration-confirmed alerts and a versioned read-only MCP surface
with the same verdict/reason/coverage fields. The UI shows the configured full
and log thresholds and the action that resolves an indeterminate result.

### What a verdict proves

Within threshold means the reported backup finish age satisfies the explicit
policy under the supplied evidence coverage. It does not prove a valid restore
chain, readable backup media, encryption-key availability or an exact recovery
point. `backup_finish_date` and AG `last_commit_time` are not interchangeable
with the last recoverable transaction. Verify restores and recovery-point
coverage separately before claiming RPO compliance.

No record in a bounded or purged history is reported as backup not observed;
it is not a claim that the database has never been backed up. Database names,
backup media paths, users, server names and query text are outside this contract.

## Delivery and verification

The first implementation stage supplies immutable bounded domain policy,
evidence and assessment contracts plus the deterministic evaluator. Focused
tests cover independent full/log limits, inclusive boundaries, recovery models,
copy-only image versus differential-base semantics, local-only AG visibility,
unknown and stale coverage, future/offset-free time, checksum/damage handling,
preserved observed faults, identity/revision mismatches and collection ownership.

No production producer supplies this new evidence yet, and no policy API,
database write, alert, MCP output or UI verdict is activated by this stage.
Leased persistence, qualification provisioning
and the policy/read surfaces are next. Native primary/secondary
qualification and end-to-end policy/alert/UI behavior remain required for the
feature to be complete. Synthetic arithmetic tests do not prove those gates.

The 38 focused assessment/contract tests pass on 2026-09-30. The .NET build
performed for that selection succeeds with warnings treated as errors. No
native SQL Server, PostgreSQL, web or fleet qualification is claimed for this
domain-only stage.

The next stage now embeds checksum-pinned SQL15/16/17 backup v2 source and a
closed manifest/schema. It retains a bounded inventory stream and five backup
slots, matching local GUID/family/recovery-fork identity without using display
names. Duplicate GUIDs, unknown copy markers, incomplete AG identity and raw
source times remain explicit. The source does not normalize finish time from
the start offset. Seven schema checks and four local SQL projection/catalog
checks pass; they do not qualify real AG behavior or production activation.
See [the source contract and lab limits](../testing/backup-v2-source.md). The
fenced writer and all policy/read surfaces remain open.

The typed parser now validates the exact two-stream source, derives keyed
target/revision/class-bound identities, preserves incomplete inventories and
withholds all prefixes on limit loss. Its immutable domain inputs are exercised
by the freshness evaluator. Historical finish resolution requires an explicit
versioned target/revision qualification of zone rules and configuration for
both endpoints; current source zone and recorded start offset alone remain
insufficient. No production qualification provider or command exists yet.
Ninety-four focused unit checks and four sequential native source/parser
checks pass on 2026-09-30. Fenced persistence,
qualification provisioning and the policy/read/alert/UI surfaces still require
implementation and qualification.

The evidence and qualification models are now domain contracts with constructor
validation for target/revision/run, lineage, source context and accounting.
An application qualification port and staged v2 collector now publish the
operational-health payload under the shared five-second deadline. The shared
validator checks the v2 payload, accounting and coverage and rejects v1/v2
mixing. Its visibility marker is limited to explicitly incomplete supported
health observations. The active registry still selects v1; at this stage the
repository also remains on v1. Rollout gates remain required before activation.
This stage passes 171 targeted unit checks and four local sequential SQL
source/parser checks; it does not qualify production collector transport,
monitoring permissions or real AG topology.

The next persistence stage now provides the private v2 writer and canonical
repository mapper in forward migrations 0270/0271. A scan, database inventory,
samples, completion receipt and schedule/circuit update commit atomically under
the run and lease fences. Evidence parents use monitor receipt time for
partition placement, retain source clock/time separately, enforce target RLS
and reject history mutation. Unknown identities retain inventory without policy
identity or samples. Truncation withholds every database/sample prefix; failed
scans retain receipt and rejection accounting only. Canonical identical replay
is accepted, while changed completion is rejected.

Resolved times reference immutable target/revision-qualified serialized .NET
rules and a UTC interval. PostgreSQL checks the reference, window, source-zone
agreement and endpoint/order/offset bounds; the typed resolver performs the
historical rule conversion. No audited provisioning command or production
qualification provider exists yet. Four native PostgreSQL 18.4 persistence
checks and two related existing commit regressions pass. Catalog scheduling and
partitions in those tests are fixture-only; production EXECUTE remains revoked.
Retention/partition maintenance, compatible-worker/catalog activation, policy
and read surfaces, alerts/UI and native AG/TLS qualification remain open.
