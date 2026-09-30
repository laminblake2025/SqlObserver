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

The retention stage now registers and maintains the three backup parents under
one policy, initially 30 days with a floor of three partitions per parent.
UTC D-1/D/D+1 creation is catalog-lease fenced and private. Existing v1 policy
revisions are preserved. The scheduled worker keeps the entire FK-linked graph
attached through a 24-hour grace period, cancels grace on policy revision
changes, rechecks all dependency/reader/backfill/recovery holds, and atomically
retires child partitions before their parents. It never drops repository
constraints with CASCADE. Failure leaves every relation attached and records
an immutable bounded retry. Generic and manual single-partition detach cannot
split this graph; global policy mutation and the scheduled route remain usable.

Two focused native PostgreSQL retention checks and seven related AG/TempDB
regressions pass, as do nine affected analytics web contracts and typecheck.
The rollout stage now installs the exact pinned backup v2 catalog and private
activation wrapper in migration 0274. Activation is catalog-lease fenced,
requires enabled grouped retention/current partitions and drained v1 runs,
and atomically advances schedule revisions. Target changes preserve the
activated version. Only workers reconciled against the pinned v2 catalog in
the last five minutes can claim v2 work; future registrations, old workers and
stale schedule revisions are rejected. The wrapper supports combined AG v2
activation and shares the target trigger lock order.

Five focused native PostgreSQL rollout cases and nine related backup
persistence, AG rollout and inventory claim regressions pass. The active production
registry remains on v1 and application EXECUTE stays revoked. Qualification
provisioning, policy/read surfaces, alerts/UI, production activation and native
AG/TLS qualification remain open.

The qualification stage now adds migration 0275, an audited TargetAdministrator
GET/PUT surface, immutable provisioning/revocation commands and a real collector
provider. Explicit serialized .NET historical rules, source-zone agreement,
window and a digest of verified configuration evidence are required. The
provider verifies the stored checksum/rules for the exact active target/revision
and does not trust owner-seeded certificates without an explicit command.
Concurrent edits use a revision check; identical latest-operation retries replay
without duplicating evidence/audit, while changed or superseded operations
conflict. Revocation retains prior certificates for immutable provenance.

The v2 commit wrapper and commands share a target qualification lock before
their other locks. New samples reject replaced/revoked/unprovisioned rules;
completed identical receipts stay replayable. The staged collector now resolves
the real provider and maps its deadline failures correctly. The production
registry stays on v1 and v2 catalog/writer EXECUTE remains private. Backup
policy/read surfaces, alerts/UI, production activation and native AG/TLS
qualification still require delivery. HTTP, application and native repository
checks do not attest the operator's historical configuration evidence.
This stage passes ten native PostgreSQL command/persistence checks, 34 affected
unit checks, two HTTP cases and the staged collector composition check. The
production backup assessment and native replica qualification remain open.

The policy stage now adds migration 0276 and a target-scoped GET/PUT policy
surface. Each immutable revision carries explicit full/log/observation limits,
full-image versus differential-base choice and checksum requirement. No RPO
policy is seeded. Creation/enabling requires a known current v2 database
identity and bounded recent source/receipt evidence; Full/Bulk-logged recovery
requires an explicit log limit. Existing policies can be disabled when evidence
is stale. Target and policy revisions plus an operation identity fence writes,
which are atomic with their audit receipt. Exact latest retries replay, changed
or superseded operations conflict, and old identities do not transfer policy.

Reads use an explicit UTC cutoff and preserve policy history for prior target
revisions. Four focused native PostgreSQL cases, one mixed-grant application
case and two HTTP cases pass. The first native regression failed before the
policy read function existed. Assessment/evidence paging, Health checks and
fleet integration, alert/MCP/UI surfaces, native replica qualification and
production activation remain open; persisted policy alone is not a freshness
verdict or proof of restore/RPO compliance.

The assessment stage now adds migration 0277 and a versioned read-only HTTP
surface. It reads the latest completed backup v2 receipt in the seven days
before an explicit monitor cutoff, without falling back from failed, empty,
truncated or expired evidence to an older success. A repeatable-read repository
transaction combines the receipt, retained inventory/sample graph, policy
history and historical qualification commands. Unknown database identities keep
their ordinal and remain unconfigurable. Missing policy has a distinct
indeterminate reason and supplies no assumed limits.

Signed continuations bind the target, target revision, cutoff, run, database
ordinal and append-only policy/qualification context. A transaction committed
between pages with an earlier recorded timestamp invalidates the continuation
instead of silently changing its assessment. Revision changes do not transfer
policy; an explicit historical target revision can read its retained evidence.
Expired evidence invalidates continuations and stays distinguishable from
never-collected data while its completion receipt remains retained.

Recorded finish UTC and raw local times retain their immutable provenance.
Assessment uses a resolved finish only while its qualification was active at
the requested cutoff. A later revocation/replacement withholds age and gives an
actionable indeterminate reason; rewind before that command preserves the prior
verdict. Known damage and missing required checksums remain observed issues.
Source clock skew, stale observations and preferred local AG replicas remain
indeterminate rather than gaining fabricated freshness or all-replica coverage.

The endpoint requires a read role on the exact target, canonical UTC input,
bounded pages and response bytes, and uses the shared signer and ProblemDetails.
This read stage does not activate production v2 collection. Health checks/fleet,
alert/MCP/UI integration and native replica/TLS qualification remain open.

Eight native PostgreSQL cases, six application cases and two HTTP cases pass
for this stage. The Server build succeeds with no warnings or errors. The
native policy commit race is exercised with a real uncommitted transaction,
not a simulated sequence of already committed rows. No broad Local suite or
production collector activation is claimed.

The Health checks Backups view now consumes these assessments using the global
range's end as its cutoff, preserving microsecond rewind URLs and the workspace
local/UTC display. Independent full/log verdicts show explicit limits; missing
policy, inactive qualification and local-only AG coverage remain unknown.
Disclosures retain policy/qualification provenance and raw clocks. Sample byte
counts are decimal strings so browser reads preserve signed 64-bit values.

Bounded client reads validate selected evidence and contiguous database pages,
bind continuations to one assessment, cancel superseded requests and recover from
stale cursors by restarting. Shared live refresh preserves open evidence and
labels the previous assessment while an update is pending. Legacy v1 snapshots
remain in a separate disclosure. Twelve new web cases, fourteen affected
operational cases, two HTTP cases and the production frontend build pass;
mounted synthetic checks exercise paging, stale recovery and live refresh.
Policy/qualification administration UI, fleet/alert/MCP integration, native
replica/TLS qualification and production activation remain open.

Health checks now provides explicit current-policy editing for a known database
identity in Live when `/me` grants TargetAdministrator on that exact target.
Rewind and unknown identities remain read-only. The editor reads the current
policy separately, supplies no new-policy defaults, and submits revision-fenced
settings with an operation ID. An uncertain save preserves the submitted payload
for exact retry; a conflict requires reloading current state. Verified success
refreshes assessments without claiming production collection has been activated.
Seven new client cases and 26 affected web cases pass. Mounted synthetic checks
exercise creation, uncertain-save replay, conflicts and role/rewind restrictions.
Historical qualification UI, fleet/alert/MCP integration, native replica/TLS
qualification and production activation remain open.
