# Overview analytics implementation

Implemented September 5, 2026. The approved design targets environments of up to ten servers, with All servers or one authorized server selected from a dropdown. Ten is a design target, not an enforced registration limit.

## Delivered behavior

- Overview has its own analytics page; the registration table and discovery inventory remain on Servers.
- Scope and time range are kept in the URL. Presets cover 1 hour, 6 hours, 24 hours and 7 days; custom UTC windows are bounded to 31 days. Optional previous-period comparisons and a visibility-aware 60-second refresh are available.
- The page shows observed attention, active alerts, blocking and deadlocks; SQL workload history; top waits; contention history; host and replication metrics; compact operational evidence; and collection coverage.
- Missing, stale and partial evidence are labelled. Snapshot evidence is distinguished from selected-window history. Charts have tabular alternatives and keep servers and dimensions separate.
- A single authorized API envelope supplies each period, with bounded concurrency, pagination, cancellation, a 30-second deadline and a 1 MiB response ceiling. Individual source failures retain available evidence.
- Workload history derives batch rates only from successful, complete, revision-matched observations with matching engine startup markers. Counter resets, missing markers and oversized integer counters produce unavailable rates instead of misleading spikes. Legacy eight-counter payloads remain accepted.

## Database and rollout

Migration 0035 adds the scoped workload-history function and extends core ingestion for the startup marker. The user-approved integrity patch also updates exactly three core collector registry rows and their certification bindings. Previously applied migration files and historical run digests are unchanged.

Deploy the migration together with the matching server and collector bundle. An old collector binary will not match the upgraded registry. The migration was exercised only in disposable PostgreSQL databases during this task; no lab or production deployment was performed.

## Practical limits

Wait rankings describe the latest comparable collector interval, rather than a reconstructed total for the selected window. Operational records without source timestamps say so. Host series are separate and are not summed into fleet totals; shared-host identity deduplication is not implemented. Backup SLA evaluation, capacity forecasts and historical query-regression scoring require additional evidence and policies. No cached two-second latency guarantee or live ten-server load certification is claimed.

## Verification

- Frontend production build and dedicated Overview model/API tests passed.
- All 160 API contract tests passed.
- All 27 core PostgreSQL persistence tests passed, including restart-marker and legacy payload coverage.
- All seven migration integration tests passed before the integrity patch; the subsequent core persistence suite reapplied the complete migration chain with the approved patch.
- Browser checks used an explicitly synthetic ten-server fixture, covering all/single scope, rapid scope changes, comparisons, partial coverage, empty and error states. Layouts at 390, 1440 and 1920 pixels had no page-level horizontal overflow.
- Canonical Local validation passed: 1,086 backend tests, all 106 frontend tests, type checking, production build and asset verification. Results are recorded in `TestResults/overview/local-validation-approved.txt`. This excludes external certification lanes and the live SQL Server lab, which was not configured for this run.

The two older frontend source-contract tests now follow the extracted Servers component. Core dependency tests now identify the intended collector among newer scheduled collectors while retaining the inventory-before-files assertion.

## Subsequent lab rollout

This Overview is now deployed on WIN-QNGOV5GDM24 with real host and replication series. See [Replication and Overview validation](SqlObserver-Replication-Overview-Validation-2026-09-05.md) for the combined rollout, forward migrations and live workload evidence. The earlier verification section describes the original implementation run.
