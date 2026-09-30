# Saved incident storage and API

The plan's **Open incident** workflow now has storage, an API and a dashboard
action, saved-investigation browsing and materialized reports/exports.
Release certification for the new report kind remains outstanding.
Analytics-generated incidents continue to work.

## Contract

`POST /api/v1/observation-targets/{targetId}/incidents/saved` accepts JSON with
`expectedTargetRevision`, `operationId`, `fromUtc`, `toUtc`, `title` and `notes`.
An Operator or TargetAdministrator grant must apply to that exact target.
The past UTC window is half-open, at most seven days, with microsecond-aligned
timestamps. Titles allow 200 UTF-8 bytes; notes allow 8192 UTF-8 bytes, including
tabs and line breaks. Request bodies are limited to 16 KiB, reject unmapped
fields, and use the existing authenticated origin and mutation-rate boundaries.

The server captures existing analytics evidence packets for the target/revision
and window. It copies the most recent bounded prefix, at most 200 packets and
1 MiB of evidence JSON. `evidenceTruncated` explicitly reports exceeding either
limit. An empty capture is allowed and returns an empty evidence array; it does
not establish complete coverage of the investigation. This command does not
collect new SQL Server samples or accept evidence supplied by the browser.

The response contains the saved title, notes, concrete window, target revision,
thread ID, operation ID, save time, truncation flag and copied evidence. Copies
remain available after the original source partitions expire. Their lifetime
is the saved investigation's lifetime; they are not raw-telemetry partitions.

`GET /api/v1/observation-targets/{targetId}/incidents/saved/{threadId}` reads the
same snapshot for a matching Viewer, Operator or TargetAdministrator. Both
responses use `Cache-Control: no-store`. The normal MCP incident discovery and
evidence projections recognize the new thread and its durable evidence copies.
MCP's existing target-revision and snapshot fences still apply.

## Atomicity and retries

Migration `0288_saved_incident_workspace.sql` creates private, forced-RLS
storage and fixed SECURITY DEFINER read/write functions owned by the migrator.
Only the server role can execute them; application roles cannot read or mutate
the tables directly. Functions require a matching transaction-local target
scope. A first save also checks and locks the current active target revision.

Retries retain the same operation ID, actor and exact command. The database
serializes that ID, compares its canonical digest and returns the original
snapshot without recapturing later evidence or duplicating its audit entry.
Changing the actor, target, window, title, notes or revision conflicts. A new
correlation ID is allowed on a retry. Incidents, copies, generations and the
`incident.save` audit record commit together. Audit details contain identities,
counts and the digest, excluding plaintext titles, notes and source evidence.

## Validation, 2026-09-30

- Three application-service tests cover matching grant scopes, invalid windows,
  Unicode byte limits, controls and conflicting repository results.
- Four real HTTP-pipeline tests cover save/read, identity and origin boundaries,
  media types, malformed/unmapped/oversized bodies, conflict and missing results.
- Five PostgreSQL 18.4 tests cover concurrent replay, immutable capture, source
  partition retirement, row/byte truncation, stale revisions, empty evidence,
  function/table privileges, cross-target scope and rollback on audit failure.
- All nine existing PostgreSQL MCP incident-list tests pass after the migration,
  covering publication fences, backdated writers, dense paging and snapshot
  visibility for analytics-generated incidents.

These **21 focused tests** passed. Native tests used disposable PostgreSQL 18.4;
no SQL Server collector changes required a VM run. No full release-suite claim
is made by this validation.

## Dashboard workflow and validation

Operators and TargetAdministrators see **Open incident** on the server
dashboard. Opening the draft freezes its concrete window and server revision;
changing the shared range or refreshing live charts does not move that draft.
The save command is immutable once dispatched. An uncertain result locks the
title and notes and offers **Retry same save**, preserving the operation ID and
exact request body. Closing an unverified draft does not undo an operation that
may have committed. A conflicting or invalid result is never shown as success.

Confirmed saves open a URL containing the target, saved window and incident
selection. Reloading the URL reads the durable snapshot. Monitoring readers
can reopen the investigation; saving still requires the matching mutation role.
Notes and evidence render as text. Empty, truncated and partial captures explain
their limits rather than implying healthy or complete coverage.

The shared custom-window helper preserves PostgreSQL microsecond precision in
UTC URLs. Ordinary millisecond and local time controls retain their display
behavior. Tests include equivalent `Z`/`+00:00` receipt timestamps and a window
only one microsecond wide.

The frontend validation passed **114 related tests**, including 11 new runtime
and rendered-component tests for the incident flow. TypeScript checking, the
production build and asset-manifest verification passed. A synthetic loopback
browser exercise on port 4187 verified a frozen draft across a range change,
locked inputs after a simulated post-commit 503, two identical request bodies,
successful retry, URL reload, escaped notes, and a Viewer reading the result
without a save action. This browser fixture validates UI behavior; the native
PostgreSQL tests above establish storage and authorization behavior separately.

The synthetic handler is isolated in `web/tools/saved-incident-fixture.mjs` and
the Overview fixture. It is never imported by production code. For visual QA,
run the existing `pnpm run fixture:overview` script after building; the default
port remains 4185 and `SQLOBSERVER_OVERVIEW_FIXTURE_PORT` selects another loopback
port. `/__incident_fixture?role=operator&mode=uncertain` selects the retry case.

## Browsing saved investigations

