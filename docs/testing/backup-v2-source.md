# Staged backup v2 source checks

The `backups.status` v2 assets are embedded under `M9BackupV2Assets` and verified
as an exact five-file SHA-256 bundle. The separate closed manifest schema v7
pins source limits, conditional AG permissions and output version 2. The active
M9 registry and production schedules still select v1. V2 catalog activation,
writer grants and native qualification remain separate rollout gates.

The SQL source emits one inventory row and up to five latest classified backup
slots per visible database except TempDB. Inventory remains when history is
absent or identity is unknown. History joins the current database GUID, family
and last recovery fork, not the database name. Duplicate database GUIDs are
explicitly ambiguous and do not acquire history. Copy-only full and log slots
remain separate; copy-only differential or unknown-copy markers disclose an
unclassified-record flag. Unresolved finish times sort ahead of known times in
their slot, so an older known timestamp cannot hide uncertainty.

Both streams retain recovery model, catalog state, AG logical identity, local
replica and role, and nullable preference. Any AG identity marker keeps visibility
local-only even if the group-database identity is incomplete. Standalone is
asserted only when the source observed a non-AG database; unknown HADR context
remains unknown. Nothing aggregates history from another replica in this stage.

## Source parameters and shape

Commands have a five-second timeout. The future collector must use a shared
five-second deadline for connection, query and parsing; passing these SQL tests
does not implement that runtime deadline. Production bounds are 512 databases,
3072 rows and 4 MiB. Supply `@maximum_databases=513` and `@maximum_rows=3073` to
disclose one lookahead for each limit. SQL rejects values outside 1–513 and
1–3073. Retain the 35-day history lower bound and propagate either lookahead as
loss rather than treating the prefix as complete.

The pinned result has 29 columns in the order below. Inventory is `row_kind=0`
and has NULL backup fields. Backup rows are `row_kind=1`, ordered after their
inventory row by kind and copy-only flag. Overall order is catalog database ID,
row kind, backup kind, copy-only. The catalog ID is an ephemeral association key,
not a durable policy identity.

| Ordinal | Fields | SQL type / semantics |
|---|---|---|
| 0–1 | row_kind, catalog_database_id | int |
| 2–7 | database, family, recovery-fork, group, AG-database, local-replica fingerprints | nullable binary(32); source digests, not published keyed identities |
| 8 | database_identity_known | bit; false for NULL or duplicate GUID |
| 9–10 | recovery_model_desc, catalog_database_state_desc | nullable nvarchar(60); catalog state is not an AG health verdict |
| 11 | visibility_scope | smallint: 0 unknown, 1 standalone, 2 AG local-only |
| 12–13 | local_replica_role_desc, local_replica_preferred | nullable nvarchar(60), bit |
| 14–15 | backup_kind, copy_only | nullable int, bit; full=1, differential=2, log=3 |
| 16–17 | source_local_start, source_local_finish | nullable datetime; offset-free source clock |
| 18 | recorded_start_offset_minutes | nullable smallint; valid backupset offset × 15, unknown stays NULL |
| 19–22 | size_bytes, has_checksum, is_damaged, metadata_complete | nullable bigint, bit, bit, bit |
| 23–24 | backup_first_recovery_fork_fingerprint, backup_last_recovery_fork_fingerprint | nullable binary(32) |
| 25 | source_observed_utc | datetime2(6); SQL source's UTC clock, not receipt time |
| 26 | source_time_zone_id | nullable nvarchar(128); current zone ID on majors 16/17, NULL on 15 |
| 27 | history_lower_bound_local | datetime; source-local lookback bound |
| 28 | unclassified_backup_seen | bit; invalid/unknown slot metadata requires degraded coverage |

The parser must re-key source digests by identity class, target and revision,
validate lineage and all closed tokens, retain unknown identities without name
fallback, preserve source accounting, and withhold invalid output. It must not
pass local-only AG history as all-replica evidence to the freshness evaluator.

## Typed parser and historical time resolution

`SqlServerBackupV2Parser` now reads all 29 fields in ordinal order, checking
the inventory/sample association, unique ordered slots, closed metadata tokens,
matching recovery lineage and one shared source clock/lookback/zone context.
Published fingerprints use the configured identity key, distinct identity
classes, target and target revision. Ephemeral catalog IDs and unkeyed source
digests do not leave this boundary. An unknown or duplicated database GUID
retains an inventory entry with no policy fingerprint or freshness assessment
input; it does not borrow another identity or fall back to a name.

