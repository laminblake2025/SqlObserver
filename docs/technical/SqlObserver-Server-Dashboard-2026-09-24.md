# Server dashboard on target-scoped overview evidence

The Server summary now opens with a target-scoped dashboard drawn from the
existing Overview projection. It carries the selected URL time range, shows
current alert and blocked-session counts separately from deadlocks in the
window, ranks the server's available issues before the charts, and plots SQL
batch requests, blocked sessions, and host CPU. Preset ranges refresh the
dashboard each minute; a custom UTC range stays fixed. Previous-period
comparison uses the existing dashed-series behavior. The original current
health snapshot and database/collection tabs remain below the timeline.

An Activity issue link preserves the selected range and opens the activity
history at the issue's UTC timestamp. The dashboard names collection gaps and
the limits of host CPU and latest-snapshot evidence. The three charts share a
UTC crosshair. Dragging across any chart writes a fixed custom window to the
URL for all three; the time-range controls provide the same keyboard-accessible
choice. Vertical lines mark ranked Overview issues within the window, rather
than claiming to show every event. Baseline bands and a full event catalogue
remain part of the planned chart replacement. Activity history
currently retains and serves only 24 hours, so older dashboard issue links
can correctly land on an explicit snapshot gap. Other current-only screens do
not yet follow the historical window.

The dashboard adds a target-scoped Overview request alongside the existing
health request. Overview currently composes live reads, so its known latency
remains until the planned precomputed latest-status projection replaces it.

The focused URL-scope test and existing Overview and Health tests passed, as
did the production web build. A synthetic target was rendered and visually
checked at desktop width; no live SQL Server or full browser suite was run.
The chart interaction model tests passed, and a synthetic drag changed the URL
to the selected custom window in the rendered app.
