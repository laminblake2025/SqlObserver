# M12 candidate evidence verification

## Status

Pending evidence-review procedure. It is a read-only inspection guide and is
not itself a release acceptance.

## Scope

Verify the exact three-file candidate inventory and cross-file identity,
content, commit, environment, and run bindings for the runbooks artifact.

## Supported versions and environment

Use only final UUID candidate directories produced for Release Windows Server
2022/2025 x64. The verifier's host-runtime 7.5 minimum and native locked
file boundary apply.

## Prerequisites

MatrixOnly and ContractOnly must pass. The candidate must be final, not
`.pending-*`, `.verify-*`, or `.quarantine`, and its four prerequisite records
must already be final UUID candidates from one commit and environment.

## Required role

An evidence reviewer with repository read access performs this procedure. A
release owner resolves any mismatch; no operator receives target or database
administrator privileges from this runbook.

## Blast radius

Inspection opens files read-only with exclusive sharing and changes no files,
services, targets, package stores, or certification policy.

## Procedure

Enumerate only the candidate's immediate files. Validate the three exact names,
then validate the artifact, test evidence, and provenance sidecar. Compare each
sidecar digest and size to bytes read through the same locked boundary; compare
run ID, commit SHA, environment ID, case ID, producer, and artifact kind.

## Verification

Require four cataloged documents, 12 sections each, bounded Markdown sizes,
relative paths only, approved procedure and prerequisite IDs, and no body text
or absolute path fields. Require a passed exact TRX result and a non-empty
artifact. Any extra file, duplicate key, stale digest, or identity change fails.

## Failure recovery

Do not repair a candidate in place. Mark the review failed, preserve the bytes
for investigation, and ask the producer owner to create a new UUID candidate.
Do not delete the failed directory unless its ownership and identity are proven.

## Evidence and UTC timestamps

Record the candidate UUID, source commit, environment, sidecar hashes, and UTC
review times in the external review record. The published artifact contains
identifiers and digests only; it never contains raw commands or document bodies.

## Escalation conditions

Escalate any path traversal, reparse point, alternate data stream, hard-link
ambiguity, file sharing failure, stale prerequisite, or mismatched TRX/result.

## Explicit exclusions

Verification does not execute commands, parse or replay Markdown, alter policy,
declare readiness/support/signing, or perform install, upgrade, rollback, or
database/target operations.
