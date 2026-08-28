# M12 certification evidence

`m12-certification-matrix.v1.json` is the versioned source of truth for M12
lanes, case IDs, and each case's allowed environment identities and required
facts. A run manifest must conform to
`m12-certification-manifest.v1.schema.json` and be accepted by
`tools/verify-test-results.ps1` before it can be used as evidence.
The versioned matrix and both schemas are pinned by
`m12-certification-assets.sha256`; changing a schema without changing the
reviewed pin is rejected. The verifier additionally compiles the policy
identity, exact 20-lane/35-case inventory, schema version, and expected asset
digests into its source. The pin file therefore cannot authenticate a changed
policy by itself.

Policy updates are deliberate source changes: update the matrix and schemas,
update the verifier's approved inventory and digest constants, add or update
ReleaseTests, obtain code review, and produce a new signed verifier provenance
record before using the new policy for release. A committed source change is
review-visible; no in-repository file can defend against a malicious source
change without the later signed provenance step.

The `Local` profile proves only environment-independent lanes and always
reports `releaseEvidence: false`; its explicit external omissions are not a
release claim. The `Release` profile requires every lane, a known release
environment, the current commit, verified artifact/sidecar/evidence hashes, and zero
missing, unavailable, skipped, not-run, or failed cases. The verifier is
read-only and fail-closed. A manifest records one or more distinct environment
records and binds every case, artifact, and evidence file to an environment,
run ID, and commit. Each matrix case declares its allowed artifact kinds;
every referenced artifact must match that case's exact producer and kind. An
artifact's `provenance` member is only a `{path, sha256}` reference to a
separate JSON sidecar. The sidecar has exactly `artifactId`, `kind`,
`producerId`, `artifactSha256`, `artifactSize`, `commitSha`, `runId`,
`environmentId`, and `createdAtUtc`, plus an optional `productId` when an
artifact certifies a shipped product separately from its per-case evidence.
The top-level `products` array records each such product's physical `path`,
`sha256`, `size`, `runId`, `commitSha`, and `environmentId`. The verifier hashes
each product through the same locked/native canonical boundary as evidence,
requires the sidecar and artifact `productId` to match, and requires every
Release installer lifecycle case to bind the same used product. This permits
each lifecycle case to retain unique evidence without accepting an arbitrary
claimed digest.
Evidence must be a parsed `json`, `trx`, or `junit` result; manifest counters
alone are never accepted.

The pending `m12-mcp-protocol` case is produced only by
`tools/run-m12-mcp-certification.ps1` on an approved Release Windows Server
2022/2025 x64 host. The producer requires an externally supplied HTTPS `/mcp`
endpoint and the deployed `SqlObserver.McpStdio` executable, runs the live
protocol/process-boundary test, and atomically publishes only its sanitized
JSON result, detail artifact, and verifier-compatible provenance sidecar under
ignored `TestResults/m12/<run-id>/`. A producer failure leaves no candidate run
directory and never changes the matrix or claims Release evidence. It uses
`.pending-<UUID>` and `.verify-<UUID>` quarantine states. If safe cleanup fails,
the producer creates no final UUID candidate; best-effort cleanup may partially
remove or leave a `.pending-*`/`.verify-*` directory for operator review. Such
quarantine directories are never followed through a reparse point and are not
candidate evidence. Its
environment contract is `SQLOBSERVER_RELEASE_MCP_ENDPOINT`,
`SQLOBSERVER_RELEASE_MCP_ENVIRONMENT` (`release-windows-server-2022` or
`release-windows-server-2025`). The stdio executable is always the canonical
repository-root Release artifact; arbitrary executable paths are rejected.
The release lab must also provide distinct existing target IDs through
`SQLOBSERVER_RELEASE_MCP_AUTHORIZED_INSTANCE_ID`,
`SQLOBSERVER_RELEASE_MCP_CANCELLATION_INSTANCE_ID`, plus
`SQLOBSERVER_RELEASE_MCP_DENIED_INSTANCE_ID`,
`SQLOBSERVER_RELEASE_MCP_DENIED_TARGET_ATTESTATION` and its externally supplied
lowercase `SQLOBSERVER_RELEASE_MCP_DENIED_TARGET_ATTESTATION_SHA256`. The
attestation is a closed, bounded JSON record proving the denied target exists
for the selected environment; target IDs are consumed only by the live test
and never written to evidence.
Its closed protocol identity is separately versioned in
`m12-mcp-protocol-contract.v1.json` and authenticated by its adjacent SHA-256
pin; this contract is intentionally outside the global matrix asset pin and
does not promote the pending lane.

The passive SQL Server cases are produced only by
`tools/run-m12-sqlserver-certification.ps1`, with one of the exact case IDs
`m12-sqlserver-2019-passive`, `m12-sqlserver-2022-passive`, or
`m12-sqlserver-2025-passive`. The producer derives major 15/16/17 and the
corresponding Windows Server 2022/2025 environment, requires the Release
profile and strict integrated-security/trusted-TLS connection contract, and
selects the explicit `RequiresM12SqlServerRelease` trait. The versioned
14-collector passive contract is pinned by its adjacent two-line SHA-256 file.
Before/after target snapshots must be byte-identical; only three sanitized
JSON files are published under the ignored run root. Ordinary validation
explicitly excludes this trait; a missing or invalid release prerequisite is a
producer failure, never a test skip.

