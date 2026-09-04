# M12 supply-chain certification

## Status

Pending-only operational procedure for the M12 supply-chain lane. Passing local
contract checks does not promote the lane or establish release evidence.

## Scope

The procedure covers the deterministic evidence dependency order: SBOM, SPDX
license evidence, vulnerability scan, provenance, and finally runbook evidence.
It covers identities and hashes, not raw package, scanner, or command output.

## Supported versions and environment

Live evidence is limited to approved x64 Windows Server 2022/2025 Release
environments. Tool versions and exact commands are fixed in the versioned
contracts; an operator may not replace them with ambient tools.

## Prerequisites

MatrixOnly and all ContractOnly checks must pass. The producer requires a clean
trusted commit and the preceding four final UUID prerequisite candidates:
`m12-sbom`, `m12-licenses`, `m12-vulnerability-scan`, and `m12-provenance`.

## Required role

The release-certification engineer may run the pending producer. An independent
reviewer must accept the resulting evidence before any release decision.

## Blast radius

The producer uses an owned isolated pending directory and held input snapshots.
It must not mutate the checkout, web dependencies, product targets, databases,
services, or unowned TestResults directories.

## Procedure

Run the fixed producer flow in dependency order. Each prerequisite must be a
final UUID candidate from the same commit and environment. Generate and verify
the bounded catalog and exactly three output files only after all prerequisite
checks and the exact live test pass. Markdown command labels are never parsed
or executed.

After the SBOM candidate is final, set
`SQLOBSERVER_M12_LICENSE_SBOM_PATH` to its canonical
`TestResults/m12/<UUID>/m12-sbom.cdx.json` path while running the
`m12-licenses` case. The license run keeps its own distinct UUID while binding
its evidence cryptographically to that exact published SBOM. Clear the variable
after the license case; the runner rejects external or non-final SBOM paths.

## Verification

Check byte and identity binding for each prerequisite sidecar, distinct run IDs
and paths, exact commit/environment agreement, and absence of raw outputs.
Check the runbooks catalog hashes four documents and records 12 sections per
document. Check result, TRX, and provenance agree on the same run and commit.

## Failure recovery

On any failed prerequisite, test, hash, identity, or publication check, leave
the matrix pending and remove only resources proven owned by this run. If
ownership cannot be proven, move the owned root to its quarantine state and
escalate; never recursively remove an unowned directory.

## Evidence and UTC timestamps

The published set is exactly `m12-runbooks.json`,
`m12-runbooks-test-evidence.json`, and `m12-runbooks-provenance.json`. Record
the supplied generated-at UTC value, commit SHA, environment ID, run ID, and
all three byte hashes. Do not include Markdown bodies, absolute paths, or raw
command output.

## Escalation conditions

Escalate stale or duplicate prerequisites, a changed commit/environment, an
unexpected candidate directory, failed held-input identity, non-deterministic
generation, or any quarantine cleanup ambiguity to release engineering.

## Explicit exclusions

This runbook does not claim readiness, support, signing, installation,
upgrade, rollback, vulnerability absence beyond the separate prerequisite, or
external attestation. It does not change matrix status or execute parsed
Markdown commands.
