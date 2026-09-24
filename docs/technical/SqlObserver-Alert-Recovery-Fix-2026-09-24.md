# Durable alert recovery confirmation

New HTTP alert rules require two accepted clear observations by default. An active or acknowledged alert stays in the same episode until the configured `clearConfirmationCount` (1–100) is reached. A matching observation resets clear progress. Unknown evidence is rejected; exact replay does not advance progress. Existing hysteresis determines the clear boundary, independently of firing confirmation.

The counter is carried through the evaluator, repository serialization, scoped database readers, canonical decision checks, and immutable replay results. Acknowledgement preserves it, rule edits reset it, and administrative disablement resolves immediately. Maintenance continues to suppress delivery without skipping recovery evaluation. Count-based confirmation can span a collection gap; it does not promise a minimum or maximum recovery duration.

## Upgrade and compatibility

Forward migration 0083 preserves existing rule settings with clear count 1 and state progress 0. New catalog entries and omitted HTTP create values use 2. HTTP update/disable requests must provide the field explicitly. The catalog moves to schema/contract version 2, with digest `fc53c14e0ad3d15364c0c02846cd93d3e020f25dc378b653ffc4c77a393bc0dd`.

Deploy the migration with the matching application. Newly submitted decisions must include the full state and integer clear counter. Historical replay documents and hashes are unchanged: an absent historical rule setting means exactly 1, and absent historical progress means exactly 0. Compatibility is selected from the stored replay record; substituted nonzero progress is rejected. History without a recorded result digest cannot substitute for an evaluation replay.

Claim validation locks rule rows before checking revisions and taking state locks. This prevents a rule edit from racing an evaluation that would otherwise commit recovery progress under an outdated policy. The versioned read functions retain target scoping, migrator ownership, and collector-only execution.

The shipped binary health rule retains zero hysteresis, allowing healthy `1` to clear. The existing connection-count template also retains its threshold and hysteresis; an appropriate nonzero delta depends on the operator's chosen threshold.

## Verification

- The new evaluator cases first produced 14 failures and four passing controls. After implementation, all 29 focused evaluator cases passed, including the existing M8 evaluator tests.
- All 19 focused HTTP cases passed. The initial sandbox run could not initialize the existing Windows Event Log logger; the unchanged tests passed when run with the required host access.
- Twenty targeted PostgreSQL cases passed across three runs: 12 recovery/upgrade/concurrency cases and eight existing affected alert cases. They exercise real migrated PostgreSQL 18.4, source evidence, claims, persisted state, acknowledgement, outbox delivery, reconfiguration, malformed/tampered state, equal-time distinct samples, a two-observation batch, maintenance, and legacy replay. Upgrade tests preserve acknowledged state and old replay hashes and verify rollback on missing/drifted catalog bindings. The concurrent edit test observes the actual PostgreSQL blocking relationship before checking revision rejection.
- Asset checks passed: 25 manifests, 248 direct pins, 37 nested contract pins, and 41 SBOM input pins. Only forward migration 0083 and its dependent checksums were added or refreshed.

Per the requested testing scope, no full Local validation, full PostgreSQL suite, or new broad CI run was requested. These results do not certify native SQL Server collection or the production Windows authentication deployment.
