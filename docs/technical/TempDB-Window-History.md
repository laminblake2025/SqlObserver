# TempDB completion-window history

Health checks → TempDB follows the workspace Live/Rewind window. The window selects **repository completion times**, including failed collection attempts. The default is its newest completed attempt. A failed, missing or empty receipt never selects an older successful sample as a substitute.

The source is the existing canonical `collection_run_v2` identity/outcome and retained `tempdb_snapshot` / `tempdb_file_snapshot` payload. Migration `0361_tempdb_window_history.sql` adds read functions. It does not change collectors, collection permissions, retention defaults or monitoring enrollment.

The summary and all file pages belong to one `runId` and `sampleTargetRevision`. Current target revision authorizes the request; the sampled revision identifies the historical payload. Byte facts are canonical decimal strings through PostgreSQL, JSON and the screen, including values above JavaScript's safe integer range.

`observedAtUtc` was stamped by the monitoring worker. The screen labels it **Collector observation time** and shows repository completion separately. It does not label this clock as native SQL Server UTC. Complete source accounting establishes neither continuous TempDB pressure nor a health verdict. Failed attempts, source loss, empty captures, revision boundaries and missing payload are reported separately; their coverage counts can overlap.

## Read contract

Both GET routes require authentication and a same-target Viewer, Operator or TargetAdministrator grant. Responses are private and must not be cached.

| Route | Required fields | Purpose |
|---|---|---|
| `/api/v1/observation-targets/{target}/tempdb/history` | `targetRevision`, `fromUtc`, `toUtc` | Page completed attempts and whole-window source accounting |
| `/api/v1/observation-targets/{target}/tempdb/history/{runId}` | Above plus `sampleTargetRevision` | Independently restore one exact summary and its file pages |

UTC query timestamps must end in `Z` and have at most six fractional digits. Windows are half-open and at most seven days. Optional `limit` defaults to 25 and is capped at 100. The screen uses 100. Repeated, unknown, incomplete and malformed query fields are rejected. An exact read is independent of the currently loaded receipt page and of a whole-window overflow.

Unread `cursor` continuations are authenticated and bind their route kind, target, current revision, exact window, page size, original cutoff, source digest and source identity/anchor. They expire 15 minutes after original issue; paging does not renew them. Native reads recheck that expiry and the retained source under repeatable-read isolation. A changed revision, source manifest or continuation returns 409. The manifest is bounded to 10,000 attempts / 250,000 retained file rows, with at most 10,000 files per source. Overflow returns 422; JSON responses are capped at 512 KiB. No raw database diagnostics or private manifest fields are returned.

## Investigation behavior

Shareable URLs use `check=tempdb`, `tempDbRun` and `tempDbRevision`. Selecting a Live receipt fixes the exact received completion window in the shared URL. Reload and browser navigation restore that tuple independently of the receipt catalog page. Invalid or repeated pins remain explicit until cleared or replaced. Compatible database/display-context changes retain the pin; another target or window removes it. Database scope does not narrow server-wide TempDB evidence.

Browsing retains received pages and their cutoff during workspace polling or refresh. Explicit source refresh rereads the same exact source/window. **Return TempDB to latest in window** clears the pin and rereads the newest attempt in that window; it does not jump outside Rewind. Ordinary read failures preserve received evidence and retry the same request. Cancellation aborts the owned request. Context, selection and mount guards reject obsolete responses. HTTP 401/403 clears receipts, summary and files, aborts sibling reads and requires an explicit access retry.

Receipt and file tables start behind separate disclosures. Controls use dark workspace tokens, minimum 44-pixel targets and visible inset keyboard focus. Wide tables scroll within their own labeled region; document-level overflow is not required.

## Verification boundaries

The owning unit/API/PostgreSQL groups, existing paired TempDB retention and canonical-reference groups, mounted Operations/TempDB groups and built-App journeys are qualified by the lead after integration. Screenshots and final-code evidence belong to `artifacts/dashboard-tempdb-history-2026-10-04/`.

Browser fixtures are explicitly synthetic. PostgreSQL tests use real production migration/commit/read paths and the real HTTP pipeline, with authentication replaced for test identities. They do not constitute a native SQL Server capture or lab qualification. The configured lab SSH route timed out during a read-only preflight; no SQL command ran or remote file was written. Native CPU, SQL Agent activity and failover qualification remain separate outstanding dashboard dependencies.
