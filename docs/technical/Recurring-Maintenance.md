# Recurring maintenance contract

This dashboard feature adds durable daily, weekly and monthly maintenance series to the existing target maintenance calendar. A series creates real repository maintenance windows, which suppress notification delivery while collection and alert evaluation continue. Assignment and alert state remain independent. Existing one-off windows remain supported.

## Civil schedule and bounds

A definition contains frequency, interval, start/end civil dates, local start time, UTC elapsed duration, a named IANA time zone, explicit daylight-saving policies and a safe reason. Daily intervals are 1–365 days; weekly intervals are 1–52 weeks with selected weekdays (Sunday=0); monthly intervals are 1–12 months with day 1–31. Weekly cadence is anchored to the week containing the start date. Missing monthly days are skipped. Dates outside the inclusive start/end bounds are excluded.

The end date is required, at most five years after the start date. Complete canonical expansion is bounded to 2,048 occurrences. This is a finite schedule: there is no implicit extension or background rolling horizon. Each occurrence has a positive elapsed UTC duration of at most seven days. Local end time can therefore move across a daylight-saving change; the form and preview disclose this distinction.

Nonexistent local start times are skipped (`gapPolicy=skip`). Repeated local times require `foldPolicy=earlier` or `later`, referring to the selected UTC instant. UTC bounds, local date, selected offset, ambiguity and expansion provenance are retained. Browser time-zone conversion does not authorize the schedule. The server supplies a preview, and the repository independently validates the canonical expansion. A time-zone data disagreement fails explicitly instead of silently applying different instants.

## Persistence and changes

A series has a durable identity and revision. Occurrence identities are stable for the series and civil date. Creation materializes future occurrences only, using the repository clock. Past anchor dates remain valid definitions, but a fresh series must contain an applicable future occurrence. Preview is an as-of read, not a guarantee that a command will apply an identical count later.

Editing future occurrences uses an explicit civil cutoff and expected series revision. Eligible matching dates update the existing window identity; obsolete future dates are cancelled. Started and past windows remain unchanged. An individual edit or cancellation becomes a durable exception. Cancelled dates are tombstones and cannot be reintroduced by later series edits. Future-series cancellation sets an irreversible civil-date fence: later cancellation can move it earlier, and later edits cannot create dates at or after it, including dates never previously generated. Still-future dates before that fence may be edited. Prior occurrence records remain, and a started window is not cancelled. Cancelled entries remain inspectable.

Existing individual-window editing/cancellation is the cancel-one/edit-one workflow. Generated occurrences retain series and local-date provenance through those changes. Occurrence provenance is exposed through separate series endpoints, preserving the exact legacy one-off listing contract.

## Authorization, concurrency and replay

Only a same-target Target Administrator can preview, read or change series. A wider grant for another target cannot authorize this target. Mutations use the existing native per-target dispatch fence and revision locks, so delivery cannot cross an applied maintenance boundary between readiness and send. Windows are half-open (`start <= now < end`); overlaps suppress delivery while any applicable window remains active.

The server derives the command digest. Immutable receipts bind operation, actor, target, series, action, expected revision, definition/cutoff or cancellation reason. Exact replay precedes current revision, time and expansion checks. Computed occurrence arrays and current time-zone data are excluded from the command identity; replay returns the original result rather than re-expanding or extending a schedule. Changed payloads or actors cannot reuse an operation. Client-provided approval or audit metadata is rejected.

## HTTP and dashboard

Routes are under `/api/v1/observation-targets/{target}/alerts/maintenance/series`. The contract provides bounded series and occurrence lists, current-series reads, server previews, create, edit-future and cancel-future. Current reads have server times and mutable-state disclosure; they are independent of the diagnostic Rewind window.

The UI uses shared dark tokens, keyboard navigation and bounded responsive tables. Confirmed mutation receipts trigger independent current-state reads; a failed readback retries GET only. An uncertain mutation retains its complete command and operation identity. Context changes, role loss, denial and unmount cancel owned requests and fence late results. Read failure or polling cannot silently replace an uncertain intention. Series and individual-window mutations exclude each other.

## Qualification

The dashboard handoff records final integrated validation, immutable input hashes, screenshots and requirement-by-requirement results after execution. Owning tests cover recurrence/DST, native lifecycle and replay, exceptions, authorization and dispatch boundaries; browser qualification uses synthetic fixtures. This contract does not claim completion of the broader repository plan.

Final checkpoint: 216 affected .NET tests (including 91 real PostgreSQL cases), 46 native web checks, 41 mounted, 8 built-App and 36 affected regression cases pass. Eight final captures cover 320/390/768/1440. Exact hashes and cases are in `artifacts/dashboard-maintenance-series/completion.json`. Browser fixtures remain synthetic.
