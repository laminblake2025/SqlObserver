# M12 failed-run quarantine and escalation

## Status

Pending-only failure handling for the supply-chain producer. Quarantine is an
investigation state, never a candidate and never release evidence.

## Scope

Handle owned `.pending-<UUID>` and `.verify-<UUID>` roots after a producer
failure, with a final `.quarantine` handoff when safe cleanup is not possible.

## Supported versions and environment

The same approved Release Windows Server 2022/2025 x64 identities and Core 7.5
or newer verifier boundary apply. The rules are independent of product target
versions and do not certify support.

## Prerequisites

Capture the producer run ID, owned root identity, owner/claim marker identities,
and failure code. Confirm the path is beneath the repository's ignored
TestResults M12 root and is not a reparse point.

## Required role

The producer owns cleanup of its own nonce root. A release engineer and security
reviewer handle a quarantine that remains after ownership or identity checks
fail.

## Blast radius

Only the current producer's verified root and markers are in scope. Foreign
final candidates, other pending/verify roots, and any unowned quarantine are
never touched.

## Procedure

On failure, close held handles and recheck byte and file/directory identity.
Remove owned temporary files only when identity and content still match. If a
root cannot be safely removed, atomically move that owned root to a unique
`.quarantine` name. Never follow a reparse point or execute any command named
in evidence or Markdown.

## Verification

Confirm no final UUID candidate was created after a failed run. Confirm the
quarantine remains outside candidate enumeration, retains all descendants for
review, and has no publication sidecar accepted by the verifier. Confirm the
matrix remains pending and workspace policy files are unchanged.

## Failure recovery

Leave an unresolved quarantine intact. Preserve its UUID, identity, failure
code, and UTC time; obtain release/security review before any later archival
action. A new attempt must use a new UUID and fresh locked inputs.

## Evidence and UTC timestamps

Record only sanitized run ID, failure class, path identity, commit, environment,
and UTC timestamps in the review ticket. Do not copy raw scanner output,
credentials, commands, or Markdown bodies into certification evidence.

## Escalation conditions

Escalate cleanup races, replacement or hard-link detection, reparse points,
unexpected descendants, marker mismatch, failed handle closure, or any request
to delete an unowned quarantine.

## Explicit exclusions

This procedure does not delete unowned material, change matrix status, execute
parsed commands, claim readiness/support/signing, or perform installation,
upgrade, rollback, target, service, or database actions.