The dashboard's **Saved investigations** action is available to matching Viewer,
Operator and TargetAdministrator readers. It loads summary metadata only, on
demand, independently of the chart window. Summaries include the title, saved
time, retained window, historical target revision, evidence count and truncation
flag. Notes and evidence payloads are read only when an investigation is opened.
Links restore the saved window and incident selection. Empty captures remain
explicit when opened; a zero evidence count is not a health verdict.

`GET /api/v1/observation-targets/{targetId}/incidents/saved` returns `targetId`,
`snapshotUtc`, `items`, `hasMore` and `nextCursor`, with `Cache-Control: no-store`.
The default page size is 20 and the maximum is 50. The newest save comes first,
using `(savedAtUtc, threadId)` descending keyset order. Historical target
revisions are included while access is checked against the current target scope.
The list is deliberately separate from automatically correlated incidents,
which do not have a user-saved title, notes or snapshot.

Migration `0289_saved_incident_listing.sql` adds the supporting index and a
private, forced-RLS publication fence, initialized with every target so empty
lists are also protected. A new save advances that fence atomically in the same
transaction. List reads lock it until the page transaction finishes.
A continuation whose publication changed returns 409 and the UI offers Refresh;
it never merges pages from different publications. Cursors use the shared API
signer, bind the target, snapshot, publication, boundary and limit, and reject
tampering or a changed page size. The existing API signing-key configuration
also applies here. SQL functions remain migrator-owned and executable only by
the server role; direct application-role table access is denied.

Validation on 2026-09-30 passed four saved-incident application tests, the
migration-preamble check, five real HTTP-pipeline tests, eight PostgreSQL 18.4
tests and 13 incident frontend tests. The native cases include dense equal-time
paging without duplicates or gaps, historical revisions, late-publication
conflicts, a concurrent first save waiting on the empty-list fence, malformed
direct SQL bounds, cross-target scope and role privileges.
TypeScript checking, production build and asset-manifest verification passed.
The earlier applied migration 0288 retains its bytes; the preamble check now
recognizes its exact existing order while enforcing the standard order on 0289.

A Viewer browser exercise on the synthetic loopback fixture verified two pages
of 20 and five investigations, 25 distinct titles, an older investigation link,
URL reload, retained notes/window and no save action. `mode=browse` seeds those
25 synthetic snapshots. This fixture does not establish database behavior; the
native PostgreSQL cases above do.

## Saved-investigation reports and exports

Opening a saved investigation now offers **Generate investigation report** to
its matching monitoring readers. The command posts `reportKind:
"saved-investigation"`, its `threadId` and an operation ID to the existing target
report endpoint. It accepts no caller-supplied window: the notes, window,
historical revision, capture limit and evidence all come from the durable saved
snapshot. The four window/current report kinds retain their definitions.

Migration `0290_saved_investigation_reports.sql` adds the fixed fifth report
definition and a server-only, migrator-owned materializer. The original window
materializer is kept as a private helper behind an allowlisted entry point;
neither entry point accepts a saved investigation as a free-window report.
The report run uses the saved target revision and the ordinary 24-hour expiry.
It copies both sections and commits an accepted audit entry atomically. Exact
concurrent retries return the same run; another actor or thread with the same
operation conflicts. Missing or expired saved reports are explicit failures.
Current target-scoped read authorization continues to apply to every export.

The **investigation** section contains `field`, `part` and `value`. Concatenate
the ordered parts of each field, notably `notes`, to recover its original text.
The **saved-evidence** section carries packet identity, observed time, kind,
visibility, confidence and all source digests/cutoff metadata, followed by
`part` and `evidenceJson`. Each `evidenceJson` cell is a JSON string: decode it
as a string, then concatenate the ordered decoded parts for a packet to obtain
its original evidence JSON. The string envelope prevents a split fragment
starting with a spreadsheet formula sentinel from being changed by formula
neutralization. Notes keep their line breaks and Unicode spelling; formula
sentinels in free text still receive the protective CSV apostrophe.

Parts contain at most 1000 Unicode characters. All cells remain below the
existing 4096-byte limit; existing row, page, materialization and export limits
remain in force. Only saved-note value cells permit CR/LF/TAB. HTML is inert
and displays multiline text, with the existing strict CSP and size bound. A
large capture can exceed the printable-view bound; the separate CSV exports
preserve all materialized details and evidence within their larger bound.
The target report routes now consistently use `no-store`.

The dashboard retains the same report operation on an uncertain outcome and
offers **Retry report generation**. It verifies the returned target, saved
revision, kind, version, state, expiry and thread-bound parameter digest before
showing links to printable HTML, saved details CSV and saved evidence CSV.

Validation on 2026-09-30 passed 16 report/preamble unit checks, nine relevant
HTTP checks, five affected native PostgreSQL report checks and 17 frontend
incident/report checks. Native tests use concurrent retries, long Unicode
notes, more than 200 evidence-part rows, actual source partition retirement,
historical revisions, missing/conflicting requests, restricted helper/function
privileges, atomic audit rollback and full CSV-to-JSON reconstruction. The
three original native report tests still pass through the new entry point.
The real HTTP pipeline verifies Viewer report creation, escaped HTML, multiline
CSV, successful export auditing, no-store and invalid parameter rejection.
TypeScript, the production build and asset verification pass.

A synthetic Viewer browser check simulated a post-commit 503, confirmed two
identical report request bodies, and then displayed all three export links.
That fixture checks UI behavior only. The release report certification contract
and its source pins still cover the original four kinds and must be expanded
and recertified for the saved-investigation kind; this is not a release-suite
pass or a complete revamp claim.
