# ADR-0005: Collector contract

- Status: Accepted
- Date: 2026-08-23

## Context

SQL Server versions, editions, platforms, permissions, and enabled features expose different diagnostic capabilities. Unbounded or overlapping queries can worsen an incident, while silent fallback or truncation can create false conclusions. A collector therefore needs an inspectable contract beyond an implementation class.

## Decision

Every collector has a versioned manifest containing:

- stable ID and display name;
- required capabilities and required permissions;
- supported SQL Server versions and platforms;
- default interval and hard minimum interval;
- timeout, maximum rows, response-byte budget, and estimated cost;
- declared fallback, including the explicit unsupported outcome;
- output schema version.

Every implementation is asynchronous, accepts and propagates cancellation, uses parameterized SQL and supported interfaces, is non-overlapping for each target, and emits structured telemetry for duration, rows, bytes, truncation/loss, retries, circuit state, and outcome. Scheduling is protected by PostgreSQL leases and fencing. Retry and circuit-breaker policy respects the original deadline and cost; retries cannot turn a bounded collector into an unbounded one.

Capability discovery selects a compatible contract path before execution. Output validation occurs before ingestion. A fallback is independently tested and reported, never silently treated as the preferred signal. Passive contracts do not modify a monitored target; any enhanced prerequisite follows [ADR-0010](ADR-0010-passive-versus-enhanced-monitoring.md).

Collectors are implemented in this order: capability/connection, core engine counters, databases/files, sessions/requests, waits, blocking, deadlocks from `system_health`, Query Store with plan-cache fallback, backups, SQL Agent, TempDB, Availability Groups, host metrics, and replication.

## Consequences

- Scheduling, permissions, UI/API interpretation, testing, and operations share one explicit description of each collector.
- New collector work includes manifest review, supported-version tests, expected-denial tests, timeout/cancellation tests, output-schema compatibility, and representative cost measurements.
- Unsupported or partial visibility is visible to users and downstream analytics.
- Conservative bounds can omit evidence; truncation and visibility gaps are first-class data rather than hidden sampling.
- Manifest evolution and output-schema compatibility add maintenance cost but prevent accidental behavior drift.
