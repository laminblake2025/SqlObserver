# SQL Agent history in the shared investigation window

The server dashboard requests SQL Agent history for its actual Overview window. Only failed job-outcome rows become timeline markers; failed steps, retries and cancellations remain supporting history. The dashboard shows at most ten failed outcomes in its evidence table and labels the first-page and source-truncation limits. Its history link opens the SQL Agent health-check tab with the same fixed UTC bounds.

History is selected by **first observation**, not execution time. `firstObservedAtUtc` is when monitoring first captured the history row. `sourceLocalStart` is an offset-free SQL Server clock value and stays unchanged when the display switches between local time and UTC. The interface does not infer a timezone, expose job names/messages, or imply that an empty page proves healthy execution.

The health-check tab follows the global time context and survives time-range and target changes in `check=agent` URLs. Each request is capped at 50 rows and seven days. Longer ranges display a corrective message without an Agent request or paging controls. Each continuation keeps its exact window and opaque cursor, replaces the displayed page, and rejects a changed snapshot. Target identity, configuration revision, exact coverage bounds and the exclusive upper timestamp boundary are checked before rendering. Scope changes hide old evidence immediately and abort pending reads.

## Focused validation

Run from `web`:

```powershell
node --experimental-strip-types --test tests/agent-history.test.mjs tests/server-dashboard.test.mjs tests/dashboard-model.test.mjs tests/operations-contract.test.mjs tests/overview-model.test.mjs
pnpm run build
```

On 2026-09-30 all 33 focused tests and the production build passed. The tests exercise real model/client code, microsecond boundaries, malformed and mismatched responses, fixed-window cursors, route restoration, rendered clock explanations, inert text and coverage warnings.

Browser verification used the built assets and loopback-only `tools/overview-fixture-server.mjs`, with synthetic Agent history from `tools/agent-history-fixture.mjs`. It verified:

- Dashboard markers explicitly say first observed and omit step/retry duplicates; the compact evidence table shows ten of 17 failed outcomes in the first 50 history rows.
- The dashboard link restores the exact window and selects SQL Agent.
- The dashboard attention link selects SQL Agent, and changing the target retains that tab and shared UTC bounds.
- Page 1 displays 50 rows; Page 2 replaces them with 14 rows and disables continuation. Refresh returns to Page 1. Fixture requests retain the same UTC bounds and limit.
- UTC display changes first-observed formatting while leaving the offset-free source start unchanged.
- Switching from one hour to six hours during delayed requests removes the old table; the six-hour result wins and the SQL Agent tab remains selected.
- A nine-day shared URL displays the seven-day limit and issues no Agent history read.

Local ignored artifacts are `web/.artifacts/agent-history-smoke.png` and `web/.artifacts/agent-history-smoke-requests.json`. These checks cover the frontend workflow with synthetic responses. They do not qualify native SQL Server collection, Windows authentication or external release evidence. No backend contracts or database migrations changed in this slice.
