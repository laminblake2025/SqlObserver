# M12 — Reports, installer, deployment, and release hardening

## Status

M12 is in planning and hardening. Package 1, the certification-evidence
foundation, is locally complete and accepted at commit `f33f3a69`. The five
M12 ADRs linked below are Proposed; none is Accepted and none creates a
release or support claim.

## Package 1 — certification evidence

The completed package adds the versioned M12 matrix and schemas, fail-closed
manifest/result verification, explicit Local versus Release validation
profiles, environment and product/sidecar hash binding, and active release
test contracts. Local validation remains non-release evidence. Release
validation still requires every lane and external environment represented by
the matrix.

## Remaining packages

- Reports and exports: implement the bounded, target-scoped catalog and safe
  HTML/CSV contract proposed by [ADR-0015](../adr/ADR-0015-reports-and-exports.md),
  including a reviewed `0021` migration if needed.
- Windows installer and PostgreSQL lifecycle: qualify WiX packaging, service
  identities, migration sequencing, upgrade/recovery, uninstall preservation,
  and separately authorized purge under
  [ADR-0016](../adr/ADR-0016-windows-installer-and-postgresql-lifecycle.md).
- Deployment identities, secrets, and transport: qualify gMSA/SSPI, SPN and
  trusted TLS, secret protection/rotation, and sensitive-content policy under
  [ADR-0017](../adr/ADR-0017-deployment-identities-secrets-and-transport.md).
- Web assets and invalidation: qualify immutable hashed SPA assets, browser
  caching, accessibility, and target-scoped invalidation-only SignalR under
  [ADR-0018](../adr/ADR-0018-web-assets-and-signalr.md).
- Release identity and evidence: complete legal/version/publisher/signing
  ownership, support-matrix qualification, and Release-profile evidence under
  [ADR-0019](../adr/ADR-0019-release-identity-and-evidence.md).

## External and owner gates

The following choices remain unresolved and must be recorded before release
behavior is accepted: managed versus customer-owned PostgreSQL; authentication
mode; installer and publisher identities; legal publisher, license, version,
and signing identity/service; normal uninstall preservation (recommended) and
any separate destructive purge; sensitive-content production availability and
key policy; accessibility target; OS/.NET/PostgreSQL/SQL Server/browser patch
floors; capacity/SLO and recovery targets.

External evidence must cover supported 64-bit Windows, Docker PostgreSQL,
live SQL Server versions/permissions, gMSA/Kerberos/SPN, trusted production
TLS, installer lifecycle, browser/accessibility workflows, sustained load,
failure/recovery, signing, and the complete certification manifest. Missing or
local-only evidence remains a gate failure, not a hidden skip.

M12 is not complete until these packages and gates pass together. This document
does not claim release readiness, product support, or a supported platform.
