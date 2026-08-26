# Runbooks

No production runbook exists in the Milestone 0/Milestone 1 scaffold because there is no runtime system to operate. This directory is an index for procedures that must be written, exercised, and versioned alongside the behavior they describe. Empty or speculative instructions must not be presented as safe operational guidance.

Future runbooks are expected to cover:

- M10 analytics rollup/backfill status, host binding, replication visibility,
  incident evidence, and retention preview/attestation/execution. These remain
  bounded repository operations; no live destructive certification is claimed.

- supported installation, gMSA and SPN setup, TLS, Windows Integrated Authentication, RBAC bootstrap, and uninstall;
- PostgreSQL 18.x provisioning, least privilege, backup/restore, migration, partition care, retention, capacity, and disaster recovery;
- target onboarding, capability discovery, generated permission review, credential rotation, and offboarding;
- passive collection validation and safe handling of unsupported/degraded collectors;
- separate DBA review, application, validation, upgrade, and removal of optional enhanced-monitoring scripts;
- service start/stop/upgrade, health checks, lease recovery, backlog recovery, and clock-skew diagnosis;
- collection lag, target/repository timeouts, circuit breakers, visible sample loss, ingestion failure, and partition failure;
- audit-write failure, suspected credential exposure, authorization anomaly, data export, and security incident response;
- alert delivery, report generation, MCP access revocation, and bounded diagnostic troubleshooting;
- supported upgrade/rollback decisions and evidence-preserving product removal.

Each future runbook must state supported versions, prerequisites, required role, blast radius, exact verification, rollback or recovery, audit evidence, UTC timestamps, and escalation conditions. It must preserve the rules in [SECURITY.md](../../SECURITY.md), including no permanent target `sysadmin`, no automatic Query Store/Extended Events/blocked-process changes, and no MCP administrative action.
