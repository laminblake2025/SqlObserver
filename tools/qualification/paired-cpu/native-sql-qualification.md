# Native SQL/Windows CPU association qualification

This is an opt-in, finite, read-only qualification scenario for the staged CPU
sources. Run it on a Windows SQL Server host under the actual integrated monitoring
identity, against a SQL instance on that same host. It exercises the production
SQL connection policy and owned SQL/TCP/PDH sampling sequence. It does not enable a
collector, write monitoring data, grant permissions, register a target, or admit a
CPU comparison to the dashboard.

A complete local TCP association remains `CandidateLocalAssociation`.
`NativeQualification` remains `Absent` and `TargetComparisonAdmitted` remains
`false`, including when the scenario succeeds. Numerically comparable raw CPU
counters are a separate result; they do not upgrade source admission.

## Preconditions

- Use Windows with SQL Server 2019, 2022 or 2025. Run the harness on the SQL host,
  not a workstation that connects to a remote SQL instance. The native helpers
  read only the local machine.
- Use the provisioned monitoring Windows identity. The SQL profile must report
  integrated Kerberos or NTLM authentication, encrypted transport and
  `IsSysAdmin == false`. The version-specific read permission is
  `VIEW SERVER STATE` for SQL Server 2019 or `VIEW SERVER PERFORMANCE STATE` for
  SQL Server 2022/2025. The harness makes no permission changes.
- The Windows identity must separately have counter/CIM access and
  `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE` (`0x101000`) access to the
  actual SQL process. Creation/image queries and same-handle liveness checks need
  these rights; SQL grants and Performance Monitor Users membership alone do not
  establish them. Both rights are checked against the process object, not the
  Windows service object's access list. See Microsoft's
  [process access-rights reference](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights).
  Denial remains a qualification failure; the harness does not grant rights,
  enable debug privileges or replace the monitoring identity.
- Supply the reviewed endpoint and certificate identity. The product connection
  factory uses integrated security, mandatory encryption, certificate validation
  (`TrustServerCertificate=false`) and disabled MARS. A failure is not permission
  to substitute SQL credentials, trust an unvalidated certificate, change routing,
  or run as sysadmin.
- Supply the expected host binding independently of this capture, using the
  existing reviewed target-to-host binding and its fingerprint key. Do not derive
  an expected value from the helper's just-observed machine UUID to make a failed
  binding pass. The keyed UUID is a source identity witness, not a hardware
  authenticity certificate or proof of SQL locality.
- Confirm that this invocation is authorized for the selected target. Target ID,
  target revision, host-binding revision and expected host fingerprint are explicit
  scenario inputs. They are not a replacement for product authorization.

Keep keys, connection details and machine identity out of shared terminal output
and source control. Review the evidence output before sharing it.

## Configuration and invocation

The dedicated harness is
`tests/SqlObserver.IntegrationTests.SqlServer/PairedCpuNativeQualificationTests.cs`.
It uses its own opt-in contract, not the general Local/Release SQL lab variables.
Supply these variables in the monitoring identity's process environment using the
approved local secret/configuration mechanism; do not put a real key in this file
or a checked-in shell script.

| Environment variable | Required value |
|---|---|
| `SQLOBSERVER_CPU_QUALIFICATION` | Exactly `1` to opt in. |
| `SQLOBSERVER_CPU_HOST` | Reviewed co-located SQL endpoint host. |
| `SQLOBSERVER_CPU_PORT` | Explicit SQL TCP port. Do not assume 1433. |
| `SQLOBSERVER_CPU_CERTIFICATE_HOST` | Expected certificate host identity for validated TLS. |
| `SQLOBSERVER_CPU_TARGET_ID` | Nonempty lowercase canonical `D`-format target UUID for this scenario. |
| `SQLOBSERVER_CPU_TARGET_REVISION` | Positive canonical decimal target configuration revision. |
| `SQLOBSERVER_CPU_HOST_BINDING_REVISION` | Positive revision of the independently provisioned host binding. |
| `SQLOBSERVER_CPU_HOST_PROFILE_REVISION` | Positive host capability-profile revision, equal to the host-binding revision. |
| `SQLOBSERVER_CPU_EXPECTED_HOST_FINGERPRINT` | Independently provisioned nonzero 64-character lowercase-hex UUID-derived host HMAC for the selected target. |
| `SQLOBSERVER_CPU_FINGERPRINT_KEY` | Secret nonzero 64-character lowercase-hex fingerprint key matching that host binding. |
| `SQLOBSERVER_CPU_EXPECTED_SQL_MAJOR` | `15`, `16` or `17` for SQL Server 2019, 2022 or 2025. |
| `SQLOBSERVER_CPU_EXPECTED_WINDOWS_SID` | Independently provisioned SID of the intended monitoring Windows identity. The harness compares it with the actual process identity and requires current/original SQL identity matches on the owned capture connection before and after native sampling. |

