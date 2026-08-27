# ADR-0019: Release identity and evidence

## Status

- Status: Proposed
- This record is not Accepted and does not declare a releasable product.

## Context

M12 evidence must prove the exact product, commit, environment, and case result
under test. A green local build is useful implementation evidence but cannot
substitute for Windows, PostgreSQL, SQL Server, browser, installer, signing,
and sustained-load qualification. Release identity also has legal and supply
chain consequences that cannot be inferred from a repository commit.

## Invariants from accepted architecture

- The certification matrix and schemas are versioned source of truth, and the
  verifier is read-only and fail-closed. Local validation reports no release
  evidence; Release requires every required lane and case with no hidden skips.
- Evidence, artifacts, products, environment identities, run IDs, commit SHAs,
  timestamps, and hashes are bound and checked. Installer lifecycle cases bind
  the same used product. A provenance sidecar is not a substitute for hashing
  the artifact itself.
- No current milestone creates a production support claim. Support requires
  compatibility, security, installation, upgrade, performance, recovery, and
  accessibility evidence across the published matrix.

## Recommended defaults (not yet decisions)

Use one immutable release identity composed of an owner-approved product
version, source commit SHA, build/run ID, certification-manifest version, and
cryptographic digests of the shipped products and sidecars. Produce the signed
installer/binaries from a controlled build, record the signing result as a
separate certification case, and retain verifier provenance with the release
record. Every lifecycle case should consume the same product digest while
retaining distinct parsed evidence.

Adopt a reproducible, reviewable versioning policy with a clear pre-release vs
release distinction, deterministic asset/dependency inputs, and an explicit
security-update/backport policy. Publish a support matrix and release notes
only after the Release profile passes; do not relabel Local evidence as release
evidence.

## Owner decisions still required

- Choose the legal publisher name, license, product/versioning scheme, and
  signing identity/service, including key custody, timestamping, rotation, and
  revocation response.
- Approve the release approver/owner, artifact retention and evidence access
  policy, and the canonical build environment.
- Set supported OS/.NET/PostgreSQL/SQL Server/browser patch floors, capacity/SLO
  and recovery targets, and the exact initial support matrix.
- Decide whether sensitive-content features are production-available and define
  the key policy before any release evidence includes them.

## Alternatives and consequences

- Commit-only identity: easy to communicate, but does not distinguish rebuilt
  or tampered binaries from the intended product.
- Digest-only identity: strong artifact binding, but weak human versioning and
  upgrade communication without an owner-approved version policy.
- Signing in an uncontrolled developer environment: fast iteration, but weak
  key custody and trust evidence.
- Release on partial lanes or skipped external cases: faster publication, but
  invalidates support claims and hides environmental risk.

## Downstream implementation and migration gates

Keep certification matrix/schema/verifier updates review-visible and update
their pinned asset digests plus signed verifier provenance whenever policy
changes. Release runs must pass preflight on the supported 64-bit Windows host,
Docker PostgreSQL lab, live SQL Server configuration, browser and installer
evidence, signing evidence, and all required lanes/cases with parsed result
files. Validate product/sidecar hashes, uniqueness, path safety, timestamps,
environment identity, commit binding, and same-product installer lifecycle
binding. Obtain legal/publisher/signing approval, publish support and rollback
runbooks, and preserve an auditable release record before making any support or
release statement. Until then, M12 remains proposed and incomplete.
