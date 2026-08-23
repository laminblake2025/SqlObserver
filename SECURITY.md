# SqlObserver security policy

SqlObserver observes high-value database systems and stores operational evidence that can itself be sensitive. Security boundaries are product requirements, not deployment suggestions.

## Reporting a vulnerability

Do not disclose a suspected vulnerability, credential, query text, plan, customer identifier, or exploit in a public issue. Use the private security-reporting channel published by the repository host or distribution owner. Include the affected version or commit, the smallest safe reproduction, impact, and any suggested mitigation. Remove secrets and production data from all attachments.

If no private channel is visible, contact the repository owner through the private channel by which you received the software and ask for secure reporting instructions before sending details. Maintainers should acknowledge a report, establish an embargoed coordination channel, assess supported versions, and publish remediation and credit information when safe. This scaffold does not promise a response SLA.

## Supported versions and policy

No released version exists at Milestone 0/Milestone 1 scaffolding, so there is currently no production-supported branch. Once releases begin, the project will publish a version-support table here, provide fixes only for versions listed as supported, and identify security-relevant upgrade requirements in release notes.

The initial platform target is Windows Server 2022 and 2025, PostgreSQL 18.x, SQL Server 2019/2022/2025 on Windows, Edge and Chrome, and Windows Integrated Authentication for the web application. Planned platforms are not supported until explicitly promoted in the [support matrix](docs/architecture/support-matrix.md).

## Non-negotiable rules

Every implementation and deployment must satisfy all of the following:

1. Never require or recommend permanent `sysadmin` on a monitored SQL Server.
2. Prefer Windows integrated authentication and group managed service accounts (gMSA).
3. On SQL Server 2022 and later, prefer the documented performance-reader roles and performance-state permissions that satisfy a collector's declared needs. On older supported releases, use explicitly generated, version-aware least-privilege grants.
4. Passive monitoring must not modify a monitored SQL Server instance.
5. Never enable, disable, clear, resize, or otherwise alter Query Store automatically.
6. Never create, start, stop, or alter Extended Events sessions or change blocked-process settings automatically.
7. Enhanced monitoring setup must be a separate, versioned, reviewable script that a DBA chooses to run.
8. MCP must not expose arbitrary SQL execution. An `execute_sql` tool is prohibited.
9. MCP must not kill sessions, change configuration, acknowledge alerts, send notifications, create indexes, force plans, or access secrets.
10. MCP must read through approved application services, never directly from PostgreSQL tables or monitored SQL Server connections.
11. Treat query text, SQL comments, plans, object names, job steps, errors, and XML as untrusted content at every boundary.
12. Enforce explicit time, row, byte, and execution limits on every API and MCP operation.
13. Write an audit record for every MCP call and every administrative write, including denied attempts where safe.
14. Never log passwords, connection strings, access tokens, encryption keys, or unrestricted SQL text.
15. Use parameterized SQL only. Dynamic identifiers require strict allowlisting and quoting in the narrow cases where parameters cannot represent them.
16. Do not use undocumented SQL Server internals when a supported API exists.

These rules may only be strengthened. A proposal to weaken one requires explicit security review and cannot be merged as an incidental implementation change.

## Monitored SQL Server posture

Agentless means the collector connects remotely; it does not mean highly privileged. Each collector declares required capabilities, permissions, supported versions/platforms, bounds, fallback behavior, and output schema version before it can run. Capability discovery selects only collectors the target can support.

Use a distinct service identity per environment where practical. Prefer Kerberos-backed Windows integrated authentication with a gMSA for unattended Windows services. Never store a reusable domain password merely to avoid configuring the service identity. SQL authentication, if a future environment requires it, must be an explicit exception with externally protected credentials, rotation, and equivalent least privilege.

Generated grants must be version-aware and reviewable. Permissions are additive only to the documented collector need, and permission tests must prove expected success and expected denial. No onboarding flow may grant itself privilege. Permanent `sysadmin` is prohibited even for troubleshooting.

Passive queries must use supported catalog views, DMVs, functions, and documented system sessions within bounded timeouts. Passive collection cannot create objects, toggle features, adjust retention, change trace/session state, or persist anything on the target.

Enhanced monitoring is a different operating mode. A DBA may inspect and manually execute a separately distributed script for features such as a dedicated Extended Events session or blocked-process threshold. SqlObserver must discover whether the reviewed setup exists and degrade safely when it does not. It must never apply, repair, or upgrade that setup itself. Query Store remains DBA-controlled in every mode.

## Authentication, authorization, and secrets

The web application targets Windows Integrated Authentication. Authorization is application RBAC and must be checked at the service boundary; authentication alone conveys no diagnostic or administrative permission. Service-to-service calls require an authenticated, audience-bound identity. Deny by default and avoid authorization decisions in the browser.

Secrets must be encrypted at rest using a Windows-appropriate protected store, accessible only to the identity that needs them, and never returned by API or MCP. Logs, exceptions, traces, metrics labels, health endpoints, configuration dumps, and audit metadata must use allowlisted fields and redact before serialization. Never serialize connection-string-shaped data into telemetry. If connection metadata is operationally necessary, emit separately allowlisted, non-secret structured fields rather than a connection string or template.

## Untrusted diagnostic content

Captured query text, comments, query plans, object names, job steps, server errors, deadlock XML, and other XML can contain credentials, personal data, hostile markup, misleading instructions, or very large payloads. Store the minimum justified content, deduplicate it, apply retention, and gate access by RBAC. Never treat captured text as a command, template, log format, prompt instruction, HTML, or trusted XML.

Render as inert text by default. Use safe XML parsers with external entities and network resolution disabled. Sanitize any derived visualization, encode content for its output context, bound decompression and parsing, and prevent spreadsheet-formula injection in exports. Do not place unrestricted SQL text in logs, traces, alert messages, URLs, metric labels, or MCP audit summaries.

## API, MCP, and administrative auditing

The MCP adapter exposes only the tools approved in [ADR-0006](docs/adr/ADR-0006-read-only-mcp.md). It authenticates to `SqlObserver.Server`, which performs RBAC and reads through application services. The bridge and protocol adapter have no PostgreSQL credential and no monitored-target credential.

Every API and MCP operation must declare and enforce an execution deadline, result-row limit, response-byte limit, bounded input ranges, and cancellation. Database statements also receive a statement timeout. Pagination must be stable and bounded; aggregation must not bypass limits. Rate limiting and concurrency budgets protect both repository and target resources.

Every MCP call is audited with actor, client identity, allowlisted tool name, UTC time, authorization result, bounded parameters or their safe digest, outcome, duration, and response size. Never put returned sensitive content or secrets in the audit record. Every administrative write is similarly audited with actor, action, object identity, before/after safe metadata or a digest, outcome, and correlation ID. Audit storage needs integrity protection, restricted access, retention, and monitoring for write failure. A required audit failure causes a sensitive operation to fail closed.

## Security limits and residual risk

SqlObserver is a diagnostic aid, not an isolation boundary, database firewall, backup product, or substitute for platform auditing. Least-privilege monitoring can still expose commercially sensitive workload details. A compromise of a collector identity may permit broad metadata reads within its grants, and a compromise of the repository can expose retained observations. Operators must isolate networks, patch dependencies and hosts, restrict repository access, use TLS, rotate identities, back up and test recovery, and choose retention appropriate to their data classification.

The current repository is scaffold-only. Passing its initial validation does not constitute a penetration test, deployment approval, or security certification. See the [threat model](docs/architecture/threat-model.md) for tracked risks.
