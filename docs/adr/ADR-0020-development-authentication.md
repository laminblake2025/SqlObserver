# ADR-0020: Isolated local development authentication

- Status: Accepted
- Date: 2026-09-24
- Supersedes: [ADR-0007](ADR-0007-authentication.md) only for the explicitly enabled local Development host described here.

## Context

The repository review and work plan requests a runnable synthetic development
environment without requiring Windows domain identities. The production
authentication and application authorization boundary must remain intact.
An environment name alone is insufficient: an accidentally exposed development
listener or a real repository connection must not acquire a synthetic identity.
This design follows the first-party development requirements and existing RBAC
contracts; it is not a new supported deployment authentication mechanism.

## Decision

Development authentication is off by default. An administrator must explicitly
set `SqlObserver:DevelopmentAuthentication:Enabled=true`. Startup refuses that
setting in every environment other than `Development`, including `Staging`,
`Production`, and `ContractTesting`.

Enabled mode also requires all of these startup constraints:

- The configured server URL is exactly `http://127.0.0.1:5080`.
- No `Kestrel:Endpoints` override is configured.
- The repository host is a loopback IP literal, the database is exactly
  `sqlobserver_dev`, and the login is exactly `sqlobserver_dev_app`.

The development bootstrap provisions that dedicated synthetic database and login.
The connection is never printed by guard failures. These checks constrain
accidental use; a trusted local administrator can still rename databases or
change infrastructure and is outside the application's security boundary.

The authentication handler issues only these fixed synthetic claims:

- Primary SID: `S-1-5-21-104001-104002-104003-1001`.
- Group SID: `S-1-5-21-104001-104002-104003-2001`.

No request header, cookie, query parameter, or body selects the actor or roles.
The existing `WindowsGroupRoleResolver` remains responsible for authorization.
Development configuration explicitly binds the group to roles on the seeded
synthetic target IDs; the handler itself grants no application role.

Every authenticated request must have a loopback socket peer and an HTTP Host
of `127.0.0.1:5080` or `127.0.0.1:5173`. The second Host permits the local Vite
proxy, which preserves Host and disables forwarded headers. Requests carrying
`Forwarded` or any `X-Forwarded-*` header are rejected. Development mode does
not support remote access, tunnels, reverse proxies, or forwarded identities.

When supplied, Origin must exactly match the permitted request origin. Browser
fetch metadata must be `same-origin`, or `none` for a safe navigation. Unsafe
methods require `application/json` and a matching Origin or `same-origin` fetch
metadata. This rejects cross-site form submissions and DNS-rebinding-style Host
changes before the synthetic identity is created. Non-browser local clients can
send an explicit matching Origin for mutations.

When disabled, the host retains its existing Negotiate authentication behavior.
The existing ContractTesting authentication seam is unchanged and does not
permit enabling this handler. Collector authentication, SQL Server credentials,
MCP bridge HTTPS requirements, service-layer RBAC, audit, and repository roles
are unchanged.

## Consequences and verification

- Local development is deliberately limited to synthetic data on fixed loopback
  ports. Reconfiguring those boundaries requires changing and reviewing this ADR.
- Other local processes can use the synthetic identity. The development machine
  and its seeded data are trusted disposable development resources, not a
  multi-user or production authentication boundary.
- API contract tests exercise the real handler, middleware, and group resolver,
  with TestServer socket features representing local and remote connections.
- Tests cover explicit enablement, non-Development refusal, startup connection
  and listener guards, unchanged disabled-mode registration, fixed identity,
  Host and peer checks, proxy headers, origins, and unsafe browser requests.
- Production deployments continue to follow ADR-0007 and the security policy.
