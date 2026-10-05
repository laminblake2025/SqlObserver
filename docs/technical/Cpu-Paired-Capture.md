# Paired CPU capture boundary

This note describes the staged SQL/Windows CPU capture dependency. A local socket
association candidate and valid raw-counter arithmetic are diagnostic evidence;
they do not admit a measured SQL-versus-host CPU comparison to the product.
`NativeQualification` remains `Absent`, `TargetComparisonAdmitted` remains `false`
and hardware identity is not verified.

The source does not replace the existing SQL scheduler CPU or collector-host CPU
series. Those sources have different semantics. Neither a configured host binding,
SQL node name, process basename, conventional port nor caller-supplied PID proves
that local Windows counters belong to the monitored SQL endpoint.

## Owned capture sequence

The production SQL connection policy uses Windows integrated authentication,
mandatory validated TLS, explicit reviewed endpoint routing and disabled MARS.
The SQL witness reader owns one connection for:

1. SQL before-witness.
2. Local TCP owner-table before-capture.
3. One owned raw PDH pair.
4. Local TCP owner-table after-capture.
5. SQL after-witness.

SQL connection ID, SQL session ID, SqlClient connection ID and the collector
process ID are distinct continuity evidence. The SQL source reports the SQL PID;
the source owner captures its own collector PID. Local helpers accept fixed,
bounded reads, not arbitrary scripts, counter paths, remote hosts or credentials.

The identity-attested read path additionally checks the independently provisioned
monitor SID against SQL's current and original login on this same owned
connection before and after native sampling. Both boundaries require integrated
encrypted TCP, a non-sysadmin context and no effective `CONTROL SERVER` or
`ALTER ANY LOGIN`. The typed evidence contains comparison booleans and exact
capture context, without returning raw SIDs or login names. A separate discovery
connection or Windows process-token check cannot substitute for either boundary.

