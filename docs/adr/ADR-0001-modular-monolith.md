# ADR-0001: Modular monolith

- Status: Accepted
- Date: 2026-08-23

## Context

SqlObserver needs independently operable web/API, background collection, and local MCP transport processes, but its domain rules, authorization, query projections, and data model will evolve together through the early milestones. Splitting those rules among independently versioned services would add network contracts, deployment sequencing, duplicated policy, and failure modes before workload evidence justifies them.

One executable cannot satisfy all operational boundaries either. Collection has target credentials and scheduling pressure; the interactive server has user-facing latency and authentication; the local MCP bridge has a protocol-specific lifecycle.

## Decision

Build a modular monolith with one product version and explicit in-process module boundaries, packaged as three separately deployable .NET hosts:

- `SqlObserver.Server` for ASP.NET Core API, web assets, SignalR, authentication/RBAC, reports, and MCP HTTP;
- `SqlObserver.Collector` for scheduling, target collection, ingestion, alerting, analytics jobs, partitions, and retention;
- `SqlObserver.McpStdio` as a local protocol bridge authenticated to the server.

Domain and application layers define inward-facing contracts. PostgreSQL, SQL Server, Windows, transports, and SDKs are adapters. Modules do not read another module's tables as an integration shortcut; application services own supported projections. Hosts remain composition roots rather than alternative implementations of business rules.

## Consequences

- Features can be developed and transacted coherently without distributed-service overhead.
- Collection failures and credentials are isolated from the interactive host, and MCP cannot inherit database access from the bridge.
- Releases coordinate compatible processes and migrations as one product.
- Module boundaries require architecture tests and review because they are not enforced by a network.
- Scaling is process-level first; a future service extraction requires workload evidence, a stable contract, and a superseding ADR.
- PostgreSQL is a shared operational dependency, governed by [ADR-0002](ADR-0002-postgresql-repository.md), while worker coordination follows [ADR-0012](ADR-0012-postgresql-worker-leases.md).
