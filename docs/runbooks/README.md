# Runbooks

These four M12 runbooks are versioned operator procedures. They describe
bounded, read-only preparation and evidence handling; they do not make a
product-support or release-readiness claim. The certification producer hashes
the exact Markdown bytes and records only document identities, section IDs,
procedure IDs, and prerequisite case identities.

| Runbook | Purpose |
| --- | --- |
| [M12 release preflight](m12-release-preflight.md) | fixed MatrixOnly/ContractOnly order and clean-tree preflight |
| [M12 supply-chain certification](m12-supply-chain-certification.md) | deterministic SBOM, license, vulnerability, and provenance evidence order |
| [M12 candidate evidence verification](m12-candidate-evidence-verification.md) | exact three-file candidate inspection and cross-file binding |
| [M12 failed-run quarantine and escalation](m12-failed-run-quarantine-and-escalation.md) | ownership-safe pending/verify/quarantine handling |

The procedures are source-backed by the M12 certification matrix, the closed
contracts under `release/certification/`, `tools/verify-test-results.ps1`, and
the supply-chain producer. Parsed commands in Markdown are identifiers only;
the producer executes its own fixed argument arrays. Never delete an unowned
`.pending-*`, `.verify-*`, or `.quarantine` directory. Preserve
[SECURITY.md](../../SECURITY.md), including no permanent target `sysadmin`, no
automatic Query Store/Extended Events/blocked-process changes, and no MCP
administrative action.
