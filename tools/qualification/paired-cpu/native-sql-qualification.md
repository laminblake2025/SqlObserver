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
| `SQLOBSERVER_CPU_EXPECTED_WINDOWS_SID` | Independently provisioned SID of the intended monitoring Windows identity. The harness compares it with the actual process identity and SQL preflight identity evidence. |

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

The harness performs a separate SQL monitor preflight through the production
connection factory. It compares the expected Windows SID with the actual process
identity and compares SQL current/original login SIDs, recording comparison
booleans rather than raw principal identities. All eight SQL preflight checks
must pass: current SID matched, original SID matched, non-sysadmin context,
integrated authentication, TCP transport, encrypted transport, effective
`CONTROL SERVER` absent and effective `ALTER ANY LOGIN` absent. The last two
checks use `HAS_PERMS_BY_NAME`; the harness does not remove grants or bypass a
failed permission check. SQL's `SUSER_SID()` refers to the
current context; `SUSER_SID(ORIGINAL_LOGIN())` checks the original login lookup.
Lookup or collation failure remains a gap, not a reason to disable validation.
See [Microsoft's SUSER_SID documentation](https://learn.microsoft.com/en-us/sql/t-sql/functions/suser-sid-transact-sql).

This separate preflight does not attest the SID on the connection subsequently
owned by the SQL bracket. The two effective-permission exclusions narrow the
permitted monitoring context; they are not a complete grants or least-privilege
audit. Preserve that distinction when reporting the result.

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
These are capture budgets, not a 30-second limit for discovery, the separate
identity preflight and the entire test invocation together.

## Scenario assertions and recorded evidence

The native class contains three separately reported cases:

| Case | Required result; limited scope |
|---|---|
| `ActualSqlAndLocalNativeSocketProcessEvidenceProduceOnlyACandidate` | Actual complete captures and a positive `CandidateLocalAssociation` are required. A missing/ambiguous/denied native association fails this case. It does not require usable PDH process arithmetic and does not bless it. |
| `ActualCandidateRawCountersHaveIndependentArithmeticEligibilityWithoutAdmission` | A positive candidate is required again. The original staged evidence keeps process arithmetic unavailable with `SourceQualificationAbsent`; independent OS arithmetic must be available. Only after that actual candidate, a separately labelled test-only numerical view must produce available process and OS arithmetic with matched native intervals. Missing counters or interval mismatch fail this case. Raw counter objects/timestamps are unchanged, and the original source evidence remains unqualified. |
| `PreCancelledRequestNeverStartsNativeSamplingAndDoesNotQualifyProviderCancellation` | A request cancelled before capture must start no native child. Discovery and separate identity preflight still run. This does not test cancellation while a native provider or helper is active. |

The test-only numerical view changes the declared numerical provenance for the
arithmetic assertion; it is not returned as a newly admitted production source.
Neither the view nor a positive candidate verifies hardware identity or changes
`NativeQualification`/`TargetComparisonAdmitted`. Preserve this distinction in any
qualification report.

xUnit output uses the prefix `PAIRED_CPU_NATIVE_EVIDENCE`, followed by bounded JSON
with the envelope `{schemaVersion: 1, scenario, evidence}`. The scenario records
process-identity and separate SQL-identity comparison booleans, observed discovery
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
