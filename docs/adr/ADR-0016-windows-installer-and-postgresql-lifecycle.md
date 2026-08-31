# ADR-0016: Windows installer and PostgreSQL lifecycle

## Status

- Status: Proposed
- This record is not Accepted. It records a release design to be qualified,
  not an installer or support commitment.

## Context

SqlObserver has separately deployable Server, Collector, and MCP stdio hosts,
with PostgreSQL as its application repository. M12 must make installation,
upgrade, recovery, and uninstall observable and reversible enough for operators
to understand what happens to services, schema, credentials, and data. A package
must not turn a development bootstrap into an implied production deployment.

## Invariants from accepted architecture

- The product remains a modular monolith with narrow host responsibilities;
  Server and Collector are Windows services and the stdio bridge is on-demand.
- PostgreSQL schema changes are immutable, numbered SQL-first migrations with
  checksums. The repository is not a monitored SQL Server.
- Least privilege, target-scoped authorization, UTC persistence, explicit audit,
  and fail-closed behavior remain in force during lifecycle operations.
- No local milestone or passing development test creates a platform or release
  support claim.

## Recommended defaults (not yet decisions)

Use a signed WiX-based Windows package with explicit preflight, install,
upgrade, recovery, and uninstall phases. Service registration should be
idempotent, record the installed product/version and migration state, and stop
and restart only the services owned by that product. Upgrade should apply only
forward, checksum-verified migrations and fail closed before changing service
state when prerequisites are not met. Recovery must leave an auditable outcome
and preserve the prior known-good configuration where possible.

Treat PostgreSQL as an external dependency in the first lifecycle design: the
installer may validate connectivity and apply reviewed migrations, but must not
silently provision a database, embed credentials, or claim backup/HA. A
customer-owned repository is the recommended default pending owner choice.

Normal uninstall should stop and unregister product services while preserving
the repository and collected data. Any purge must be a separate, explicit,
destructive operation with a strong confirmation, preview, audit, and recovery
documentation. This preservation policy is recommended, not yet accepted.

Installer logs and manifests should contain safe IDs, versions, hashes, and
outcomes only; secrets, connection strings, and unrestricted diagnostic content
must not be copied into them.

## Owner decisions still required

- Choose managed versus customer-owned PostgreSQL, including backup/restore,
  network, upgrade, and failure ownership.
- Choose the installer/publisher identities, service identities, auth mode,
  certificate provisioning, and whether a separate database administrator must
  approve migration execution.
- Approve normal-uninstall preservation and the exact separate purge authority.
- Set supported OS/.NET/PostgreSQL patch floors, upgrade/rollback promises,
  capacity/SLO targets, and recovery objectives.
- Select the legal publisher, license, versioning, and signing service/identity
  (coordinated with ADR-0019).

## Alternatives and consequences

- Bundle and manage PostgreSQL: smoother single-machine setup, but the product
  assumes backup, patch, HA, data ownership, and credential responsibilities
  that may not fit customer operations.
- Require a separately operated PostgreSQL service: clearer ownership and
  existing DBA controls, but install has more preflight failures and upgrade
  coordination.
- Destructive uninstall by default: simpler cleanup, but risks irreversible
  evidence loss and violates the safer operational expectation.
- MSI-only or ad hoc scripts: lower packaging effort, but weaker chained
  rollback, identity, upgrade, and evidence behavior.

## Downstream implementation and migration gates

Define an install-state model, service dependency/order, migration lock and
failure mapping, configuration protection, and recovery runbook before writing
installer behavior. WiX package tests must cover clean install, repeat install,
upgrade across migration boundaries, interrupted upgrade, service crash,
recovery, uninstall preservation, and separately authorized purge. PostgreSQL
tests must cover supported-version preflight, checksum verification, least
privilege, backup/restore assumptions, and migration failure recovery.
Release evidence must bind the exact signed product used by every lifecycle
case; installer success locally is not release certification.
