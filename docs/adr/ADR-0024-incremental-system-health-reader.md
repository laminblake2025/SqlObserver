# ADR-0024: Read `system_health` incrementally without skipping XE buffers

Status: proposed, 2026-09-29

## Context

The `deadlocks.system-health` collector reads every retained `system_health*.xel`
file on every run and selects the newest `@maximum_rows` deadlocks. This becomes
more expensive as files grow. If more than the row limit arrives between runs,
the oldest excluded deadlocks can disappear at rollover without ever being
collected. The current collector has no source position to distinguish an empty
read from one that omitted data.

SQL Server's `sys.fn_xe_file_target_read_file` accepts paired
`initial_file_name` and `initial_offset` arguments for an incremental read.
Its `file_offset` identifies a **memory buffer**, which commonly contains
multiple events. Passing that offset back skips the entire buffer. Therefore a
page limit on deadlock events, followed by a bookmark at the last returned
event, can silently skip other events in the same buffer. See
[Microsoft's function documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-functions/sys-fn-xe-file-target-read-file-transact-sql?view=sql-server-ver17).

The current asset, manifest, and bundle are checksum-pinned. The collector
runtime already has fenced leases and a terminal commit. A separately written
bookmark could advance after a rejected or failed run, so it must be included
in that same commit boundary.

## Decision

Introduce a new version of the passive `deadlocks.system-health` asset and an
opaque, target-revision-scoped source bookmark. Do not change the version-1
query or its checksum. The bookmark stores the exact file name and byte offset
of the **last fully accounted XE buffer**, plus the configured event-file path
identity, source session identity, run ID, and repository update time. It is
read after the run starts under its lease and is passed to the collector as a
pair. The file path is discovered only from the running `system_health`
event-file target; neither a caller nor persisted cursor may supply a new
read path. Existing rollover-file and file-size guards still apply.

The reader scans source metadata for **all event kinds**, not only
`xml_deadlock_report`. It processes buffers in verified file order and advances
past a buffer only after every event in that buffer has been accounted for.
It reads XML only for supported event kinds. In particular, a buffer with no
deadlocks can advance the bookmark, avoiding repeated reads of ordinary
`system_health` events. A bounded page ends at a buffer boundary; it never
sets its bookmark from the last event of a row-limited page. It must establish
that a buffer is complete before proposing that buffer as the next bookmark.

The collector returns both its observations and a proposed source position.
The fenced PostgreSQL commit validates the target revision, schedule revision,
run, lease, payload, and source position, then persists observations, loss
evidence, and the bookmark atomically. A replay of the same commit is
idempotent. A stale run cannot move the bookmark. Cancellation, timeout,
permission error, parse failure, invalid source path, or a partial read leaves
the previous bookmark in place. Re-reading after such a failure may produce
duplicate events, which the canonical deadlock identity must absorb.

If the bookmarked file is no longer among retained files, the reader does not
interpret an empty incremental result as healthy. It records a source
rollover gap, including the previous bookmark and the earliest retained
source position, and resumes only through a fenced commit that publishes that
gap. A changed event-file target or session identity similarly creates a new
source epoch with a visible gap before a new baseline. Target revision changes
cannot inherit a bookmark from the prior revision. A buffer that exceeds row
or byte limits also becomes an explicit gap; it cannot be silently marked
complete. The commit may advance beyond such a buffer only together with
durable loss evidence describing the skipped buffer. If the next safe position
cannot be established within bounds, leave the bookmark unchanged and report
the collector as partial instead of looping as a successful empty read.

The SQL Server query remains read-only and never starts, stops, or changes an
XE session. The collector retains per-run row, XML-size, response-byte, and
command-time budgets. A source timestamp is evidence time, not a substitute
for the file/offset bookmark. Readers must still deduplicate deadlocks across
replays, reconnects, and the first scan of a new source epoch.

## Delivery and verification

1. Add a forward migration for the bookmark and source-gap records, with
   forced row-level security, restrictive grants, target revision linkage,
   retention relationships, and catalog checks. Extend the fenced deadlock
   commit rather than adding an independent cursor write.
2. Add a version-2 pinned SQL asset and collector contract that return the
   source file, buffer offset, event kind, timestamp, and bounded XML for
   supported kinds. Determine and qualify file ordering and complete-buffer
   detection against SQL Server 2019, 2022, and 2025 before enabling the new
   schedule. Fail closed when those assumptions cannot be established.
3. Wire the bookmark through run start, execution, and commit. Migrate an
   existing target by making its first version-2 scan an explicit baseline;
   do not infer a precise position from version-1 deadlock timestamps.
4. Test multiple events at one offset, a page ending inside a buffer,
   non-deadlock-only buffers, duplicate commit, stale lease, target revision
   change, session restart, deleted bookmark file, oversized buffer, and
   cancellation between read and commit. Run the read-only
   [bookmark probe](../../tools/lab/verify-system-health-bookmark.ps1) with the
   collector login on SQL Server 2019, 2022, and 2025. It checks a retained
   shared-offset buffer; separate controlled lab tests must cover rollover and
   restart. Mocked rows cannot establish the XE function's ordering and
   rollover behavior.

Until this contract is implemented and qualified, the version-1 collector
continues to use its bounded full-file read. The dashboard must not describe
an empty version-1 result as proof of complete deadlock coverage.
