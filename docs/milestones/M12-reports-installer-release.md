# M12 — Reports, installer, deployment, and release hardening

## Status

M12 is in planning and hardening. Package 1, the certification-evidence
foundation, is locally complete and accepted at commit `f33f3a69`. ADR-0015 is
Accepted for the local reports implementation; the remaining M12 ADRs are
Proposed. No document here creates a release or support claim.

## Package 1 — certification evidence

The completed package adds the versioned M12 matrix and schemas, fail-closed
manifest/result verification, explicit Local versus Release validation
profiles, environment and product/sidecar hash binding, and active release
test contracts. Local validation remains non-release evidence. Release
validation still requires every lane and external environment represented by
the matrix.

The MCP certification producer is a pending-lane harness, not an
implementation promotion: `tools/run-m12-mcp-certification.ps1` accepts only
the canonical repository and approved Release Windows Server environment,
executes the live HTTPS protocol and stdio child-process contract, and writes
sanitized, hash-bound run-local evidence only after all checks pass. Local
validation explicitly excludes the `RequiresM12McpRelease` trait; missing live
release prerequisites are failures in the producer and remain an external
gate.

The passive SQL Server certification producer is also implemented as a
pending-only release harness. It maps SQL Server 2019/2022/2025 cases to
majors 15/16/17 and Server 2022/2025 environments, runs the explicit live
non-mutation test with a strict integrated Windows-authenticated TLS contract,
and publishes only closed, sanitized evidence after equal canonical snapshots.
Local and ordinary Release sweeps exclude `RequiresM12SqlServerRelease`; the
producer is the only selector. This adds no support or release claim.

## Remaining packages

- Reports and exports: locally implemented as the bounded, target-scoped
  catalog and safe HTML/CSV contract in [ADR-0015](../adr/ADR-0015-reports-and-exports.md),
  including migration `0021`. External report-volume, browser/accessibility,
  and release certification evidence remains pending.
- Windows installer and PostgreSQL lifecycle: qualify WiX packaging, service
  identities, migration sequencing, upgrade/recovery, uninstall preservation,
  and separately authorized purge under
  [ADR-0016](../adr/ADR-0016-windows-installer-and-postgresql-lifecycle.md).
  The local foundation now provides only bounded, read-only lifecycle and
  migration assessments with closed schemas and exact catalog validation. It
  does not create WiX/MSI/Burn, mutate services or PostgreSQL, write config or
  ACLs, uninstall/purge, or infer a PostgreSQL patch floor; ADR-0016 remains
  Proposed and all lifecycle evidence remains pending.
- Deployment identities, secrets, and transport: qualify gMSA/SSPI, SPN and
  trusted TLS, secret protection/rotation, and sensitive-content policy under
  [ADR-0017](../adr/ADR-0017-deployment-identities-secrets-and-transport.md).
  The local slice now contains only a sanitized, twelve-check assessment and
  PostgreSQL configuration fact inspector; identity, TLS, secret-store,
  installer, and release evidence remain pending.
- Web assets and invalidation: the local identity foundation now emits a Vite 8
  `.vite/manifest.json` and explicit hashed entry/chunk/asset names, then
  generates and verifies a closed, SHA-256-bound catalog under ignored
  `web/.artifacts`. The catalog checks exact bytes, safe POSIX paths, symlink
  exclusion, manifest graph closure, and `index.html` references. This is
  implementation evidence only; qualify immutable serving, cache policy,
  browser/accessibility behavior, and target-scoped invalidation-only SignalR
  under [ADR-0018](../adr/ADR-0018-web-assets-and-signalr.md) separately.
- Release identity and evidence: complete legal/version/publisher/signing
  ownership, support-matrix qualification, and Release-profile evidence under
  [ADR-0019](../adr/ADR-0019-release-identity-and-evidence.md). The local
  decision-neutral assessment records the exact commit, certification policy
  identity, 20-lane/35-case matrix inventory, and 8 implemented versus 27
  pending cases; it always remains `not_ready` and is not release evidence.

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
