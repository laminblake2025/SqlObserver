# Chart baseline bands and configured thresholds

The shared Overview chart supports hourly percentile bands, a median line and horizontal rule thresholds. Server dashboard and Resources charts read these references for the selected target and window. The vertical axis includes visible references; a reference alone cannot create a chart without selected-window observations.

## Evidence and limits

The available baseline catalog covers host and replication metrics, not SQL engine metrics. This delivery connects the **collector-host available-memory** baseline to the matching memory series. It does not present that host as necessarily the SQL Server host or invent SQL CPU, waits or query baselines.

`BaselineV1` derives percentiles from the preceding 28 complete UTC days and keys hours as Sunday=0 through Saturday=167. The client requests that exact source window ending at the UTC day boundary before the selected start. It validates target, metric, response bounds and every row's source bounds. Only an explicit empty dimension set with complete visibility, valid percentiles and nonzero evidence contributes a band. Missing hours remain gaps. Multiple source generations are not combined; duplicate hourly references and a full 200-row listing fail explicitly because the existing endpoint has no continuation.

Bytes are converted to GiB to match the chart. Shading is p10–p90, with a median line; it is a descriptive comparison, not a confidence interval, healthy range or alert decision. Accessible reference details include samples, coverage, confidence, UTC hour, exact source window and generation. Retained baselines are read now. The UI does not claim that a reference was available to the operator at the selected historical time.

Thresholds come from enabled metric rules, read only when `/me` grants Target Administrator on that server. The existing API remains the authority for access. Target, metric and displayed unit must match a series. Confirmation, hysteresis and clear settings continue to govern actual alerts. Lines are labeled as **current** rule settings, including in Rewind; they do not reconstruct historical configuration. Rule identities, cursors and the listing timestamp are checked across at most four pages (up to 400 rules), with a visible partial-list note when continuation remains. Disabled and non-metric rules produce no line.

Target, revision, selected bounds, refresh and access changes invalidate the reference view and abort pending requests. Reference errors do not erase the underlying observations. A missing baseline and inaccessible rules have explanatory text rather than a healthy verdict.

The baseline web parser also now accepts the actual serialized `BaselineResult` fields: mean, standard deviation, bounds, dimensions and dimension hash. It retains dimension identity and rejects malformed spread or unknown fields. Against the previous committed parser, the actual-shaped fixture failed with `Unexpected baseline field`; the current regression passes.

## Validation — 2026-09-30

The production build, TypeScript and asset verification passed. **41 distinct focused frontend checks** passed across the chart-reference, chart-range/time, analytics contracts/live shapes, alert-rule reader, resource-file, access and display suites. The nine new checks exercise actual parser/client/model/renderer code, source and scope mismatch, UTC hours, gaps, unit conversion, pagination bounds, duplicate rules/cursors, changed listing timestamps, inert text and accessible reference evidence.

The built interface was exercised through the loopback-only Overview fixture. Synthetic Admin mode verified three memory-hour bands and two metric thresholds across the dashboard and Resources, including a threshold from the second rule page. Reference details exposed all three hours. Delayed window changes removed prior bands/thresholds and the latest six-hour result won. Viewer mode issued no rule read; a missing hourly reference stayed unshaded. Empty and future-source responses produced no band and explanatory text. The browser reported no console errors in the inspected final view.

Ignored local evidence: `web/.artifacts/chart-reference-smoke.png`, `chart-reference-details-smoke.png`, and `chart-reference-smoke-requests.json`. The old-parser reproduction is also under that directory. Browser responses were synthetic; these checks do not qualify native collection, database derivation or external release certification. No backend contract or migration changed. Extending baseline derivation to SQL metrics and reconstructing historical rule settings remain work for the full revamp plan.
