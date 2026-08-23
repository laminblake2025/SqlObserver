# ADR-0003: Windows services

- Status: Accepted
- Date: 2026-08-23

## Context

The initial application platform is Windows Server 2022 and 2025. Collection and serving must start unattended, participate in Windows service recovery and eventing, use integrated identities such as gMSA, and be installed and upgraded predictably. Interactive console lifetimes and scheduled tasks do not provide the required service semantics.

## Decision

Host `SqlObserver.Server` and `SqlObserver.Collector` as independently managed Windows Services built on .NET 10 hosting. The MCP stdio bridge is a local, on-demand process launched by an MCP host and authenticates to the server; it is not granted service or database credentials merely because it runs locally.

Each service receives a distinct, least-privilege identity where practical, prefers gMSA, supports graceful cancellation, exposes bounded health/telemetry, and keeps machine-specific configuration outside binaries. Installation must configure service dependencies, recovery, ACLs, endpoints, and identities explicitly. Services do not elevate or grant target permissions themselves.

## Consequences

- Windows lifecycle, identity, eventing, and recovery behavior match the initial support target.
- Server and collector can be stopped, recovered, upgraded, and scaled independently while remaining one modular product.
- Install/upgrade testing must cover service accounts, ACLs, SPNs, TLS certificates, failure recovery, and cancellation on Windows Server 2022/2025.
- SQL Server on Linux and non-Windows application hosting are not implied; they require later design and support work.
- A service wrapper cannot be used to hide interactive prompts or a dependency on permanent administrative rights.