There is no connection-string, password, caller PID, certificate-trust bypass or
native-qualification input. Run from the repository root under the monitoring
identity, with the repository's pinned .NET SDK available. The following command
selects only the dedicated native scenario; it does not run the full SQL lab suite:

```powershell
dotnet test ./tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj --filter "FullyQualifiedName~PairedCpuNativeQualificationTests" --logger "trx;LogFileName=paired-cpu-native-sql.trx" --results-directory ./artifacts/paired-cpu-native-sql
```

The native scenario carries both `RequiresSqlServer` and
`RequiresPairedCpuQualification` category traits. Explicitly selected native
tests fail closed when opt-in or required configuration is absent; they do not
silently skip. Retain the complete test result and bounded xUnit evidence output
for the invocation. A passing environment-contract test only proves configuration
validation with managed inputs; it makes no SQL or Windows source claim.

To select only the separate managed environment-contract tests:

```powershell
dotnet test ./tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj --filter "FullyQualifiedName~PairedCpuQualificationLabContractTests" --logger "trx;LogFileName=paired-cpu-environment-contract.trx" --results-directory ./artifacts/paired-cpu-environment-contract
```

Do not infer completion from command exit alone: inspect executed/skipped counts,
the scenario assertions and the recorded gap/results. Missing or denied source
evidence is not repaired by changing the expected host fingerprint to the observed
value or elevating the monitoring identity.

The scenario reads discovery/identity evidence and performs a bounded capture. It
does not generate a load, start or stop SQL/Agent services, kill SQL processes,
change grants or configuration, or exercise a restart/boot transition. Those
broader cases require a separately authorized lab exercise; they are not implied
by this invocation.

## What the scenario observes

The harness checks the actual Windows process token and performs production
capability discovery. Those checks remain separate from SQL capture-session
identity. It no longer opens another SQL connection for an identity preflight.
Both actual-capture cases require identity evidence before and after native
sampling on the same connection owned by the SQL witness reader.

The harness uses the internal opt-in `ReadAttestedBracketAsync` path. Its
`SqlCpuAttestedWitnessBracket<T>` contains the ordinary `Bracket` plus
`BeforeIdentity` and `AfterIdentity`. The sequence is identity-before, SQL
before-witness, native completion and disposal, SQL after-witness, then
identity-after. Both identity receipts must bind the execution and the witness
connection. Missing, mismatched or refused evidence prevents publication; an
initial refusal starts no native sampler.

