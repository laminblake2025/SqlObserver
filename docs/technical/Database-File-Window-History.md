# Database file allocation in the shared window

Resources must not substitute today's file allocation or autogrowth settings for
a Rewind investigation. This feature reads retained database-file receipts and
their actual source rows. It adds a read contract; it does not change collection,
infer file/database incarnations, or identify an allocation change as an
autogrowth event.

## Identity and time

The receipt list uses the shared half-open completion-time window. Each receipt
retains its immutable run ID and sample target revision, separately from the
current target revision authorizing the request. Failed, lossy, empty and missing
payload receipts remain visible. The default is the most recent receipt in the
window, even if it has no usable file rows. Source observation, ingestion and
run completion timestamps have separate labels.

Selecting or paging an investigation fixes the global Rewind window. Exact
receipt identity and the existing database/file selection survive URL reload and
history. Changing context cancels and fences old work. A missing, expired,
out-of-window or invalid receipt cannot become the newest receipt or another
file. Live reads follow the workspace clock until investigation begins.

The source has no durable database/file incarnation identity. Numeric database
and file IDs locate a row only within its exact receipt. File-ID reuse cannot
justify continuous configuration history, growth attribution or reset-free I/O
calculations across receipts. Existing selected-window latency evidence retains
its separate source and continuity rules.

## Reads and bounds

Target-scoped Viewer, Operator or Target Administrator grants govern private,
no-store reads. Lists scan the complete bounded window before paging by
completion time and run ID descending. An exact snapshot independently reads
one run, ordered by database ID and file ID. It does not claim whole-window
coverage or depend on the selected receipt appearing in a loaded list page.

The maximum window is seven days, with 10,000 receipts and 250,000 retained file
rows for a list cohort, and 10,000 files for one receipt. Pages hold at most 100
items. Exceeding a bound requires narrowing the window or source; no prefix is
represented as complete. Exact reads can restore a receipt independently of an
overflowing list cohort, subject to their own source bounds.

Protected cursors bind target/revisions, window, read kind, exact receipt where
applicable, page size, source cutoff and immutable read epoch. They expire fifteen
minutes after the original issue. List epochs include all bounded source facts;
exact epochs include the selected run and all its facts. Retention, payload or
policy changes invalidate unread continuation. Cached received pages remain
historical receipts, subject to authorization and context fences.

All reads use a five-second original repository budget. The migration reads
authoritative file snapshots and canonical collection-run identity/outcome data,
including after original run mirrors are sealed. It checks native target scope,
current target revision, row accounting, loss, schema and source-clock skew.

The authoritative file table currently has no supported scheduled expiry. Its
retention duration is therefore unknown in this read contract; policies for the
unused file snapshot mirror cannot be advertised as source retention. Policy and
source-epoch changes exercise continuation invalidation. Supported canonical
file-source expiry remains unqualified.

## Configuration and interface

Byte sizes, increments and cumulative counters travel as exact decimal Int64
strings. Percent growth is separate. A null maximum retains the source's existing
unlimited-or-unknown meaning. Reported zero growth is disabled; conflicting byte
and percent modes are explicit. These settings do not establish used log space,
free disk space, a forecast, current health or whether growth can succeed.

The shared dark theme, keyboard controls, wrapped identifiers, contained tables
and quiet evidence details apply to receipt selection and exact file browsing.
Refresh replaces an investigation only after a validated success. Failure retains
the received evidence and retry intent. Denial purges it. Original queries and
continuations stay bounded; late responses cannot cross context or unmount.

## Qualification

Owning checks must cover real canonical commits and PostgreSQL reads, sealed
mirrors, target grants, current versus sample revision, all receipt outcomes,
exact restoration, pagination/retention epochs, source clocks, overflow and exact
Int64 values. Production HTTP tests replace only authentication. Mounted and
built-App journeys must cover shared time/URL behavior, ownership, refresh,
denial and unavailable evidence, with axe, keyboard, responsive and screenshot
verification against final assets. Browser fixtures are synthetic; these checks
do not certify SQL Server collectors or physical file incarnation identity.
