# MCP catalog and diagnostic-field corrections — 2026-09-24

The existing 25 read-only tools now describe their actual inputs and evidence.
Each tool has a distinct title and description, every input has a description,
and the three metric-key inputs advertise the ten enabled domain catalog keys.
HTTP and stdio servers share instructions covering instance discovery, UTC,
window limits, pagination, evidence gaps, and unavailable content.

## Input contract

Public timestamps must be invariant ISO timestamps ending in `Z`. Window limits
are 24 hours for blocking history; seven days for query performance, observed
Agent failures, and diagnostic events; and 31 days for metric series and deadlock
search. Signed cursors retain their original effective window and argument
binding, including which optional arguments were omitted. Internal signed cursor
timestamps keep their existing representation.

Comparison accepts its four actual endpoints, with equal positive durations of
at most 31 days and no overlap. The unused generic `fromUtc` and `toUtc` inputs
were removed. Adjacent windows remain valid in either chronological order.
Defaults for limits and forecast horizon are advertised explicitly.

Controlled validation errors name the field and its valid bounds. Arbitrary
downstream exception text remains redacted. Invalid input records one terminal
audit outcome and does not reach query services. Nested malformed inputs retain
their exact audit digest; the strict 32-level tool limit includes the arguments
object and empty containers. The separate HTTP JSON depth limit remains intact.

## Response changes

These are intentional wire-contract changes. Clients must refresh their tool
catalog, and the bridge and authenticated server must use the matching approved
version.

| Tool | Resulting fields and meaning |
|---|---|
| Wait summary | Adds required nullable decimal strings `waitingTasksDelta`, `waitTimeMillisecondsDelta`, and `signalWaitTimeMillisecondsDelta`. Existing totals remain cumulative. Null preserves missing-baseline/reset evidence; zero remains zero. |
| Current and historical blocking | Adds required nullable blocker and root session IDs, plus collected `chainDepth` and `chainState`. History retains collection-loss evidence. |
| File I/O | Renames misleading `latencyMilliseconds` to `ioStallMilliseconds`, the cumulative combined read/write stall total. |
| Backup status | Replaces `databaseName` and `value` with `databaseFingerprint` and nullable `sizeBytes`; adds `sourceTimeUnknown`. `backupAtUtc` remains required and nullable. |
| Agent failures | Replaces `jobName`, `failureAtUtc`, and `reason` with `jobId`, `firstObservedAtUtc`, and `failureFingerprint`. This does not invent an execution timestamp. |
| Availability groups | Uses closed replica/database branches selected by `kind`, with distinct identity and state fields, visibility scope, and `stateAvailable`. Database rows no longer pretend their fingerprint is a replica role. |

Typed mappings, disclosure allowlists, required fields, and JSON Schemas change
together. Raw query text, plan XML, messages, commands, credentials, and provider
details remain excluded. The mapper and output-schema classes were extracted
from `McpRuntime.cs` to keep these contracts separately readable.

The approved catalog digest is
`670C8BB620C599FBFA605C2DB6BFAB3827722AEA23D42EDE8DF82386D4F1F304`.
Titles now participate with names, descriptions, and input schemas. Output schemas
still do not participate in the existing digest algorithm; the simultaneous
metadata revision gives this response change a new approved server identity.
All explicit bridge, certification, schema, and checksum approval points agree.

The complete discovery/catalog stdio transcript is approximately 73 KB. The
native certification harness therefore uses a separate 128 KiB stdout bound for
those two protocol frames. Diagnostic output and adversarial-process bounds stay
at 64 KiB, and frame-count/envelope checks remain in force.

## Validation and remaining work

Catalog/input regressions first produced 54 failures and 23 passing controls;
all 77 passed after implementation. Projection regressions produced 20 failures
and seven passing controls. The combined MCP suite then passed 338 of 342 cases.
The four remaining cases exposed two outdated fixtures and the two protocol
transcripts exceeding their old bound; all four passed after targeted fixes.

Local review also reproduced one empty-container depth-boundary failure with two
passing controls. The final input class passed all 80 cases, including that fix
and stricter fractional timestamp syntax. The combined run includes all 27
projection regressions, all 25 typed output fixtures, cursor continuation, audit,
authorization, HTTP, and stdio coverage. No unrelated full Local or database run
was repeated for this mapper/catalog-only batch.

Evidence is under `artifacts/revamp-mcp-catalog/test-results/`. The release pin
verifier passed 25 manifests, 247 direct pins, 37 nested contract pins, and 41
SBOM inputs. The attempted independent review could not complete because its
agent hit an account usage limit; the final source review was local.

Incident discovery, a separate metric-catalog tool, dimension-selectable
forecasts, actual Agent execution time, backup source corrections, split file
stalls, and all-tools PostgreSQL/native certification remain later work.

Follow-up: [metric-catalog discovery](SqlObserver-Mcp-Metric-Catalog-2026-09-24.md),
[backup timestamp corrections](SqlObserver-Backup-Timestamp-Fix-2026-09-24.md),
and [separate file stalls](SqlObserver-File-Stall-Fix-2026-09-24.md) have since
landed. The counts and test evidence above describe this earlier checkpoint.