Incomplete inventory, offline state, unknown recovery model, unclassified
backups and incomplete AG context retain partial coverage. A complete local
secondary scan remains `AvailabilityGroupLocalOnly`, so the domain evaluator
cannot issue an all-replica verdict from it. Missing metadata and known
checksum/damage faults remain present in the recorded samples. Source UTC
observation time and monitor UTC receipt time are separate fields.

The parser reads at most one row beyond its configured row/database ceiling.
On row, database or byte truncation it publishes no database prefix and records
all read rows as a conservative lower bound on withheld evidence. Malformed
input and cancellation return no snapshot. Output collections own their data.
The byte allowance covers the envelope, expanded keyed identities, escaped
zone text and derived assessment inputs. These limits are independent: a
sample-rich scan can hit the conservative byte ceiling before the row ceiling.

UTC finish resolution requires an explicit `BackupHistoricalZoneQualification`
for the exact target/revision, with a version and a half-open UTC interval
certifying historical zone configuration and rules. No production provider or
audited qualification command is installed in this stage. The current SQL
zone ID alone cannot create that qualification. A recorded start offset must
agree with the qualified zone at start, both endpoints must be unambiguous and
valid, and both UTC instants must lie inside the qualified interval. The
resolver uses the finish's own offset across a clock transition; unknown,
inconsistent or out-of-range values retain a closed reason and no UTC finish.
It never substitutes the host zone, clamps calendar overflow or fabricates a
receipt-time finish. See [.NET ambiguity handling](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isambiguoustime?view=net-10.0)
and [UTC conversion behavior](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.converttimetoutc?view=net-10.0).

Typed evidence and historical qualification models now live in the domain.
Their constructors enforce closed metadata, lineage, unique slots, source
accounting and the exact target/revision/run, including unknown-identity rows.
The SQL adapter retains only the parser result and conversion logic. An
application port supplies explicit historical qualifications without coupling
the repository or validator to the SQL adapter.

## Collector payload stage

`SqlServerBackupsV2Collector` now produces a version-2 operational-health payload
validated by the shared `CollectorOutputValidator`. Required capabilities,
transport, privilege, major/platform/edition and exact permission scopes are
checked before either the qualification port or the SQL source is called.
SQL Server 2019 additionally requires the AG metadata grant when the profile
observes AG support; the source's 51006 guard remains authoritative if topology
and capability evidence differ. Missing source bounds are invalid output, not
permission failures. Failed runs publish no source prefix.

One linked deadline covers qualification lookup, connection, query and parsing.
The production command supplies typed row and database lookahead parameters,
uses a sequential reader, and captures receipt UTC when the reader opens. The
collector qualification port reads the latest explicitly provisioned command
for the exact active target/revision. Absence or revocation returns no
qualification. It never invents rules from the current zone ID. External cancellation propagates; deadline exhaustion
has its own outcome. Version-1 and version-2 payloads cannot cross validators.

The active registry still selects v1. The repository now dispatches v2 payloads
to a private fenced writer; production collector execution remains ungranted.
Retention is registered and maintained as a three-table group. Private catalog
activation and compatible-worker claim gates are installed. Audited historical
zone provisioning and explicit per-database policy commands are available;
assessment reads remain pending. No production
activation or native collector qualification is claimed here. Cross-replica
pairing remains explicit work; this collector does not discover peers or merge
histories.

## Time and metadata boundaries

