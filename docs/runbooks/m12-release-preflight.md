# M12 release preflight

## Status

Pending M12 release-certification documentation. This runbook is exercised by
the pending runbooks lane only; it is not a support or readiness declaration.

## Scope

This procedure covers repository-bound preflight, policy identity, and the
fixed MatrixOnly/ContractOnly order. It does not install, upgrade, configure,
or modify a product or target.

## Supported versions and environment

The pending lane accepts only Release Windows Server 2022 or Windows Server
2025 x64 identities. PostgreSQL 18.x and SQL Server support remain external
qualification gates described by the support matrix.

## Prerequisites

Use a clean, trusted checkout at the intended commit. The certification matrix,
all referenced schemas, and the producer must be present. Do not substitute a
local profile, copied checkout, network checkout, or mutable working tree.

## Required role

An authorized release engineer performs the read-only preflight; a second
reviewer owns the later evidence acceptance decision. No database, Windows
service, or target-admin role is required for this runbook.

## Blast radius

The preflight reads repository files and emits no candidate evidence. It has no
runtime, database, service, target, or network side effect.

## Procedure

Follow this fixed order: verify the policy identity with MatrixOnly; validate
each closed producer contract with ContractOnly; then inspect the four runbook
documents and their catalog. ContractOnly is side-effect free. The producer,
not Markdown, owns any live command arrays.

## Verification

Confirm MatrixOnly preserves every lane and leaves `m12-runbooks` pending.
Confirm the five supply-chain cases are ordered SBOM, licenses,
vulnerability-scan, provenance, runbooks, and that each has the exact Release
environment identities and `supply-chain-evidence` kind.

## Failure recovery

Stop on any policy, parser, pin, or clean-tree failure. Keep the checkout
unchanged and retain the failure details outside certification evidence. Do not
retry against a different commit or convert a Local result into Release
evidence.

## Evidence and UTC timestamps

Record the commit SHA, policy identity, run ID, and UTC start/end times in the
review record. MatrixOnly and ContractOnly output is diagnostic only and must
not be published as a candidate artifact.

## Escalation conditions

Escalate a changed matrix, stale pin, reparse point, alternate data stream,
unexpected file, parser error, or environment mismatch to release engineering
and security review. Escalate missing external infrastructure separately.

## Explicit exclusions

This runbook makes no readiness, support, signing, installation, upgrade,
rollback, or live-target claim. It never executes commands copied from
Markdown, changes the matrix, or deletes unowned quarantine material.
