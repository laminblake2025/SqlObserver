# ADR-0009: Clean-room boundary

- Status: Accepted
- Date: 2026-08-23

## Context

SqlObserver may address diagnostic outcomes also addressed by mature SQL Server monitoring products. Functional similarity at that level does not permit reuse of a competitor's protected expression, internal design, confidential material, or distinctive product identity. Without an explicit boundary, design shortcuts could contaminate code, data models, documentation, tests, and assets.

## Decision

Develop SqlObserver as an independent clean-room implementation. Requirements may come from this repository's original brief, public product descriptions at the level of user outcomes, official platform/standards documentation, authorized experiments, original user research, and properly licensed dependencies.

Do not copy, adapt, translate, trace, transcribe, extract, decompile, or closely imitate proprietary code, SQL, schemas, APIs, wire shapes, internal identifiers, UI layouts, screenshots, text, icons, names, assets, test fixtures, thresholds, or undocumented behavior. Do not use leaked/private documentation, binaries, packet captures, database inspection, trial-installation internals, or NDA material as implementation references. Renaming, reformatting, generation, or machine translation does not cure copying.

Create original architecture, terminology, schema, interface, UI, implementation, documentation, and assets. Record meaningful source provenance and dependency licenses. Contributors with relevant non-public exposure disclose it privately and abstain from that design area until an independently sourced design is available. Uncertain material is quarantined and independently replaced.

The operational policy and review checklist live in [the clean-room product boundary](../product/clean-room-boundary.md); original vocabulary lives in [terminology](../product/terminology.md).

## Consequences

- Reviews include provenance and originality, not only correctness.
- Direct compatibility with a proprietary private schema/API/UI is neither a goal nor a valid test oracle.
- Work may be redesigned or removed when provenance cannot be established, even if it is technically useful.
- Public facts can support independently expressed requirements, but public access does not grant a license to copy expressive material.
- This boundary applies to generated output and third-party submissions as fully as hand-written work.
