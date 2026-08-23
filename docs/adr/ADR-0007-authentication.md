# ADR-0007: Authentication

- Status: Accepted
- Date: 2026-08-23

## Context

The initial deployment is Windows Server in a Windows-managed environment, and the web application requires Windows Integrated Authentication. Interactive users, unattended services, the local MCP bridge, PostgreSQL, and monitored SQL Server have different identity and authorization needs. Treating network location or successful authentication as authorization would expose sensitive diagnostic data.

## Decision

Use Windows Integrated Authentication for the web application and prefer gMSA identities for unattended Windows services. Resolve authenticated principals into application RBAC roles and target scopes at the server; deny by default. Authorization is enforced in application services for every data projection and administrative operation, not only in routes or the browser.

Use distinct identities and least-privilege grants for server-to-PostgreSQL, collector-to-PostgreSQL, and collector-to-target access where practical. Prefer Windows integrated target authentication. Never require permanent `sysadmin`, and never let onboarding grant its own target privileges. A DBA reviews version-aware generated grants.

The MCP stdio bridge uses an audience-bound authenticated channel to `SqlObserver.Server` and carries the requesting identity/context required for normal RBAC. Local process access alone grants nothing. No browser or MCP client receives repository or target credentials.

If a future environment cannot use integrated authentication, its alternative identity flow requires a new threat review and ADR before support. Credentials that cannot be eliminated are held in a Windows-appropriate protected store, scoped to the necessary service identity, rotated, and never logged or returned.

## Consequences

- The design aligns with the initial Windows platform and avoids reusable service passwords where gMSA is available.
- Deployments must correctly configure Active Directory, SPNs, Kerberos/delegation where needed, TLS, service ACLs, and group-to-role mappings.
- Authentication and RBAC require negative, cross-role, target-scope, disabled-account, and service-identity tests.
- Administrators must plan recovery identities without embedding a universal application backdoor.
- Planned Linux and managed-database targets need explicit identity designs rather than silent fallback to stored privileged credentials.
