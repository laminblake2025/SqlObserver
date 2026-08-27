# ADR-0015: Reports and bounded exports

## Status

- Status: Accepted (local implementation)
- Owner approval: the four fixed reports, inert printable HTML, UTF-8
  per-section CSV, immutable 24-hour PostgreSQL materialization, and existing
  WIA target-scoped Viewer/Operator/TargetAdministrator access are approved
  for the local slice. External and release certification remain pending.

## Context

Reports are a presentation of existing, authorized projections, not a second
collection path or a general SQL query surface. They must remain target-scoped,
bounded, reproducible, and safe when diagnostic content is untrusted. A report
run also needs an attributable snapshot so a later read or export cannot silently
change the evidence represented by the original request.

## Invariants from accepted architecture

- The Server owns API and report presentation; it uses application services and
  repository ports. It never connects directly to a monitored SQL Server.
- PostgreSQL is the application repository, migrations are immutable SQL-first
  changes, and persisted timestamps are UTC. Authorization is target-scoped and
  authenticated is not synonymous with authorized.
- Query text, plans, object names, errors, XML, job steps, and similar content
  are sensitive, untrusted data. They are not placed in logs, URLs, audit
  summaries, or unrestricted exports without a later explicit policy.

## Accepted local defaults

The accepted local catalog uses existing projections only and issues no
target queries:

- Instance Health
- Performance Window
- Incident Evidence
- Capacity/Readiness

The initial output should be inert printable HTML and UTF-8, per-section CSV.
PDF, XLSX, and downloadable JSON are deferred until their renderer and content
policies are separately qualified.

Create an immutable `reporting.report_run` for each accepted request. It should
bind a UTC snapshot, target revision, report-definition version, actor identity,
and a normalized-parameter digest. Materialize canonical, sensitive-free
section rows in PostgreSQL; do not persist a rendered binary and do not add a
background queue for the initial slice. A 24-hour materialization retention
period is fixed for the local slice; the bounded collector expiry worker
removes expired runs. Backup/restore and external retention qualification
remain release gates.

The request is for one target and a UTC half-open window. Detailed windows are
limited to 7 days and trend windows to 31 days. Continuations are signed opaque
cursors; an expired or otherwise no-longer-available run returns HTTP 410.
Roles are target-scoped `Viewer`, `Operator`, and `TargetAdministrator`.

Every create, read, export, deny, timeout, oversize, expiry, and failure reaches
the audit boundary. Audit data contains safe identifiers and bounded metadata;
query text, plan XML, deadlock XML, job text, and raw provider errors are
excluded unless a later policy explicitly approves them.

The exact initial engineering bounds are: 64 KiB request; 5 seconds repository
time; 15 seconds total; 200 rows per section; 2,000 HTML rows; 10,000
materialized/export rows; 1 MiB HTML/API response; 8 MiB CSV/materialization;
two concurrent runs per actor and 16 globally; immediate HTTP 429 when full.
CSV is RFC 4180, UTF-8, deterministically UTC-formatted, and neutralizes a
leading first non-whitespace `=`, `+`, `-`, or `@` to prevent spreadsheet
formula execution.

Migration `0021` adds the report-run, materialized-section, cursor/replay, and
least-privilege repository functions needed by this contract.

## Owner decisions still required

- Decide which sensitive-content classes, if any, may be production-visible in
  reports and exports, and the key-management/rotation policy for protected
  content.
- Confirm the accessibility target and capacity/SLO targets.

## Alternatives and consequences

- Generate directly from live targets: fresher data, but violates the Server
  target boundary and makes authorization, timeout, and audit behavior harder
  to reproduce.
- Persist rendered PDF/XLSX/JSON: familiar downloads, but adds renderer,
  parser, formula, binary-retention, and malware/content-safety surfaces.
- Queue report work: useful for very large reports, but adds broker/worker
  lifecycle and delivery state before capacity evidence justifies it.
- Return raw diagnostic content: more troubleshooting detail, but materially
  expands disclosure, export-formula, parser, and retention risk.

## Downstream implementation and migration gates

Application contracts freeze report definitions, canonical parameter
digests, section schemas, cursor signing/expiry, and error mapping before route
work starts. Repository work requires a reviewed immutable `0021` migration,
security-definer functions with fixed search paths, RLS/target checks, least
privilege, replay behavior, and bounded cleanup; the migration must not be
silently introduced by this proposal. API and browser tests must prove limits,
cancellation, authorization, safe rendering, CSV formula neutralization,
equal-timestamp pagination, expiry 410, audit terminals, and sensitive-content
exclusion. Release certification must include representative-volume evidence,
browser/accessibility evidence, and failure/recovery evidence. None of these
gates is a release-support claim; external report evidence and release
certification remain pending.
