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

The verifier requires PowerShell Core 7.5 or newer (`pwsh`); Windows
PowerShell 5.1 and older Core hosts are rejected rather than downgraded.

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
