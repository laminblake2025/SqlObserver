# ADR-0012: PostgreSQL worker leases

- Status: Accepted
- Date: 2026-08-23

## Context

Separately deployed collector processes must avoid overlapping collection for the same target/collector and coordinate maintenance such as partitions, retention, rollups, and alert evaluation. Process-local locks cannot coordinate multiple workers or survive failure. Introducing a message broker before throughput and reliability evidence would add a second distributed system and deployment dependency.

## Decision

Use PostgreSQL-backed, time-limited worker leases before introducing a separate broker. A lease is keyed by a stable work identity, owned by a unique execution ID, stores UTC acquisition/renewal/expiry times, and yields a monotonically increasing fencing value.

Acquire and renew leases atomically using PostgreSQL concurrency primitives and repository time, not a worker's local wall clock. Only the current owner/fencing value may commit lease-protected work. Renewal stops before the execution deadline leaves insufficient safety margin. On cancellation, clean completion, or bounded failure, the worker releases when possible; crash recovery relies on expiry. Uncertain ownership fails closed and prevents target work or commit.

Leases coordinate ownership, not payload transport or durable work history. Work definitions and outcomes remain explicit repository records. Operations expose contention, acquisition/renewal latency, expired/stolen leases, stale commit rejection, and scheduling lag. Lease-table access uses a narrow repository role.

A broker may be proposed later only with measured evidence that repository leases cannot meet throughput, fairness, isolation, or availability goals, plus a migration/operations design in a superseding ADR.

## Consequences

- SqlObserver avoids an early broker while gaining cross-process non-overlap and crash recovery.
- PostgreSQL availability and transaction behavior are part of scheduler availability; workers must back off and expose degraded state during repository faults.
- Fencing is required because expiry alone cannot stop a paused former owner from resuming.
- Tests must cover concurrent acquisition, renewal, expiry, cancellation, process pause/crash, stale fencing, clock skew, database restart/failover, and long-running work.
- Lease retention/cleanup and hot-key contention require capacity monitoring and bounded maintenance.
