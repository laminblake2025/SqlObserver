# Retained SQL Agent activity history

This qualified slice reads the canonical receipts from [activity observations](Sql-Agent-Activity-Observations.md) through migration **0372**, scoped HTTP reads and the SQL Agent observations panel. It does not qualify live running-job counts, elapsed time, a current step or long-running alerts.

## Window and exact selection

The list route is `/api/v1/observation-targets/{instanceId}/sql-agent/activity/observations`. It binds the current target revision and a UTC completion window. List items summarize completed attempts, including failures and missing capture payloads. Full job rows are returned only by exact detail at `/{runId}`, bound to the selected sample target revision independently of the loaded list.

The bounds follow existing CPU history: a 31-day window, 100 receipts per page, a 10,000-attempt cohort and a 15-minute protected cursor carrying the original cutoff and content epoch. Exact detail remains independently readable when the list cohort overflows. A failed, absent or invalid selected receipt does not select an older success. HTTP responses are bounded to 512KiB.

The window manifest reads canonical scalar metadata and immutable receipt digests. Served summaries recompute the payload digest and check its closed wrapper, bounded source accounting and headers; they do not loop over unreturned job rows. Exact detail performs full source/job validation. The repository clamps reads to five seconds. Coverage counts describe canonical source accounting and retention, rather than current Agent liveness.

Canonical disappearance is `observation_not_retained`. A surviving canonical run without its receipt is `payload_missing`; a stronger retirement description requires exact retirement evidence. The existing canonical retirement ledger stores batch digests, so it cannot prove arbitrary per-run membership. No retired-run count is inferred from it.

## Evidence boundaries

Same-target Viewer, Operator and Target Administrator reads are private/no-store. Retained history does not resolve current enrollment to perform a capture and remains readable after enrollment expiry or revocation. The public projection excludes Windows SID, endpoint and certificate pins, SQL object identities, enrollment IDs and evidence digests. Enrollment revision is visible provenance.

Source schema1 and module metadata schema2 remain distinct. UTC source brackets preserve seven fractional digits and use exact 100ns comparison. Job dates retain their seven-digit, offset-free local strings, including reversed local clocks; they are never assigned UTC or subtracted to produce duration. Last executed step evidence is labeled as such.

Request windows, canonical completion times and repository cursor clocks use microsecond-aligned UTC. These boundaries have a different precision contract from the retained source's seventh fractional digit.

Job states are source-column candidates. Stopped service retains `ServiceNotRunning` and `Unknown` candidates. Capture coverage and loss remain explicit. Public admission and service/session association flags remain false, with native qualification `Absent`.

## Browser ownership

The SQL Agent tab shows activity completion-window observations alongside the existing failure history, which retains its seven-day first-observed semantics. Dedicated `agentActivityRun` and `agentActivityRevision` URL fields preserve exact selection across reload and navigation. An invalid explicit pin suppresses fallback until cleared. Selecting or retaining a received observation window freezes the workspace range to custom.

List and exact-detail requests have independent cancellation and retry state. Target, revision, access and window changes fence stale responses; 401/403 removes both observation sources and requires explicit access retry. Polling cannot replace a pinned investigation. Source-local dates remain literal source values regardless of the workspace time display setting.

Receipt and tab navigation callbacks also check the rendered URL and current target before writing a route. A synchronously changed route cannot be overwritten by a click on its retired DOM.

## Qualification and remaining acceptance

Final groups pass **512 unit**, **126 HTTP**, **100 PostgreSQL integration**, **1 isolated retention**, **55 native web**, **36 mounted component** and **11 built-App** cases, with zero skips. These are separate groups, not a unique-test aggregate. Component TypeScript, production build and asset verification pass. The [qualification record](../../artifacts/dashboard-agent-activity-history-2026-10-07/completion-v2.json) preserves source archives, every failed attempt, exact consumed input versions, independent reviews and synthetic screenshots.

The installed restricted server-role PostgreSQL reader returned a maximum summary page of **100 canonical runs ×512 retained jobs in 376.6ms**, within its five-second deadline. This is one disposable PostgreSQL18.4 fixture measurement, excluding fixture setup; it is not a fleet performance guarantee. Summary reads do not return the jobs. Exact detail validates and returns the selected bounded job set.

The isolated-clock retention case verifies real backup restoration and recovery attestation, leased receipt detach/drop, changed history epochs, explicit missing payloads, canonical survival and restored exact detail. No per-run retirement evidence is fabricated from the existing batch ledger.

HTTP tests exercise root composition with a mock read port; installed PostgreSQL tests use the real restricted repository port and synthetic source captures. Browser tests use finite synthetic fixtures. These groups do not constitute a connected native collector-to-dashboard run. Earlier actual-monitor Run15 reader evidence remains separate. Running/session/time qualification, persistent provisioning, continuously deployed acceptance and the broader dashboard goal remain open. No live migration, schedule opt-in, persistent SQL installation, deployment, push or upload occurred.

The [2026-10-07 connected acceptance record](../../artifacts/dashboard-agent-deny-connected-2026-10-07/local-qualification-v1.json) subsequently passes one opt-in journey with synthetic source/capability evidence, the production collector, real sealed PostgreSQL18.4 repository under restricted roles, root Kestrel HTTP and the hash-verified built Operations UI. Authentication is replaced for the fixture; API reads are not intercepted. Scoped enablement, real lease/start/commit/replay, revocation disabling collection, retained exact success/failure/missing reads, unchanged source clocks and private-field exclusion pass. At320/1440px, exact URL/reload, newest-failure and missing-run no-fallback behavior, local keyboard scrolling, viewport fit and scoped axe checks pass. Screenshots and trace retain their connected fixture provenance.

The [separate Run16 qualification](../../artifacts/dashboard-agent-deny-connected-2026-10-07/native-qualification-v1.json) proves actual-monitor public collector capture and effective explicit-DENY refusal with normal baseline cleanup. It does not join that native source to the connected synthetic journey. Source admission and association remain false and native qualification remains `Absent` in retained/public evidence. Native-source canonical replay, running/session/time semantics and continuous deployed acceptance remain open.

The subsequent [Run17 replay](../../artifacts/dashboard-agent-native-replay-2026-10-07/completion-v1.json) qualifies one unchanged restored-phase native capture through fresh canonical admission, real restricted server-role history reads, root HTTP and built Operations at320/1440px. Original enrollment, source clocks, run/target/revision, accounting, loss and all12 local job dates survive; collection is revoked before browsing history. Exact URL/reload, default selection, missing-run refusal, keyboard scrolling, viewport fit, exact list/detail query bindings and zero scoped axe violations pass without API interception. The native browser artifacts retain their recorded-source provenance; the separate rehearsal is explicitly synthetic. Test authentication and synthetic scheduling prerequisites remain explicit. This is finite replay; running/session/time, native repository resolution, persistent provisioning and continuously deployed acceptance remain open, with admission/association false and qualification `Absent`.
