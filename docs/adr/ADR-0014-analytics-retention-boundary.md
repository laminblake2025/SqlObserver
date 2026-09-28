# ADR-0014: Analytics and retention repository boundary

## Status

- Status: Accepted

Accepted for M10 local implementation; production and live-target certification
remain out of scope.

## Decision

Rollups, baselines, forecasts, host/replication evidence, incident packets, and
retention state are persisted in PostgreSQL through fixed `SECURITY DEFINER`
functions. Every operation carries target scope, target revision, generation,
UTC cutoff, and replay identity where applicable. The Server process consumes
application ports only and never connects directly to a monitored SQL Server.

Retention is disabled and duration-less by default. A global
`SecurityAdministrator` must use optimistic revision updates and a recovery
attestation before preview-gated detach/grace/drop operations can proceed.

The default-policy statement describes the original M10 rollout. Migrations
0156-0158 subsequently enabled bounded defaults for written evidence classes;
the canonical-table and dependency decisions are in
[ADR-0021](ADR-0021-canonical-retention-graph.md).

## Consequences

The repository can enforce RLS, byte/row/time bounds, append-only evidence, and
safe conflict mapping consistently. Docker/PostgreSQL, SSPI/WMI, sustained
load, upgrades, installers, and live target certification require later gates.