The pending reports lane is produced only by
`tools/run-m12-reports-certification.ps1`. It maps the three reports cases to
their exact release test and facts, accepts only Release Windows Server
2022/2025 identities, and uses the validated `SQLOBSERVER_RELEASE_POSTGRES`
connection through the existing PostgreSQL fixture. The producer uses a strict
non-secret connection parser and rejects ambiguous host members, credentials,
certificate bypasses, and non-`VerifyFull` TLS. The versioned
`m12-reports-contract.v1.json` and closed schema pin the four report
definitions, section formats, bounds (including 7-day detailed and 31-day trend
windows), snapshot retention, concurrency, HTML encoding, RFC4180 CSV, and
formula neutralization rules. The producer accepts only a strict-parser-validated
multi-host PostgreSQL topology using `VerifyFull` and publishes
only three sanitized JSON files after the selected test passes. It performs a
fresh non-incremental Release build, requires a clean trusted Git tree before
creating evidence, and pins the selected PostgreSQL live test source and
project file alongside the report implementation sources. The producer script
itself is bound by the validator's external source pin;
the producer test is intentionally not self-pinned because changing its own
source would necessarily change any hash embedded in that source. Raw reports
and exports, identifiers, connection strings, SQL, and provider errors are
never published. Local and ordinary Release validation exclude
`RequiresM12ReportsRelease`; missing prerequisites fail the producer and leave
the cases pending.

The pending observability lane is produced only by
`tools/run-m12-observability-certification.ps1`. It accepts Release Windows
Server 2022/2025 identities, selects exactly one readiness or collector
telemetry test, and validates the closed source-pinned observability contract.
The product path checks PostgreSQL 18 readiness through the existing
compatibility port and captures only bounded in-process OpenTelemetry signals;
it never sends telemetry to an external endpoint for certification. Output is
sanitized JSON evidence published atomically only after a fresh Release test,
and the matrix cases remain pending until external evidence is accepted.

The verifier requires PowerShell Core 7.5 or newer (`pwsh`); Windows
PowerShell 5.1 and older Core hosts are rejected rather than downgraded.

The pending supply-chain SBOM case is produced only by
`tools/run-m12-supply-chain-certification.ps1`. `m12-supply-chain-contract.v1.json`
and its closed schemas pin a deterministic CycloneDX 1.7 subset. The Node-only
generator consumes exactly three fresh host `.deps.json` files, a sanitized
production `pnpm list` and the decision-neutral web asset catalog; all inputs
are bound by the bijective `m12-sbom-inputs.v1.json` manifest. Timestamp, commit,
run and environment are explicit inputs, and component/dependency/string/JSON
bounds are fail-closed. ContractOnly performs no publish; live certification
requires both supported Windows Server environments, x64, a clean trusted tree,
fresh Release publishes and one exact release test. Only the SBOM, sanitized test
evidence, and nine-field provenance sidecar may be published, with pending matrix
status unchanged until external evidence is accepted.

The same producer accepts `-CaseId m12-licenses` for the separate pending license
case. It regenerates the frozen SBOM, resolves every NuGet/npm runtime component
against the reviewed Apache-2.0/MIT/PostgreSQL policy (including the exact SNI
runtime override), and publishes only deterministic license evidence, test evidence,
and provenance after the license-bound Release test passes. ContractOnly validates
the independently pinned license contract and schema assets without building or
publishing; no live Server 2022/2025 evidence is claimed locally.

Release validation also requires a supported 64-bit Windows runner, a running
Docker PostgreSQL lab, live SQL Server configuration, browser/installer/OS
evidence, signing evidence, and `SQLOBSERVER_CERTIFICATION_MANIFEST` pointing
to the complete release manifest. These are intentionally preflight failures
when absent; a local run cannot substitute for them.

The read-only `tools/assess-release-identity.ps1` helper provides a bounded,
decision-neutral assessment of this policy. Its closed contract is
`release/contracts/release-identity-assessment.v1.schema.json`; it reports the
current commit and policy identity, runs `MatrixOnly`, and preserves the
20-lane/35-case inventory (8 implemented lanes/cases and 27 pending cases).
It always returns `status: not_ready`, `releaseEvidence: false`, and
`readyToRelease: false`, and emits no product, publisher, license, signing,
environment, path, or command-error values.

Evidence and provenance sidecar files are bounded, non-empty, and opened with
a read-only handle that denies concurrent writes/deletes while hashing.
Reparse-point ancestors are
rejected and canonical paths are deduplicated; this is the Windows kernel
sharing guarantee used by the verifier. Stable file-ID APIs are not portable
to every host; Release fails closed when Windows native final-path/file-ID
retrieval is unavailable. Ordinary artifact, provenance sidecar, and evidence files
must be created within 24 hours before `generatedAtUtc` and no later than five
minutes after it; the generated timestamp itself is also limited to that
current-run window. Reproducible artifacts are not accepted by this foundation
profile. Artifact, evidence, and sidecar paths, identities, and content hashes
must all be unique; sidecars are subject to the same traversal, ADS,
reparse-point, and sharing checks as artifacts and evidence.

Installer lifecycle artifacts may be direct signed Burn `.exe` files; the
verifier accepts the file type and the separate signing case/evidence records
the signature result. The verifier does not claim to perform Authenticode
verification itself.
