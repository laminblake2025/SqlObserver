# MCP stdio and protocol repairs — 2026-09-24

This batch repairs bridge startup behavior and the release harness's protocol
checks, following reliability checkpoint `1e693044ecd231a90b84e38fe84c6bc9058fa4ef`.

## Bridge startup and lifecycle

The stdio bridge advertises the stable name `SqlObserver.McpStdio`, the exact
approved catalog version, and read-only server instructions. It connects to and
verifies the upstream server before advertising local tools. Catalog and
identity mismatches still return exit code 3. Missing optional discovery identity
also returns 3; the SDK throws when that missing identity is accessed, so the
bridge handles that accessor failure explicitly.

Expected connection, discovery, and catalog-read failures return exit code 4
with one fixed diagnostic line. Provider exception messages and stack traces
are not printed. A single ten-second deadline covers the complete startup
sequence, including fallback and catalog retrieval. The prior transport
connection timeout alone did not bound that work. Caller cancellation remains
distinct from an upstream failure, and the startup deadline ends before normal
proxy operation.

SDK and host log providers are suppressed and host lifetime status messages are
disabled. Protocol frames remain on stdout; startup diagnostics use stderr.
The upstream client and transport are disposed on success and failed startup.
An internal HTTP/stream/writer seam exercises the same bridge without changing
global Console or opening native sockets. Public HTTPS endpoint validation,
Windows credentials, certificate revocation checks, disabled redirects/cookies,
and zero reconnection attempts remain in force.

## Current and downlevel protocol evidence

The pinned SDK supports two distinct paths. Current protocol `2026-07-28` uses
`server/discover` and per-request metadata. Its server identity is under
`result._meta["io.modelcontextprotocol/serverInfo"]`. Downlevel protocol
`2025-11-25` uses `initialize`, an initialized notification, and the older result
fields. The release harness now uses the appropriate path for each revision
and includes the required current-protocol HTTP headers.

The HTTP response parser accepts the SDK's exact `event: message` SSE line.
It still requires one data frame and a closed, duplicate-free JSON-RPC success
envelope, rejects other event names and repeated event lines, and retains the
existing byte limits. Tests cover malformed envelopes and multiple frames.

The native-child harness now has separate current and downlevel transcripts.
Those exact transcript builders and output assertions are exercised locally
through the real bridge over pipe streams. This verifies their wire behavior
without claiming that the published Windows executable or Negotiate/TLS lab
has been certified.

## Regression evidence

After adding the injectable seam without changing behavior, all 216 existing
MCP tests passed. The first ten bridge regressions then produced six failures
and four passing controls: missing metadata across four protocol combinations,
unbounded startup, and an uncaught upstream exception. All ten passed after
the fixes and one fixture correction: the client does not own supplied pipes,
so the test must explicitly complete its writer to model EOF.

The original HTTP release probe failed both revisions because of its SSE parser.
After correcting the parser, downlevel passed and current still failed because
the harness used initialization. Discovery, identity placement, and required
headers were corrected against the pinned SDK and then verified in process.
Draft expectation and compile corrections are not additional product findings.

Further startup controls reproduced the missing-identity accessor failure while
four identity/catalog controls passed. The narrow identity guard fixes that
failure. Catalog-read exceptions and a stalled catalog request both return the
safe failure result without advertising tools or writing tool-audit rows.

Evidence is retained under `artifacts/revamp-mcp-bridge/`, including
`bridge-seam-controls.trx`, `bridge-red.trx`, `bridge-green-eof.trx`,
`protocol-probe-red.trx`, `protocol-probe-after-sse.trx`,
`protocol-http-header-green.trx`, and `bridge-startup-controls.trx`.
Independent read-only review, including the final protocol and identity
corrections, found no actionable issues.

The complete MCP suite passed 238 tests, including 22 new bridge and protocol
cases. Canonical Local validation then passed 1,874 .NET tests and 147 frontend
tests with zero failures or skips, plus the build, TypeScript, web asset, and
release contract checks. The independent pin verifier passed all 25 manifests,
247 direct pins, 37 nested contract pins, and 41 SBOM input pins. Local evidence
is in `artifacts/revamp-mcp-bridge-reviewed/test-results/` and
`artifacts/revamp-mcp-bridge-reviewed-local.log`.

No SQL migration, collector bundle, dependency, tool catalog, or protocol-version
approval changes are part of this batch. Native executable/TLS/Negotiate
certification remains separate work.
