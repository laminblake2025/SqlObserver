# Overview timeout repair

Implemented and deployed to WIN-QNGOV5GDM24 on September 7, 2026.

## Findings and changes

Overview previously composed evidence sources sequentially for each target. Several live repository reads reached their five-second limits; together they exhausted the HTTP endpoint's 30-second deadline. Fast workload and host series were consequently lost with the whole response.

Composition now uses one eight-read semaphore shared across the authorized target scope. Each source has a five-second cancellation budget including its pagination, and the evidence phase has a 20-second deadline. Available evidence is returned with explicit gaps when a source cannot finish. Caller cancellation and authorization failures still propagate. Parallel results use thread-safe collections and deterministic resource/series ordering. The endpoint deadline is unchanged.

A separate wait-page contract bug was reproduced after 15 full pages: `ServerWaitSummaryPage` compared a null final-page cursor's baseline against the actual non-null baseline and rejected the last page. The baseline identity check now runs only when a next cursor exists. Real mismatched continuation baselines remain rejected.

Some initial database reads were much slower than subsequent reads. Session-only JIT/planner experiments did not establish a reproducible planner-specific cause, so no persistent optimizer setting, permission change or database migration was made. The composition budget makes the response resilient to recurrence of slow sources.

## Deployment and live verification

- Server directory: `C:\SqlObserverLab\server-overview-timeout-v2-20260907`.
- Collector remains `collector-replication-overview-v2-20260905`; frontend remains `web-history-pagination-20260905`.
- Only SqlObserverServer was restarted. Existing configuration and identities were retained. Both application services are running.
- One-hour fleet Overview: HTTP 200 in 6,792 ms, with 17 resources and ten series.
- 24-hour fleet Overview: HTTP 200 in 2,700 ms, with 17 resources and ten series.
- Wait rankings render as observed evidence. The only gap in these final responses was the existing backup source-timezone notice.
- Live browser verified default Overview, single-server selection and previous-period comparison, current coverage, populated charts and wait rankings, without loading or alert errors.

Timings describe the single-server lab and are not a ten-server load certification. Slow sources can still be labelled unavailable if their budget expires.

Rollback may restore the server executable path to `server-replication-overview-v3-20260905` and restart SqlObserverServer; configuration and frontend arguments must be preserved. No schema rollback is needed.

## Tests

Canonical Local validation passed for the composition change, including backend suites, all 109 frontend tests, type checking, production build and asset verification. After the final-page correction, the full unit and API suites were rerun successfully. Regression tests cover independent progress during a stalled source, caller cancellation, shared concurrency across ten targets, final-page acceptance and continuing-page baseline rejection.

Logs and disposable diagnostic/deployment artifacts are under `TestResults/overview-timeout`. The code changes are limited to the Overview query service, the wait-page contract and their regression tests. No GitHub publication was performed in this task.
