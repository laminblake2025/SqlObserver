# Availability evidence browser fixture

The existing dashboard fixture now supplies synthetic AG v2 replica and
database-replica pages. It serves the production web build on loopback port 4174;
it never contacts SQL Server or the production API. V1 operational cards return
empty synthetic snapshots so the older-summary disclosure remains accessible.

From `web`, run:

```powershell
pnpm run build
pnpm run fixture:dashboard
```

Open this fixed Rewind URL and select **Availability groups**:

```text
http://127.0.0.1:4174/#/operations?target=11111111-1111-4111-8111-000000000001&range=custom&from=2026-09-29T11%3A00%3A00Z&to=2026-09-29T12%3A00%3A00Z
```

Change the scenario without restarting the server, then choose **Restart both
AG streams**. The default when the file is absent is `primary`.

```powershell
Set-Content -LiteralPath .artifacts/availability-scenario.txt -Value secondary
```

| Scenario | Expected behavior |
| --- | --- |
| `primary` | 51 rows per stream, independently paged 50 + 1. A disconnected replica and a suspect database remain fault states despite complete coverage. |
| `secondary` | Partial coverage and local-only visibility. Configured remote replicas remain present with unknown state and counters. |
| `disabled` | No run receipt, unknown source time, actionable qualification/enablement guidance and disabled next-page controls. |
| `retired` | A retained run receipt with no remaining rows, degraded coverage and newer-cutoff guidance. |
| `stale` | First pages succeed; continuation returns 409. **Restart AG snapshot** returns both streams to page 1. |
| `mismatch` | Streams return different target revisions and an explicit warning against correlating them. |
| `database-error` | The database stream fails with retry guidance while the replica stream stays available. Switch to `primary` and retry only the failed stream. |
| `denied` | Read-role guidance; no automatic retry button. |
| `delayed` | A delayed response for inspecting loading and navigation cancellation. |

Each synthetic request appends its target, stream, cutoff and cursor to
`.artifacts/availability-requests.ndjson`. This is a browser diagnostic record,
not the application's authorization or audit log. Reset the scenario to
`primary` when finished and stop the server with Ctrl+C.

## Verified 2026-09-30

The production build was exercised through the Codex in-app browser:

- Both streams paged independently; previous-page navigation retained the
  original cutoff. The request record matched the opaque stream cursors.
- Editing the Rewind end changed the shareable URL and restarted both streams
  on page 1 at the new cutoff.
- The UTC/local display toggle changed UTC observation labels without changing
  the raw SQL Server local commit timestamp.
- Secondary visibility, disabled collection, retired evidence, stale-cursor
  restart, mismatched revisions and independent failure/retry behaved as above.
- A layout defect found during this check was corrected: tables now keep state
  labels intact, put fault columns before supporting observer/visibility fields,
  and provide a keyboard-focusable scroll region with bounded height and sticky
  column headers. Fifty rows no longer push the controls down a full table page.

The 15 focused client/parser/render tests and production build/asset verification
passed. A local screenshot is recorded at
`web/.artifacts/ag-evidence-browser.jpg`. These are synthetic UI checks; native
SQL Server primary/secondary qualification, real Negotiate/TLS certification and
collector activation remain separate rollout gates.
