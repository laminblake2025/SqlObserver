# Saved incident storage and API

The plan's **Open incident** workflow now has storage, an API and a dashboard
action. Saved-investigation browsing and report/export integration remain to
be implemented. Analytics-generated incidents continue to work.

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