The source deliberately does not publish a converted finish UTC instant.
Microsoft defines `backupset.time_zone` at backup start, not finish. It is
insufficient to resolve a backup spanning an offset transition. A current
`CURRENT_TIMEZONE_ID()` is additional context, not a certificate of the historical
zone configuration. The parser needs an explicit, qualified time-resolution
contract; ambiguous, invalid, unknown or inconsistent historical times must
remain indeterminate. See
[backupset](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/backupset-transact-sql?view=sql-server-ver17)
and [CURRENT_TIMEZONE_ID](https://learn.microsoft.com/en-us/sql/t-sql/functions/current-timezone-id-transact-sql?view=sql-server-ver17).

Metadata visibility is limited to visible databases. `VIEW ANY DATABASE` alone
does not enumerate every offline database. Microsoft also documents that
`sys.database_recovery_status` can try to start an unopened database. Qualify
offline, AutoClose and permission behavior in a disposable lab before production
activation, without adding database-alter privileges just to improve coverage.
See [sys.databases](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-databases-transact-sql?view=sql-server-ver17)
and [sys.database_recovery_status](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-recovery-status-transact-sql?view=sql-server-ver17).

## Verification recorded on 2026-09-30

Seven real JSON Schema checks pass for the approved manifest and rejection of
extra fields, nested drift, missing bounds, unbounded limits, wrong output
version and omitted AG permission. No PostgreSQL database is used by these
schema-only tests despite their project location.

The collector SQL inventory, passive-source and row-bound checks pass, along
with PowerShell syntax validation. Eight focused checks of the actual passive
validator confirm that fixed THROW messages are accepted while mutation and
dynamic execution remain rejected. This records the collector-specific checks,
not a full Local validation run. The source uses read-only CTEs; its test fixture
table variables are separate from the embedded collector assets.

Four SQL Server checks pass on the local engine: all three versioned SQL assets
execute against table-variable fixtures, and the installed-major source returns
the real catalog shape and rejects invalid source limits. Fixtures cover renamed
and recreated databases, duplicate and missing GUIDs, wrong recovery forks,
missing/purged history, five slots, equal-time tie selection, unknown offsets,
unresolved finish time, classified loss, local-only secondary context, incomplete
AG identity, and database/row limits. Fixtures modify table variables only; the
real source read does not issue backup, restore, grants or persistent DDL.

The same four SQL checks now drive the typed parser through a sequential
`SqlDataReader`, including the installed-major catalog read. Ninety-four
focused unit checks pass across historical time resolution, typed parsing and
the existing freshness assessment contracts. The initial resolver tests failed
against the unresolved implementation before the conversion logic was added.
Tests cover transition folds/gaps, crossing a transition, historical window
boundaries, calendar overflow, keyed identity/revision isolation, malformed
streams, nullable metadata, preserved faults, future source time and withheld
limit prefixes. No full Local test profile was run for this change.

The collector/domain/validator stage passes 171 targeted unit checks, including
the backup suites and related AG, replication and legacy operational-health
checks affected by the shared validator. Four local native source/parser checks
also pass after domain promotion. The production command and failure paths are
tested through injected ports; trusted TLS, non-admin execution and real AG
behavior still require the separate native qualification above.

The local run used Windows authentication and encrypted transport with the
existing Local-only certificate bypass for its self-signed lab certificate.
It does not qualify trusted production TLS, a non-admin monitoring principal,
native SQL Server 2019/2025, or real AG preference and topology. These remain
separate required lab checks. The production collector, fenced writer,
retention, policy commands and assessment UI are not activated by these tests.

The persistence stage adds forward migrations 0270/0271 and a canonical mapper.
`backup_v2_scan`, `backup_v2_database` and `backup_v2_sample` are typed, append-only,
forced-RLS parents partitioned by the monitor receipt clock. The target clock and
offset-free source times remain separate evidence. Unknown database identity
retains an ordinal inventory row without backup samples or a policy identity.
All inventory/sample rows, the scan, completion receipt, visibility gap and
schedule/circuit update commit together under the exact run, target revision,
schedule revision and worker lease. Identical canonical completion replay is
accepted after the schedule advances; changed completion is rejected.

Resolved finish times reference an immutable target/revision qualification
containing bounded serialized .NET zone rules, their checksum, expected source
zone and a half-open UTC window. The writer checks that reference, source-zone
agreement, recorded-start conversion, endpoint window/order and plausible
minute-aligned finish offset. It does not reinterpret Windows zone rules in
PostgreSQL. Historical transition conversion remains the typed resolver's
responsibility. At that persistence stage provisioning was fixture-only; the
audited command/provider described below now supplies explicit certificates.

Four focused PostgreSQL 18.4 tests pass, including direct malformed JSON and
qualification-boundary cases, discarded truncated prefixes, unknown identity,
nullable metadata, rejected item/byte accounting, future target clocks, RLS,
private grants, append-only protection, lease/schedule fences and rollback when
the final sample partition is missing. Two related existing backup/AG commit
regressions also pass. Fixture-only partitions and v2 catalog scheduling are
explicit: these tests do not activate production v2 or qualify native AG/TLS.

Forward migrations 0272/0273 add one `m9_backup_evidence` retention policy for
the scan, inventory and sample graph. Its default is 30 days with a floor of
three partitions for every parent. Operator v1 backup policies remain unchanged.
Catalog maintenance creates and registers only the current UTC D-1/D/D+1
partitions for each parent, verifies their identity/bounds/owner, and revokes
direct application-role access. The SQL wrapper checks the catalog lease
before and after the operation.

The scheduled retention worker starts a 24-hour attached grace period. All
three tables stay readable during grace; a policy revision change cancels it.
After grace, the worker checks the retention lease, every partition's physical
and registry identity, floors, reader/backfill leases, time-scoped analytics
dependencies, unreviewed inbound foreign keys and recovery attestation. It
detaches/drops samples before inventory before the scan in one transaction;
it never uses `CASCADE`. A blocked drop rolls back every preceding DDL action,
records an immutable retry, and leaves the graph attached. The separate
single-partition worker and manual detach endpoint cannot split this graph.
Policy edits remain available through the global retention API; manual backup
group retirement uses the scheduled route at this stage.

Two native backup retention tests and seven existing AG/TempDB retention
regressions pass on PostgreSQL 18.4. They exercise real linked rows, grace
readability/cancellation, child reader/backfill holds, floors, missing recovery
evidence, an unknown FK, a final-parent drop failure and subsequent retry,
preserved FK constraints, private partition maintenance, stale leases and
authorized policy mutation. The nine affected analytics client/parser checks
and web typecheck pass. These are focused checks; no full Local profile or
production collector activation is claimed.

Forward migration 0274 installs the exact pinned v2 catalog without activating
it. Its private reconciliation entry point requires the catalog lease, enabled
grouped retention and current partitions, and rejects activation until v1 runs
drain. The transition increments schedule revisions, preserves interval/enabled
settings, and keeps new or revised targets on v2 after activation. Catalog and
target triggers share the AG, backup, inventory lock order. The wrapper also
supports a combined AG v2 catalog.

Only an owner registered by that exact reconciliation can claim backup v2 work;
registration expires after five minutes and future timestamps are rejected.
Legacy catalogs cannot downgrade activated schedules, and pre-transition work
cannot begin under an old schedule revision. The production registry still
selects v1 and application EXECUTE on the v2 catalog/writer remains revoked.
Fixture activation does not qualify a deployed collector.

Five focused PostgreSQL 18.4 rollout cases pass, covering activation rollback,
private grants, catalog pins, lease and retention prerequisites, combined AG v2,
AG-only routing, operator interval/enabled settings, idempotence, target
revisions, stale work and old/expired/future workers. Nine related backup
persistence, AG rollout and inventory claim regressions also pass.

## Historical zone provisioning

Migration 0275 adds an append-only command ledger and a narrow collector read
function. Both the certificate and ledger remain private with forced RLS.
The Server exposes GET/PUT
`/api/v1/observation-targets/{instanceId}/health/backup-zone-qualification/`.
Both require TargetAdministrator on that exact target. PUT is rate limited,
accepts JSON only, and uses the shared origin/body limits. Denied administration
is audited through the application boundary; successful commands and their
certificate/audit receipt commit atomically. The ledger retains the receipt's
ID/time without adding an FK that would prevent independent audit retention.

GET takes `targetRevision` and returns the current `qualificationRevision`,
enabled state, source zone, UTC window and evidence/rules digests. Zero means
no explicit command for that active revision. Provision with these fields:

```json
{
  "expectedTargetRevision": 1,
  "expectedQualificationRevision": 0,
  "operationId": "<new UUID>",
  "enabled": true,
  "historicalConfigurationConfirmed": true,
  "serializedZoneRules": "<verified TimeZoneInfo.ToSerializedString() snapshot>",
  "sourceZoneId": "<exact SQL source zone ID>",
  "fromUtc": "2026-01-01T00:00:00Z",
  "toUtc": "2027-01-01T00:00:00Z",
  "evidenceSha256": "<64 lowercase hex characters>"
}
```

The sample window is illustrative. The operator must verify historical target
zone configuration throughout the requested half-open interval and preserve
the evidence identified by its digest. A zone ID or the monitor's current
rules alone is not that evidence. Serialized .NET rules are validated and
bounded to 64 KiB before canonical storage; the entire JSON request also has
the existing 64 KiB limit. The provider verifies their checksum and deserializes
those stored rules without consulting the monitor's zone database. Owner-seeded
certificates without a command are not implicitly trusted.

Use the GET revision as the expected revision on edits, retaining the operation
ID when retrying the same mutation. An identical latest operation replays
without a second audit/certificate; changed, superseded or stale-target requests
conflict. Concurrent edits have one winner. Revoke with `enabled: false`, the
expected revisions and a new operation ID, omitting every qualification field.
Revocation appends history instead of deleting certificates used by prior scans.
The latest command is the single active qualification; backups outside its
window remain indeterminate.

Commands and new v2 commits serialize on a target qualification lock before
their target/run/schedule locks. New samples cannot cite a revoked, replaced or
unprovisioned qualification. A failed final write rolls back scan/inventory/
sample evidence, receipt and schedule changes. Already completed identical
receipts remain replayable, retaining their original immutable provenance.
The staged v2 collector is composed with the real provider, while the active
registry still uses v1. Provider timeout is a deadline outcome before target SQL.

Focused checks cover real PostgreSQL command concurrency, atomic certificate/
audit rollback, idempotence, revocation, revision/role isolation, private grants,
immutable history, invalid serialization and a stored-rule DST transition.
HTTP checks exercise explicit confirmation, malformed rules, JSON/origin
requirements, administrator reads/revocation, conflict and out-of-scope grants.
These checks do not prove the operator's historical evidence, real AG/TLS
qualification or a complete backup policy/assessment/alert/UI path.
Ten focused PostgreSQL 18.4 command/persistence checks, 34 affected collector/
authorization unit checks and two HTTP cases pass. A separate composition
check confirms the staged provider/collector and active v1 registry. No full
Local profile was run for this stage.

## Explicit backup policies

Migration 0276 persists append-only policy revisions keyed by target, target
revision and the v2 database fingerprint. No rows or RPO values are seeded.
The policy table has forced RLS and no direct application-role grants. Its
Server-only mutation function writes policy and audit together, checks the
target/policy revisions and operation ID, and retains the observed inventory
run as provenance without an FK that would obstruct evidence retention.

GET/PUT
`/api/v1/observation-targets/{instanceId}/health/backups/databases/{databaseFingerprint}/policy/`
exposes one policy. GET requires Viewer, Operator or TargetAdministrator on
that exact target, takes `targetRevision` and optional `atUtc`, and returns the
latest policy recorded by that cutoff. Its default is current UTC; future,
non-UTC and non-microsecond-aligned cutoffs are invalid. Missing policy returns
404, with no implicit threshold. Old target/database policies remain readable
for history and cannot be silently assigned to a new identity/revision.

PUT requires TargetAdministrator on the exact target, JSON and the existing
origin/body/rate limits. A complete request is:

```json
{
  "expectedTargetRevision": 1,
  "expectedPolicyRevision": 0,
  "operationId": "<new UUID, retained on identical retries>",
  "enabled": true,
  "maximumFullAgeSeconds": 86400,
  "maximumLogAgeSeconds": 900,
  "maximumObservationAgeSeconds": 300,
  "fullRequirement": "full_image",
  "requireChecksums": false
}
```

Those thresholds are illustrative, not recommendations. Full/log limits must
be 1–2,592,000 whole seconds and observation age 1–3,600 seconds. Full requirement
is explicitly `full_image` (including copy-only full images) or
`differential_base` (conventional full backups). Enabled Full/Bulk-logged
databases require an explicit log limit. Simple recovery permits a null log
limit. A later recovery-model change does not invent one; its assessment will
remain indeterminate until the policy is updated.

Creation and enabling require a known, complete database identity in the latest
v2 scan, with monitor receipt and source observation within the last hour and
no future source time. Partial/truncated/failed scans cannot silently fall back
to older complete identity evidence. A database absent from the latest scan
returns `database_not_observed`. An existing policy may still be disabled when
inventory is unavailable or stale. Identical latest-operation retries replay;
changed or superseded operations conflict, and concurrent revisions have one
winner. Successful writes and rejected valid administration are audited.

Four focused native PostgreSQL checks pass using real fenced v2 inventory
commits. They cover explicit thresholds, null log values, immutable as-of
history, audit atomicity/rollback, stale inventory, disablement, concurrency,
private grants and database/target replacement. One mixed-grant authorization
check and two real HTTP pipeline cases also pass. The first repository
regression failed for the missing policy read function before implementation.
No full Local profile was run. Policy configuration does not activate v2
collection, expose assessment verdicts, or complete the alert/UI/replica path.

## Assessment and evidence reads

Migration 0277 and
`GET /api/v2/observation-targets/{instanceId}/health/backups` expose the staged
v2 assessment independently of the existing v1 observations. Parameters:

- `atUtc`: optional UTC instant ending in `Z`, with at most six fractional
  digits, no future value. Defaults to current UTC on the first page and the
  signed cursor's original cutoff on continuation.
- `limit`: 1–100 databases, default 50. Each has at most five backup slots.
- `targetRevision`: optional positive revision, default current target revision
  on the first page. Supply an older revision explicitly for retained history;
  a continuation restores its signed revision.
- `cursor`: opaque shared-signer continuation, bound to the complete assessment
  context. A changed/expired context returns 409 `cursor_stale`; restart paging.

Viewer, Operator or TargetAdministrator must be scoped to this target. Direct
tables and collector access to these reporting functions remain private. Each
page's header, samples and policy read share one repeatable-read transaction.
The receipt lookup is bounded to run starts within seven days before the cutoff.
If no receipt remains in that window, `not_collected` describes this bounded
read, not proof that collection never occurred. A retained receipt whose graph
has expired produces `evidence_not_retained`. The latest failed or withheld run
does not reuse a prior success. Rejected output counts remain visible on failed
receipts without publishing rejected database/sample rows.

Database ordinals preserve unknown identities through pagination. Known
identities without policy report `policy_not_configured`; unknown identities
report `database_identity_unknown`. Neither has an invented RPO, policy revision
or observed age. Full/log checks carry independent status, reason, limits,
nullable age and selected evidence. Policy settings, source coverage, collector
state and known observed issues are also returned. Local AG coverage stays
indeterminate even when the observer is preferred.

Each sample exposes `recordedFinishUtc`, raw local start/finish, recorded start
offset, time state and qualification revision. `qualificationTrusted` means the
immutable referenced qualification was active at this cutoff; it does not turn
an unresolved time state into a resolved one. Revoked/replaced qualified times
remain visible as recorded evidence but cannot produce a current age/verdict.
Consumers must use the check's selected evidence, never recompute age from a
sample's recorded UTC without its qualification and coverage context. Rewind
before revocation uses the earlier command. Known damage and missing required
checksums remain observed issues independently of the overall verdict.

Sample `sizeBytes` is a canonical non-negative decimal string or null, bounded
by signed 64-bit storage. JSON numbers are not used for this field because a
JavaScript client would lose precision for large recorded sizes.

Signed cursors include policy-history count and qualification-command revision.
These detect late commits recorded before the first page's cutoff, including
edits to a database on a later page. No policy or qualification update can
silently alter that continuation. Cursor payloads remain below the shared
signer's limits. Responses are bounded to 1 MiB, use `no-store`, and return
ProblemDetails with code/correlation identity on failures.

Focused native PostgreSQL tests use real fenced v2 commits. They exercise paging
without duplicates/gaps, unknown identities, as-of policy and qualification
history, revocation, disabled policies, collection and output-validation
failures, source limits, expired graphs, target revision changes, source clock
skew/staleness, local AG preference and a policy transaction committed between
pages. Application tests cover mixed-role scope, observed faults under revoked
qualification and malformed repository contracts. HTTP tests use the real
pipeline for authentication, signed bindings, cutoff restoration, nullable
evidence, explicit action messages and stale-cursor errors. The initial native
regression reached PostgreSQL and failed `42883` before the reporting function
existed. No full Local suite or production/native-replica qualification is
claimed. Eight native PostgreSQL cases, six application cases and two HTTP
cases pass for this read stage. Fleet, alerts, MCP and production activation
remain pending.

## Health checks view

The Backups category now reads this assessment with the global time context.
The end of the range is its monitor cutoff; the start does not filter snapshot
records. Custom URLs preserve all six fractional digits across pages. Live
refresh uses the shared refresh signal, cancels superseded requests and retains
the previous labeled assessment while updating the same page. An open evidence
disclosure survives that refresh. Changing target or rewind context clears the
previous assessment. Local/UTC display uses the workspace toggle; raw SQL Server
local times keep their own label and recorded values.

Full and log verdicts, explicit limits and observed issues appear separately.
Absent policy, unresolved/revoked timestamps and preferred local-only AG evidence
remain unknown. Evidence disclosures show policy revision, qualification trust,
raw clocks, checksums, damage and exact byte counts. Earlier v1 observations are
available in a separate disclosure. No v1 row is used to invent a v2 verdict.

Client reads stream at most 1 MiB, validate closed contracts and selected
evidence, and page 50 databases at a time. Continuations must preserve the
receipt, target revision and exact cutoff, with contiguous inventory ordinals.
Stale or unverifiable evidence clears the verdicts and offers a restart;
temporary failures offer a retry without reflecting server error text.

Twelve focused web checks cover cutoff precision, unknown states, unsafe sizes,
false-green rejection, page binding, bounded fetch and the rendered production
view. Fourteen affected operational checks and the two HTTP cases pass. The
frontend typecheck, production build and asset verification pass. Mounted
loopback synthetic checks cover rewind paging, stale-cursor recovery and open
evidence during live refresh. These checks do not qualify real SQL Server/AG
collection. Policy/qualification administration UI, fleet/alert/MCP integration,
native replica/TLS qualification and production v2 activation remain open.

## Policy administration in Health checks

An active TargetAdministrator grant on the exact selected target now exposes
Configure backup policy inside a known database's evidence disclosure in Live.
Read-only and out-of-scope grants do not expose it. Unknown database identities
remain unconfigurable. Rewind is read-only and explains how to return to Live.

Opening the editor loads the current policy independently of the displayed
assessment cutoff. New policies have no suggested thresholds, status, full
requirement or checksum choice. All settings are explicit; age limits use whole
seconds, with the same storage bounds. Enabled Full/Bulk-logged recovery requires
a log threshold. Existing policies can be disabled; the server still validates
identity, recovery model, inventory freshness and target scope.

Each save carries the target revision, current policy revision and a fresh
operation ID. Submitted fields remain locked after an uncertain result, and
Retry same policy save sends exactly that payload and operation ID. Definitive
conflicts require Reload current policy before another edit. Successful responses
must match the submitted settings and next policy revision before the UI reports
success and refreshes assessment. Closing, switching identities or losing admin
scope cancels the active client request; it does not claim that a server write
was rolled back. Reopening reads current state.

Seven focused model/API/role tests and the 26 affected backup/operational checks
pass, along with frontend typecheck/build and asset verification. Mounted
synthetic checks exercise blank creation, an uncertain committed save and exact
retry, conflict reload, rewind restrictions and an admin grant scoped to another
server. The fixture logged identical retry payloads and one resulting revision.
No backend code changed in this stage, so the previously verified native/HTTP
policy tests were not repeated. Historical zone qualification administration UI,
fleet/alert/MCP integration, real replica/TLS qualification and production v2
activation remain open.

```powershell
cd web
node --experimental-strip-types --test tests/backup-policy.test.mjs tests/backup-assessment.test.mjs tests/operations-contract.test.mjs
pnpm run build
```

```powershell
cd web
node --experimental-strip-types --test tests/backup-assessment.test.mjs tests/operations-contract.test.mjs
pnpm run build
```

```powershell
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupEvidenceReadTests'
dotnet test tests/SqlObserver.UnitTests/SqlObserver.UnitTests.csproj --filter 'FullyQualifiedName~BackupAssessmentQueryTests'
dotnet test tests/SqlObserver.ApiContractTests/SqlObserver.ApiContractTests.csproj --filter 'FullyQualifiedName~BackupAssessmentHttpTests'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupV2ManifestTests'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupV2PersistenceTests'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupV2RolloutTests'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupZoneQualificationTests'
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --filter 'FullyQualifiedName~BackupFreshnessPolicyTests'
dotnet test tests/SqlObserver.UnitTests/SqlObserver.UnitTests.csproj --filter 'FullyQualifiedName~BackupHistoricalTimeResolverTests|FullyQualifiedName~BackupV2ParserTests|FullyQualifiedName~BackupFreshnessTests'
# Use the existing explicit Local or Release SQL Server lab contract.
dotnet test tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj --filter 'FullyQualifiedName~BackupV2SourceIntegrationTests'
```