All eight identity checks must pass at each boundary: current SID matched,
original SID matched, non-sysadmin context, integrated authentication, TCP
transport, encrypted transport, effective `CONTROL SERVER` absent and effective
`ALTER ANY LOGIN` absent. Only comparison booleans are reported; principal names,
SID bytes and the configured SID are withheld. SQL's `SUSER_SID()` identifies the
current security context, including impersonation; `SUSER_SID(ORIGINAL_LOGIN())`
looks up the login that originally connected. Lookup or collation failure is a
refusal, not permission to disable validation. See Microsoft's
[SUSER_SID](https://learn.microsoft.com/en-us/sql/t-sql/functions/suser-sid-transact-sql)
and [ORIGINAL_LOGIN](https://learn.microsoft.com/en-us/sql/t-sql/functions/original-login-transact-sql)
documentation.

This binds the observed actor checks to the actual capture connection. It is not
a complete grants audit, proof of unchanged permissions between probes, or
qualification of every SQL version, native provider or failure scenario. The two
effective-permission exclusions narrow the accepted monitoring context without
establishing universal least privilege.

The public `ReadBracketAsync` path remains deliberately unattested. Existing
`PairedCpuCaptureSource`, schema-1 persistence and retained diagnostic API are
unchanged; older or ordinary capture receipts must not be described as carrying
this new identity pair. The v1/v2 pinned witness SQL assets are unchanged. The
opt-in identity query is fixed application code and accepts only the privately
copied expected SID, not a caller-supplied script or qualification flag.

The SQL reader holds one connection while collecting the before-witness,
TCP-before, raw CPU pair, TCP-after and SQL after-witness. It uses the pinned SQL
source and fixed local Windows helper code; it accepts no arbitrary script,
counter path, remote computer, credentials or write command.

SQL connection ID, SQL session ID, SqlClient connection ID and the source owner's
actual process ID are separate continuity evidence. The observed SQL PID is used
for native process ownership; `sqlservr.exe`, a node name, a conventional port or
an instance-name ordinal cannot substitute for this association. Both bounded
IPv4 and IPv6 owner tables must be complete. The qualifier requires an exact
established server tuple owned by SQL's PID and its reverse owned by the collector
process. Unsupported transport, ambiguous mapping or a missing tuple is a gap.

Native process handles preserve creation epochs and explicit liveness observations.
Running-process exit FILETIME is retained but does not establish liveness. Raw PDH
types, statuses, time bases and each counter's native interval anchors are retained.
An unavailable or ambiguous legacy Process counter does not erase independent OS
evidence. A shared query alone does not prove matched process/OS intervals.

The coordinator has one original deadline, at most 26 seconds, within the SQL
reader's original request budget, at most 30 seconds. Each child must stop and
dispose before the next stage. Cancellation does not prove termination: unproven
termination or cleanup retains ownership and quarantines that target. A late
payload cannot become a successful sample or authorize an overlapping retry.
These are capture budgets, not a 30-second limit for discovery and the entire
test invocation together. The capture's same-session identity reads are inside
its original budget.

## Scenario assertions and recorded evidence

The native class contains three separately reported cases:

| Case | Required result; limited scope |
|---|---|
| `ActualSqlAndLocalNativeSocketProcessEvidenceProduceOnlyACandidate` | Actual complete captures, the validated same-session identity pair and a positive `CandidateLocalAssociation` are required. A missing/ambiguous/denied native association fails this case. It does not require usable PDH process arithmetic and does not bless it. |
| `ActualCandidateRawCountersHaveIndependentArithmeticEligibilityWithoutAdmission` | The validated same-session identity pair and a positive candidate are required again. The original staged evidence keeps process arithmetic unavailable with `SourceQualificationAbsent`; independent OS arithmetic must be available. Only after that actual candidate, a separately labelled test-only numerical view must produce available process and OS arithmetic with matched native intervals. Missing counters or interval mismatch fail this case. Raw counter objects/timestamps are unchanged, and the original source evidence remains unqualified. |
| `PreCancelledRequestNeverStartsNativeSamplingAndDoesNotQualifyProviderCancellation` | A request cancelled before capture must start no native child. Process identity and discovery still run; no SQL bracket identity proof is produced. This does not test cancellation while a native provider or helper is active. |

The test-only numerical view changes the declared numerical provenance for the
arithmetic assertion; it is not returned as a newly admitted production source.
Neither the view nor a positive candidate verifies hardware identity or changes
`NativeQualification`/`TargetComparisonAdmitted`. Preserve this distinction in any
qualification report.

xUnit output uses the prefix `PAIRED_CPU_NATIVE_EVIDENCE`, followed by bounded JSON
with the envelope `{schemaVersion: 1, scenario, evidence}`. The scenario records
process-identity and owned before/after SQL-identity comparison booleans, observed discovery
outcomes, candidate/gap reasons, clock/process epochs, raw counter types/statuses/
time bases/decimal-string values, table accounting and explicit absent-admission
results. The separate records `original_staged_arithmetic` and
`test_only_derived_raw_arithmetic` must not be merged into a single production
qualification claim.

The output excludes raw addresses, machine/process/principal names, SIDs,
fingerprint keys, whole source objects and provider exception messages. Failures
use fixed stage/reason records. Keep the complete TRX/xUnit output, source commit
and applicable configuration provenance together. An evidence record can be
emitted before an assertion fails; its existence is not a passing result.

## Required broader lab matrix

This finite scenario does not complete the following gates. Record each as passed,
failed or not exercised, with the actual identity, platform and source versions.

| Gate | Still-required evidence |
|---|---|
| SQL versions and permissions | SQL Server 2019/2022/2025 under the real non-sysadmin monitor identity; explicit denied SQL and Windows counter/process/CIM cases. |
| Routing and locality | Reviewed certificate/routing configurations, multiple instances and ports, IPv4/IPv6, remote targets, unsupported transport and non-local endpoint cases. No DNS, proxy or alias inference. |
| Process and machine continuity | SQL restart, PID reuse/exit, process creation change, PDH name ambiguity, responding-node change, boot change and host-binding/revision change. |
| Native interval and arithmetic | Actual supported raw types/time bases/anchors, idle and controlled load, multicore process use, localization and counter/provider failures. Missing process counters remain unavailable. |
| Topology | All processor groups, more than 64 logical processors, count changes and unavailable topology. |
| Ownership and failure | Cancellation at each stage, original-deadline expiry, provider stalls, byte/row caps, helper exit and unproven termination/cleanup without overlapping native work. |
| Product admission and history | Reviewed admission policy, canonical persistence, retained-window/epoch/expiry semantics, typed API/MCP exposure and dashboard interactions/accessibility. These are not activated by this harness. |

Local ABI probes, managed fixtures and a successful co-located capture each prove
only their recorded scope. Do not describe them as remote SQL qualification,
fleet coverage, hardware verification, historical continuity, least-privilege
certification for every platform or completed dashboard CPU support.

See [the staged source overview](README.md) for the current implementation
boundaries and primary API documentation.


## Recorded native components through the Resources page

`CpuComponentsNativeQualificationTests` emits a private `CPU_COMPONENT_NATIVE_RECEIPT_V1:` envelope only after its actual source assertions pass. Retain its complete successful TRX and the originating runner's exact-case, input-hash and identity evidence. The envelope is test evidence containing raw counters; it is not the public history representation.

The opt-in `CpuComponentsNativeReceiptHttpTests` requires the following environment inputs:

- `SQLOBSERVER_CPU_NATIVE_RECEIPT_TRX`: absolute path to that reviewed TRX.
- `SQLOBSERVER_CPU_NATIVE_RECEIPT_SHA256`: its independently recorded SHA-256.
- `SQLOBSERVER_CPU_NATIVE_WEB_ROOT`: absolute path to current verified `web/dist` assets.
- `SQLOBSERVER_CPU_NATIVE_BROWSER_SCRIPT`: absolute path to `web/tools/cpu-native-receipt-acceptance.mjs`.
- Optional `SQLOBSERVER_CPU_NATIVE_NODE`: the trusted Node executable path.

With the established disposable PostgreSQL 18.4 fixture and installed browser dependencies, select it explicitly:

```powershell
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj -c Release --filter FullyQualifiedName~CpuComponentsNativeReceiptHttpTests
```

The test rejects missing, failed, ambiguous or hash-mismatched native evidence. It preserves source identity, raw counters, source timestamps and accounting; repository scheduling and completion are fresh replay bookkeeping. A loopback production HTTP server uses test authentication and the compiled Resources page, without API mocks. It checks canonical replay, exact/list equality, private evidence redaction, cross-target denial, responsive rendering, URL restoration and scoped accessibility. It does not certify continuously deployed collection, durable Windows process rights, Windows HTTP authentication, or every supported SQL version.

`ServerLifecycleNativeQualificationTests` is a separate explicit native category. Its three cases qualify actual stable source captures, observed coverage and pre-cancellation. They do not enable lifecycle collection, create canonical lifecycle receipts, set source consistency true, or prove physical failover/restart transitions. Ordinary local/CI sweeps exclude these lab-only categories rather than silently substituting fixtures.

## Lifecycle recorded-native replay and Agent readiness

The stable lifecycle case exports exactly two private snapshots under `SERVER_LIFECYCLE_NATIVE_RECEIPTS_V1:` after its assertions pass. The opt-in `ServerLifecycleNativeReceiptTests` consumes the successful TRX using `SQLOBSERVER_LIFECYCLE_NATIVE_RECEIPT_TRX` and `SQLOBSERVER_LIFECYCLE_NATIVE_RECEIPT_SHA256`. Select `FullyQualifiedName~ServerLifecycleNativeReceiptTests` explicitly. Ordinary validation excludes `RequiresServerLifecycleNativeReceipt`.

The replay uses the real default-off lifecycle catalog and production administrator opt-in inside a disposable PostgreSQL 18.4 database. It uses the installed role grants without temporary writer grants or a fabricated contract. Both original snapshots, identities, source clocks and accounting pass through production lease/start/commit and idempotent replay. It verifies sealed receipts, the initial-to-comparable predecessor chain, checkpoint and scoped history. Fresh repository times and zero replay duration are ingestion bookkeeping. Stable snapshots yield zero inferred events; source consistency and physical failover remain unqualified. Work is constructed from the exact installed schedule; this replay does not claim a live `engine.core` prerequisite observation. Separate admission tests prove scheduler eligibility using explicitly synthetic canonical engine fixtures.

Run the replay promptly after native capture: its comparable-chain assertion requires the original source clocks to remain within the writer's five-minute boundary. Older evidence correctly produces `source_clock_boundary`; do not rewrite timestamps or loosen the boundary to make it comparable. Keep failed attempts and their original evidence alongside a fresh successful capture/replay.

`SqlAgentActivityNativeReadinessTests.ActualMonitorReportsBoundedAgentReadinessWithoutAdmission` is a separate one-case probe tagged `RequiresSqlAgentActivityReadiness`. It uses production discovery unchanged and an actual-monitor, integrated encrypted SQL connection with before/after session attestation. Three permission probes and three bounded reads distinguish missing discovery evidence, denied permission and returned rows. Each command has a five-second timeout within a thirty-second total budget. A passing readiness test means diagnostics completed; it does not establish complete job visibility, service/session association, elapsed duration, running jobs or production admission. It changes no grants, jobs or services. Ordinary SQL validation excludes this explicit category.

The 2026-10-05 four-case SQL-only run passed under the monitor account with no process-DACL mutation. Its Agent evidence found no discovery entries for the three required permissions, reported false permission predicates for `VIEW SERVER SECURITY STATE` and `msdb.syssessions` SELECT, and allowed `msdb.sysjobactivity` SELECT. The actual session-table read failed with error 229. The bounded activity read returned 47 rows; the service read returned zero matching rows. This is an unavailable/ambiguous service observation, not evidence that Agent is stopped. See [current evidence](../../../artifacts/dashboard-lifecycle-agent-2026-10-05/completion.json).

The subsequent [service-predicate qualification](../../../artifacts/dashboard-agent-read-path-2026-10-05/completion.json) clarifies that the server-security permission predicate is false while actual DMV reads succeed. Session SELECT still fails with error 229. A bounded filename-shape probe reports two quoted executable paths with arguments, one Agent token and zero original strict matches; it exports no paths or arguments. V2 capture assets now expand the same pinned service projection twice, preserving the three-result schema 1 contract and original v1 bytes. Quoted paths accept opaque arguments after space/tab; malformed quotes, false suffixes and ambiguous unquoted whitespace are unavailable.

The readiness case executes 20 read-only parameterized VALUES cases using that exact projection, then reads its bounded actual service output. The qualified SQL17 monitor run selects one Agent service with stopped status. A separate administrative SCM snapshot also observes stopped state; these distinct observations do not establish complete visibility, service/session association, full-capture qualification or current-job elapsed time. Commands retain five-second timeouts under the original thirty-second diagnostic budget and before/after actual-session attestation. The [signed-module package](../../provisioning/sql-agent-activity/README.md) retains its original offline preview. The separate [finite trial library](../../provisioning/sql-agent-activity/trial/README.md) now implements transactional installation and guarded removal/recovery. Native signing and cleanup are not established by managed tests. Runtime admission remains unchanged.

## Finite signed-module native trial checkpoint — 2026-10-05

The [finite trial evidence](../../../artifacts/dashboard-agent-module-trial-2026-10-05/completion.json) records exactly three passing `SqlAgentActivitySignedModuleQualificationTests` cases and zero skips under the actual collector Windows identity. This explicit `RequiresSqlAgentActivitySignedModule` category is excluded from ordinary SQL sweeps. The reviewed coordinator supplies separate DBA definition/signature verification, temporary installation and verified normal removal; the monitor runs bounded reads over an owned integrated TLS session.

The SQL17 run parsed 12 rows through the unchanged production three-result parser. Direct syssessions SELECT still failed with 229; sysjobactivity was already directly readable. Invalid NULL/512/514 bounds failed with 50001. Pre-cancellation qualifies generic source opening, not a production module adapter. Agent remained stopped, so ServiceNotRunning and Unknown candidates preserve the absence of elapsed/long-running evidence.

The package's 2,536 source inputs and four exact-set runner inputs were unchanged before/after. The coordinator verified restoration of the original baseline, including removal of the temporary master user. Its protected journal remains on the guest; local evidence includes the returned cleanup result and reviewed implementation, not a copied journal. The temporary service was deleted and the final snapshot found no matching qualification processes/services; universal historical descendant cleanup is not claimed. Earlier failed attempts and their cleanup results are retained.

Full foreign-owner visibility, 512+lookahead under load, running/queued jobs, service/session association, restart boundaries, in-flight cancellation, explicit DENY/signature negatives, crash recovery and production adapter/admission remain separate gates. Do not start Agent or existing jobs to manufacture that evidence. The persistent CPU helper still awaits its previously requested approval.