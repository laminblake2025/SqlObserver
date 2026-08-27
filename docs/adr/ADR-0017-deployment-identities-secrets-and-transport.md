# ADR-0017: Deployment identities, secrets, and transport

## Status

- Status: Proposed
- This record is not Accepted. The defaults below cannot be treated as a
  production identity or secret policy until owners approve and labs qualify it.

## Context

The application has several trust boundaries: interactive web users, Windows
services, the local MCP bridge, PostgreSQL, and monitored SQL Server targets.
Identity, credential storage, and HTTPS transport must not collapse those
boundaries or rely on caller-provided headers. Diagnostic content may include
secrets or business data and must remain protected even when a collector or
renderer fails.

## Invariants from accepted architecture

- Windows Integrated Authentication plus application RBAC is the established
  web direction; authentication alone is not authorization.
- Server, Collector, MCP stdio, PostgreSQL, and target access have distinct
  responsibilities and should use distinct least-privilege identities where
  practical. Permanent `sysadmin` and self-granting onboarding are prohibited.
- gMSA is the established deployment preference for unattended Windows service
  identities. Sensitive content is untrusted, bounded, audited, and excluded
  from unrestricted logs and telemetry.
- HTTPS/TLS validation is required at the transport boundary; caller-supplied
  identity, role, target-scope, bearer, or trusted-identity headers are not an
  authorization source.

## Recommended defaults (not yet decisions)

Use separate gMSA identities for Server and Collector, with narrowly scoped
PostgreSQL and target permissions. Keep the MCP stdio bridge credential-free
with respect to databases and authenticate it to the Server over validated
HTTPS. Prefer Windows integrated authentication for interactive users and
target access; map groups to application roles server-side.

Store no plaintext secrets in installer arguments, source, manifests, URLs,
logs, or crash reports. Where a secret is unavoidable, use an owner-approved
Windows protected-secret mechanism, restrict its ACL to the owning service, and
make rotation/revocation explicit. PostgreSQL connection material should be
provisioned by deployment policy rather than invented by application code.

Require a trusted certificate chain, hostname validation, modern TLS settings,
and an explicit certificate rotation/expiry alarm. Do not add certificate
validation bypasses or fallback to cleartext. Keep sensitive report/query
content unavailable in production by default until the owner approves its
availability and key policy.

## Owner decisions still required

- Choose managed versus customer-owned PostgreSQL and its identity integration.
- Confirm authentication mode(s), gMSA account names, service ACLs, SPNs, and
  delegation boundaries for each supported topology.
- Approve the secret store/protection mechanism, key ownership, rotation,
  backup/recovery, and the policy for making sensitive content production
  available.
- Choose certificate issuer, trust-anchor distribution, TLS minimums, and
  operational ownership of renewal and incident response.
- Set patch floors, capacity/SLO targets, and the supported identity topology.

## Alternatives and consequences

- Customer-managed service accounts: familiar in some environments, but create
  password rotation, disclosure, and operator burden that gMSA avoids.
- Stored SQL or bearer credentials: broader portability, but increased secret
  theft and delegation risk; any exception needs a separate threat review.
- Self-signed or validation-bypassed TLS: easy bootstrap, but permits endpoint
  impersonation and cannot support a trustworthy release posture.
- Make sensitive content available by default: convenient diagnostics, but
  expands data-protection, key-management, export, and incident scope.

## Downstream implementation and migration gates

Document the identity matrix and ACLs for every process-to-repository,
process-to-target, and client-to-Server path. Implement configuration loading
that rejects plaintext/ambiguous secrets, validates certificate names and
expiry, and emits safe rotation/denial telemetry. Tests must cover gMSA/SSPI,
SPN/delegation, expected denial, certificate rollover, revocation/expiry,
cross-target authorization, and secret absence from artifacts and logs.
Installer and release manifests must identify the approved publisher/signing
service without embedding private key material. Live Windows, trusted-TLS, and
identity-lab evidence are mandatory M12 external gates; local tests do not
create a support claim.