These are actor and connection binding checks. They do not prove uninterrupted
permissions or absence of all elevated grants, qualify the native fault/version
matrix, establish hardware identity, or change CPU comparison admission. SQL's
[SUSER_SID](https://learn.microsoft.com/en-us/sql/t-sql/functions/suser-sid-transact-sql)
reflects the current execution context; [ORIGINAL_LOGIN](https://learn.microsoft.com/en-us/sql/t-sql/functions/original-login-transact-sql)
identifies the original connecting login. Missing or mismatched evidence fails
closed; it is not replaced with the expected identity.

This is the internal opt-in `ReadAttestedBracketAsync` path used by the native
qualification harness. It returns `SqlCpuAttestedWitnessBracket<T>` with the
ordinary bracket and separate `BeforeIdentity`/`AfterIdentity` proofs. The order
is identity-before, SQL before-witness, native completion and disposal, SQL
after-witness, then identity-after, all under the original capture deadline.
An initial identity refusal starts no native sampler; a final refusal publishes
no successful bracket.

Public `ReadBracketAsync` remains deliberately unattested. The existing staged
`PairedCpuCaptureSource`, private schema-1 capture writer and diagnostic history
are unchanged. Their retained rows do not gain identity proofs through this
change. The fixed application identity query does not alter the pinned v1/v2
SQL witness assets or accept an arbitrary script or qualification flag.

A candidate requires complete bounded IPv4 and IPv6 owner tables and exactly one
established SQL-owned server tuple plus its collector-owned reverse at both
captures. Same-handle process creation epochs and explicit `WAIT_TIMEOUT` liveness
must remain consistent. Running-process exit FILETIME is retained but does not
prove liveness. Host binding, independently observed machine/boot witness,
target/run/revision, source schema, owned connection and clock brackets must also
match. Unsupported, ambiguous, missing or denied evidence remains a gap.

The local coordinator owns one deadline of at most 26 seconds inside the SQL
reader's original request budget of at most 30 seconds. Each child must stop and
dispose before the next stage. Completion represents stopped work and cleanup,
not merely a returned DTO. Cancellation is advisory; unproven termination or
cleanup retains target ownership/quarantine. Late evidence cannot turn the
original failed request into a successful capture or allow overlapping work.

## Clocks and numerical evidence

SQL source UTC, native host UTC, monotonic observations, native process creation
FILETIME and each PDH counter's raw anchors remain distinct. Offset-free SQL
startup time is source local time. No timezone conversion invents physical startup
UTC, and cadence or elapsed request time never substitutes for a PDH denominator.

The raw process and OS counters carry their actual counter type, API/provider
status, time base, signed Int64 numerator/denominator values, local collection
FILETIME and `MultiCount`. The approved types are the process 100ns timer and OS
idle inverse timer. Unsupported/multi-timer types are not coerced into these
formulas. `MultiCount` is retained but is not a denominator for the approved
non-multi types. The process denominator uses the independently observed active
logical processor count across processor groups.

Per-counter eligibility and interval alignment are separate checks. Matching
collection queries or wall-clock timestamps alone do not establish a paired
interval. Legacy Process mapping requires exactly one valid `ID Process` row for
the requested PID, with that exact instance name occurring once in each of the
independently ordered ID and CPU arrays. Duplicate names belonging to unrelated
processes do not invalidate a unique target mapping. Duplicate target names or
multiple valid target-PID rows remain ambiguous; no first-row or ordinal join is
accepted. All rows remain bounded by the existing 256 KiB/1,024-row limits. The
selection policy is compiled for managed tests and embedded verbatim in the fixed
Windows PowerShell helper. Ambiguous target mapping makes process arithmetic
unavailable; it does not erase independently valid OS evidence. Source identity,
boot/process/topology/clock boundaries, counter reset/status/type/time-base/range
failures and interval mismatch remain explicit.

The existing qualification harness keeps the original staged process provenance
unavailable. Its separately labelled test-only numerical view is created only
after an actual association candidate, preserves the same raw objects and never
changes source admission. A harness arithmetic pass cannot be reused as an
admitted production metric.

## Per-run capture record

`CpuPairedCaptureObservation` is source schema 1. Its own version is separate from
the version-2 SQL socket witness, version-2 raw CPU helper and version-1 TCP helper.
The record binds target, target revision and canonical run, with optional host
binding/profile revisions that must match when present. It contains at most six
source frames and a bounded 65,536-byte capture payload.

The retained evidence consists of sanitized SQL before/after witnesses, sanitized
TCP before/after frames, original raw CPU endpoints, ID Process counters, process
creation/liveness/topology witnesses and executable fingerprints. SQL startup
identity and cumulative process user/kernel times remain source evidence. Raw
socket address bytes, endpoint routing details and process names are omitted.
TCP rows preserve observed state/owner and source-derived forward/reverse
direction; the complete-table counts remain distinct from retained matching rows.
Intentional exact-tuple filtering is not a claim that the entire TCP tables were
stored, nor is it source truncation.

This sanitization preserves diagnostic attribution while limiting replay: the
stored projection cannot reconstruct the discarded exact endpoint comparisons.
A private candidate verdict reflects the original source derivation. It is not a
new PostgreSQL or future-reader attestation of native association.

The record retains numerical eligibility, gap reasons and native interval anchors,
not numeric percentages. Both public percentage properties and numerical-eligibility
percentage properties remain null. Complete captures have `Partial` state because
source admission is absent; incomplete captures have `Unavailable` state. Neither
state is a health verdict, and an absent field is not zero-filled.

Byte accounting distinguishes known parsed helper UTF-8 lengths from the SQL
logical-response upper bound. A null helper length means unknown; it is never a
zero measurement or proof that no I/O occurred. Successful parsing records the
exact received UTF-8 length, including a valid wrong-host refusal. If parsing
throws and the reader returns `invalid_source_output`, the length remains unknown.
The SQL upper bound is declared accounting, not a measurement of TDS wire traffic. Native table row
counts, retained rows, source frames and collector output items are different
quantities and must not be relabelled as one another.
Generated receipt output bytes are accounted independently from received source
bytes. An unavailable-host receipt can contain useful refusal metadata with zero
source frames and zero response bytes; generating that metadata does not mean a
SQL or helper read occurred.

## Source and canonical persistence

`PairedCpuCaptureSource` resolves the persisted host target within the original
request budget. The target and target revision, binding/profile revision, bound
state and CPU capability must match before it opens the SQL/native bracket.
Missing or denied host evidence returns an explicit unavailable capture without
pretending that SQL or Windows frames were received. Resolution and capture do
not receive a fresh deadline; cancellation and elapsed-budget checks also run
before accepting the completed bracket.

The source evaluates the original SQL/socket/process association before creating
a private arithmetic view. That view changes only the declared process provenance
used by the numerical qualifier. The retained CPU endpoints keep their original
`Unavailable` process provenance. A failed candidate does not unlock process
arithmetic; a valid candidate with absent or invalid counters can still retain
the raw frames and an explicit numerical gap.

`PairedCpuCaptureCollector` emits at most one private capture and no metric rows.
Its version-1 manifest bounds the capture to six frames, 65,536 output bytes and
212,992 accounted response bytes. The adapter checks the discovered Windows SQL
version, integrated authentication, encrypted transport, non-sysadmin state and
version-specific read permission. A completed private receipt can have a
`Succeeded` collector outcome while its capture remains unavailable or partial:
that outcome means the bounded receipt was produced, not that CPU source
qualification passed. Exceptions and validation failures remain canonical
failure outcomes with no usable capture. There is no production registry entry
or automatic schedule activation in this checkpoint.

Migration `0359_cpu_paired_capture.sql` adds the private partitioned
`telemetry.cpu_paired_capture_receipt` table and the atomic
`control.commit_cpu_paired_capture_collection_run` writer. The explicit
`CpuPairedCapturePersistencePayload` projection excludes raw routing data and
names. Its closed JSON shape binds the source schema, target, revision, run,
outcome, refusal reasons, frame counts and private evidence. PostgreSQL recomputes
the canonical JSONB SHA-256 completion digest, validates the projection and
hard-rejects admitted comparison flags or non-null percentages.

The writer checks the worker lease, canonical run identity/request digest,
immutable collector contract, schedule ownership and current active target
revision. Exact completion replay returns the original receipt before checking
the advanced schedule. Divergent completion data cannot rewrite the run. A fresh
completion stores the private receipt, canonical outcome and applicable
visibility-gap accounting, then advances the schedule in the same transaction.
Failure receipts retain a null capture rather than fabricated source frames.

The table and daily partitions use forced target row-level security and
append-only mutation guards. Collector and Server roles have no direct table
access; the collector invokes the bounded writer. The existing internal retention
dependency role has SELECT and intentionally bypasses row-level security so its
reviewed dependency function can protect evidence across the fleet. It is not a
public history-reader role. Ordinary roles remain subject to the target policy;
the native test uses a temporary non-bypass role without changing production grants.
The reviewed canonical-run
dependency inventory includes the receipt's run reference, so canonical identity
retirement cannot silently remove its evidence. Partition creation checks native
ownership, range, registry and immutability. The explicit staged
`control.ensure_cpu_paired_capture_partition_set` helper requires the existing
catalog-reconciliation worker lease before and after creation. Providing this
helper does not register or activate the production CPU collector.

The `cpu_paired_captures` retention policy starts disabled with a null lifetime
and three daily partitions preserved. Its native revision/audit policy pipeline
requires a global Security Administrator context and a finite day-based lifetime
of 90 through 3,650 days when supplied. These native policy bounds do not activate
retention. No cross-run CPU checkpoint, inferred baseline
or continuity claim is stored.

## Open qualification and product work

The [native qualification guide](../../tools/qualification/paired-cpu/native-sql-qualification.md)
defines the opt-in monitor identity, independently provisioned host binding,
same-owned-session before/after SQL identity evidence, exact commands and limited
scenario assertions. Process-token checks and capability discovery remain
separate prerequisites. A pre-cancelled capture produces no bracket identity
proof, and a successful identity pair is not a complete grants audit.

The broader native matrix remains open: actual SQL Server 2019/2022/2025 under the
monitor identity, wrong-host/remote/multiple-instance and denied-permission cases,
restart/PID/boot/host-binding changes, controlled load, localization, processor
groups/topology and in-flight provider cancellation/timeout/termination. Managed
fixtures and local non-SQL ABI probes do not close those gates.

## Retained capture diagnostics

The Resources screen reads canonical completed `cpu.paired-capture` attempts through
the protected `/api/v1/observation-targets/{instanceId}/resources/cpu/captures`
list route and its `/{runId}` exact route. Both bind the current target revision
and a shared completion-time window of at most 31 days. An exact pin also names
the sample revision; it can retain older evidence without treating that evidence
as the current target configuration. Failed attempts remain visible instead of
falling back to an older successful capture.

List continuations preserve the original cutoff, fifteen-minute expiry and
retained-manifest epoch. The list is bounded to 10,000 attempts, with at most 100
receipts per page. Exact reads remain available independently of that list bound.
The Server role invokes the scoped read function and has no direct private-table
grant. The read function validates the immutable stored envelope before projecting
safe diagnostic fields. Routing tuples, session identities, fingerprints and
process names remain private. Source clocks preserve seven fractional digits;
raw signed counters use exact decimal strings.

The screen distinguishes receipt coverage from source qualification. Every response
keeps qualification absent, hardware verification and comparison admission false,
and CPU percentages null. An available receipt list does not establish a CPU
baseline, health status or continuity between runs. A missing or expired private
payload yields a visible gap, with the canonical attempt preserved.

Resources URLs retain an exact CPU run and sample revision through the shared
window and database controls. Malformed pins remain visible until explicitly
cleared or replaced. Paging, refresh, cancellation and access changes fence stale
responses; a denied read purges retained content until access is explicitly retried.

MCP projections, public CPU comparison and dashboard series remain separate work.
This diagnostic history does not complete CPU dashboard support. Current/long-running SQL Agent job data
and qualified AG transition sources remain independent open dependencies; a
failure-history duration or sampled role change does not complete them.

This document describes source boundaries and implemented contracts. It records
no executed test, Windows/SQL lab qualification or product admission result;
those results belong to the lead's preserved qualification evidence.
