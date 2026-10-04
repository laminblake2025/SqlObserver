using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Explicit, read-only native qualification under the configured Windows
/// monitor token. A successful test records a candidate, never production admission.
/// No account, certificate, permission, workload or service state is modified.</summary>
public sealed class PairedCpuNativeQualificationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresPairedCpuQualification")]
    public async Task ActualSqlAndLocalNativeSocketProcessEvidenceProduceOnlyACandidate()
    {
        var context = await PrepareAsync();
        var captured = await CaptureAsync(context);
        EmitCapture("candidate_association", context, captured);
        AssertCandidate(context, captured);
        // Association is independent of whether the current PDH process arrays can
        // produce unambiguous counter evidence. This case does not bless arithmetic.
        Assert.False(captured.TargetComparisonAdmitted);
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresPairedCpuQualification")]
    public async Task ActualCandidateRawCountersHaveIndependentArithmeticEligibilityWithoutAdmission()
    {
        var context = await PrepareAsync();
        var captured = await CaptureAsync(context);
        EmitCapture("candidate_raw_arithmetic", context, captured);
        AssertCandidate(context, captured);
        var native = captured.NativeEvidence;
        PairedCpuEndpoint before = native.Cpu!.Before!, after = native.Cpu.After!;
        PairedCpuComparison original = PairedCpuQualifier.Qualify(before, after, TimeSpan.FromMilliseconds(250));
        Emit("original_staged_arithmetic", new
        {
            reason = original.Reason.ToString(), processReason = original.SqlProcess.Reason.ToString(),
            processAvailable = original.SqlProcess.Available, osAvailable = original.OperatingSystem.Available,
            nativeQualification = original.NativeQualification.ToString(), original.TargetComparisonAdmitted,
        });
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, before.Binding.SqlProcessWitness);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, after.Binding.SqlProcessWitness);
        Assert.False(original.SqlProcess.Available);
        Assert.Equal(PairedCpuGapReason.SourceQualificationAbsent, original.SqlProcess.Reason);
        Assert.False(original.TargetComparisonAdmitted);
        Assert.Equal(PairedCpuNativeQualification.Absent, original.NativeQualification);
        Assert.True(original.OperatingSystem.Available,
            "Actual independent OS counter arithmetic is unavailable: " + original.OperatingSystem.Reason);

        // Test-only arithmetic view, AFTER an actual native candidate proof. It
        // changes only the declared numerical provenance, never the raw counters or
        // production evidence. Neither this view nor its result admits a source.
        PairedCpuEndpoint arithmeticBefore = TestOnlyNumericalViewAfterActualCandidate(before);
        PairedCpuEndpoint arithmeticAfter = TestOnlyNumericalViewAfterActualCandidate(after);
        Assert.Same(before.ProcessCounter, arithmeticBefore.ProcessCounter); Assert.Same(before.OsCounter, arithmeticBefore.OsCounter);
        Assert.Same(after.ProcessCounter, arithmeticAfter.ProcessCounter); Assert.Same(after.OsCounter, arithmeticAfter.OsCounter);
        Assert.Equal(before.QueryId, arithmeticBefore.QueryId); Assert.Equal(after.QueryId, arithmeticAfter.QueryId);
        Assert.Equal(before.MonotonicTicks, arithmeticBefore.MonotonicTicks); Assert.Equal(after.MonotonicTicks, arithmeticAfter.MonotonicTicks);
        Assert.Equal(before.ObservedAtUtc, arithmeticBefore.ObservedAtUtc); Assert.Equal(after.ObservedAtUtc, arithmeticAfter.ObservedAtUtc);
        PairedCpuComparison arithmetic = PairedCpuQualifier.Qualify(arithmeticBefore, arithmeticAfter, TimeSpan.FromMilliseconds(250));
        Emit("test_only_derived_raw_arithmetic", new
        {
            arithmetic.NumericallyComparable, reason = arithmetic.Reason.ToString(),
            sqlProcess = Component(arithmetic.SqlProcess), operatingSystem = Component(arithmetic.OperatingSystem),
            nativeQualification = arithmetic.NativeQualification.ToString(), arithmetic.TargetComparisonAdmitted,
            arithmetic.HardwareIdentityVerified, rawCounterObjectsUnchanged = true,
        });
        Assert.True(arithmetic.SqlProcess.Available, "Actual process counter arithmetic is unavailable: " + arithmetic.SqlProcess.Reason);
        Assert.True(arithmetic.OperatingSystem.Available, "Actual OS counter arithmetic is unavailable: " + arithmetic.OperatingSystem.Reason);
        Assert.True(arithmetic.NumericallyComparable, "Actual per-counter native intervals do not form a comparable pair: " + arithmetic.Reason);
        Assert.InRange(arithmetic.SqlProcess.Percent!.Value, 0m, 100m);
        Assert.InRange(arithmetic.OperatingSystem.Percent!.Value, 0m, 100m);
        Assert.Equal(arithmetic.SqlProcess.FromNative100ns, arithmetic.OperatingSystem.FromNative100ns);
        Assert.Equal(arithmetic.SqlProcess.ToNative100ns, arithmetic.OperatingSystem.ToNative100ns);
        Assert.Equal(PairedCpuNativeQualification.Absent, arithmetic.NativeQualification);
        Assert.False(arithmetic.TargetComparisonAdmitted); Assert.False(arithmetic.HardwareIdentityVerified);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, before.Binding.SqlProcessWitness);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, after.Binding.SqlProcessWitness);
        Assert.True(string.Equals(native.Cpu.ProcessNameBefore, "sqlservr.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(native.Cpu.ProcessNameAfter, "sqlservr.exe", StringComparison.OrdinalIgnoreCase),
            "The observed process-image basename is not SQL Server; private source names are withheld.");
        Assert.True(native.Cpu.ProcessExecutableFingerprintBefore is not null &&
            native.Cpu.ProcessExecutableFingerprintBefore == native.Cpu.ProcessExecutableFingerprintAfter,
            "The independently observed executable identity changed or is unavailable.");
        Assert.NotNull(native.Cpu.IdProcessBefore); Assert.NotNull(native.Cpu.IdProcessAfter);
        Assert.Equal(captured.Before.Engine.ProcessId!.Value, native.Cpu.IdProcessBefore.FirstValue);
        Assert.Equal(captured.After.Engine.ProcessId!.Value, native.Cpu.IdProcessAfter.FirstValue);
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresPairedCpuQualification")]
    public async Task PreCancelledRequestNeverStartsNativeSamplingAndDoesNotQualifyProviderCancellation()
    {
        var context = await PrepareAsync();
        var reader = new SqlServerCpuTargetWitnessReader(context.Contract.Key);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        int started = 0;
        byte[] sidBytes = MonitorSidBytes(context.Contract);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await reader.ReadAttestedBracketAsync(context.Contract.ExecutionRequest(context.Profile), sidBytes, (witness, token) =>
                {
                    started++; return context.Native.StartOperation(context.Contract.Host, witness, token);
                }, cancellation.Token));
        }
        finally { CryptographicOperations.ZeroMemory(sidBytes); }
        Assert.Equal(0, started);
        Emit("pre_cancelled_request", new { nativeStarterCalls = started, requestCancelled = true,
            qualifiesNativeProviderCancellation = false, targetComparisonAdmitted = false });
    }

    private async Task<Context> PrepareAsync()
    {
        PairedCpuQualificationLabContract contract = PairedCpuQualificationLabContract.Load(Environment.GetEnvironmentVariable);
        if (!OperatingSystem.IsWindows()) throw new XunitException("Native paired CPU qualification requires a co-located Windows SQL Server host; no source was opened.");
        bool processIdentityMatched;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            processIdentityMatched = string.Equals(identity.User?.Value, contract.ExpectedWindowsSid, StringComparison.Ordinal);
        Emit("process_identity_preflight", new { identityMatched = processIdentityMatched });
        Assert.True(processIdentityMatched, "The current Windows token is not the independently configured monitoring identity; source access was not attempted.");

        CapabilityProfile profile;
        try { profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(contract.DiscoveryRequest(), CancellationToken.None); }
        catch (Exception failure) { throw SafeFailure("production_capability_discovery", failure); }
        Emit("production_discovery", new { outcome = profile.Outcome.ToString(), reason = profile.Reason.ToString(),
            encrypted = profile.TransportEncrypted, nonSysadmin = !profile.IsSysAdmin, authentication = profile.AuthenticationScheme.ToString(),
            observedSqlMajor = profile.ServerIdentity?.Version.Major });
        Assert.Equal(contract.TargetId, profile.TargetId); Assert.Equal(contract.TargetRevision, profile.TargetRevision);
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded,
            "Actual production discovery refused this source. Unrelated optional permission degradation does not replace the exact CPU requirements.");
        Assert.NotNull(profile.ServerIdentity);
        Assert.Equal(contract.ExpectedSqlMajor, profile.ServerIdentity.Version.Major);
        Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.True(profile.TransportEncrypted, "The production discovery did not observe encrypted SQL transport.");
        Assert.False(profile.IsSysAdmin, "Native qualification refuses a sysadmin SQL monitoring context.");
        Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm,
            "The actual SQL monitoring context did not use Windows integrated authentication.");
        string required = contract.ExpectedSqlMajor == 15 ? "server.view-state" : "server.view-performance-state";
        Assert.True(profile.Permissions.Any(item => item.PermissionId.Value == required &&
            item.Scope == PermissionEvidenceScope.Server && item.Outcome == PermissionEvidenceOutcome.Granted),
            "Actual discovery did not grant the version-specific CPU witness state permission.");
        return new(contract, profile, new WindowsCpuAssociationSamplingReader(
            new WindowsCpuTcpAssociationReader(contract.Key), new WindowsTargetCpuPairReader(contract.Key)));
    }

    private async Task<SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult>> CaptureAsync(Context context)
    {
        byte[] sidBytes = MonitorSidBytes(context.Contract);
        try
        {
            CollectorExecutionRequest request = context.Contract.ExecutionRequest(context.Profile);
            var captured = await new SqlServerCpuTargetWitnessReader(context.Contract.Key).ReadAttestedBracketAsync(
                request, sidBytes, (witness, token) =>
                    context.Native.StartOperation(context.Contract.Host, witness, token), CancellationToken.None);
            foreach (var identity in new[] { captured.BeforeIdentity, captured.AfterIdentity })
            {
                Assert.Equal(request.TargetId, identity.TargetId); Assert.Equal(request.TargetRevision, identity.TargetRevision);
                Assert.Equal(request.RunId, identity.RunId);
                Assert.Equal(captured.Bracket.Before.ConnectionSessionId, identity.ConnectionSessionId);
                Assert.Equal(captured.Bracket.Before.ClientConnectionId, identity.ClientConnectionId);
                Assert.Equal(captured.Bracket.Before.TcpConnection!.ServerConnectionId, identity.ServerConnectionId);
                Assert.Equal(captured.Bracket.Before.TcpConnection.ServerSessionId, identity.ServerSessionId);
                Assert.True(identity.CurrentIdentityMatched && identity.OriginalIdentityMatched && identity.NonSysadmin &&
                    identity.Integrated && identity.Tcp && identity.Encrypted && identity.NoControlServer && identity.NoAlterAnyLogin,
                    "The owned bracket did not attest the configured monitor identity and bounded security context; private identities are withheld.");
            }
            Assert.Equal(1, captured.BeforeIdentity.Ordinal); Assert.Equal(2, captured.AfterIdentity.Ordinal);
            Assert.True(captured.BeforeIdentity.ObservedAtUtc <= captured.Bracket.Before.SourceObservedAtUtc);
            Assert.True(captured.AfterIdentity.ObservedAtUtc >= captured.Bracket.After.SourceObservedAtUtc);
            Emit("owned_sql_identity_attestation", new
            {
                attestsOwnedBracketSessionIdentity = true, bothContextsMatchedRequest = true,
                beforeObservedUtc = captured.BeforeIdentity.ObservedAtUtc, afterObservedUtc = captured.AfterIdentity.ObservedAtUtc,
                beforeOrdinal = captured.BeforeIdentity.Ordinal, afterOrdinal = captured.AfterIdentity.Ordinal,
                currentIdentityMatched = captured.BeforeIdentity.CurrentIdentityMatched && captured.AfterIdentity.CurrentIdentityMatched,
                originalIdentityMatched = captured.BeforeIdentity.OriginalIdentityMatched && captured.AfterIdentity.OriginalIdentityMatched,
                nonSysadmin = captured.BeforeIdentity.NonSysadmin && captured.AfterIdentity.NonSysadmin,
                integrated = captured.BeforeIdentity.Integrated && captured.AfterIdentity.Integrated,
                tcp = captured.BeforeIdentity.Tcp && captured.AfterIdentity.Tcp,
                encrypted = captured.BeforeIdentity.Encrypted && captured.AfterIdentity.Encrypted,
                noControlServer = captured.BeforeIdentity.NoControlServer && captured.AfterIdentity.NoControlServer,
                noAlterAnyLogin = captured.BeforeIdentity.NoAlterAnyLogin && captured.AfterIdentity.NoAlterAnyLogin,
                completeLeastPrivilegeAudit = false, targetComparisonAdmitted = false,
            });
            return captured.Bracket;
        }
        catch (XunitException) { throw; }
        catch (Exception failure) { throw SafeFailure("owned_sql_tcp_cpu_tcp_attested_bracket", failure); }
        finally { CryptographicOperations.ZeroMemory(sidBytes); }
    }

    private static byte[] MonitorSidBytes(PairedCpuQualificationLabContract contract)
    {
        if (!OperatingSystem.IsWindows()) throw new XunitException("SQL monitor attestation requires the Windows monitoring token.");
        try
        {
            var sid = new SecurityIdentifier(contract.ExpectedWindowsSid);
            var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0); return bytes;
        }
        catch (ArgumentException)
        { throw new XunitException("The configured monitor SID is invalid; private identity is withheld."); }
    }

    private static void AssertCandidate(Context context, SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult> capture)
    {
        Assert.Equal(SqlCpuWitnessGap.None, capture.ContinuityGap);
        Assert.Equal(context.Contract.TargetId, capture.Before.TargetId); Assert.Equal(capture.Before.TargetId, capture.After.TargetId);
        Assert.Equal(context.Contract.TargetRevision, capture.Before.TargetRevision); Assert.Equal(capture.Before.TargetRevision, capture.After.TargetRevision);
        Assert.Equal(capture.Before.RunId, capture.After.RunId); Assert.Equal(2, capture.Before.SourceSchemaVersion); Assert.Equal(2, capture.After.SourceSchemaVersion);
        Assert.Equal(SqlCpuPhysicalTransport.Tcp, capture.Before.PhysicalTransport); Assert.Equal(SqlCpuPhysicalTransport.Tcp, capture.After.PhysicalTransport);
        Assert.NotNull(capture.Before.TcpConnection); Assert.NotNull(capture.After.TcpConnection);
        Assert.Equal(capture.Before.TcpConnection.ServerConnectionId, capture.After.TcpConnection.ServerConnectionId);
        Assert.Equal(capture.Before.TcpConnection.ServerSessionId, capture.After.TcpConnection.ServerSessionId);
        Assert.Equal(capture.Before.ConnectionSessionId, capture.After.ConnectionSessionId);
        Assert.Equal(capture.Before.ClientConnectionId, capture.After.ClientConnectionId);
        Assert.Equal(Environment.ProcessId, capture.Before.TcpConnection.CollectorProcessId);
        Assert.Equal(Environment.ProcessId, capture.After.TcpConnection.CollectorProcessId);
        Assert.True(capture.NativeEvidence.Complete, "Native captures are incomplete; no replacement evidence is accepted: " + SafeToken(capture.NativeEvidence.Reason));
        CpuTcpAssociation association = capture.NativeEvidence.Qualify(capture.Before, capture.After);
        Assert.True(association.CandidateLocalAssociation, "Actual native SQL/process/socket association is unavailable: " + association.Reason);
        Assert.Equal(PairedCpuNativeQualification.Absent, association.NativeQualification);
        Assert.False(association.TargetComparisonAdmitted); Assert.False(association.HardwareIdentityVerified);
        Assert.False(capture.Before.LocalWindowsProcessAssociationQualified); Assert.False(capture.After.LocalWindowsProcessAssociationQualified);
        Assert.False(capture.NativeEvidence.TargetComparisonAdmitted); Assert.Equal(PairedCpuNativeQualification.Absent, capture.NativeEvidence.NativeQualification);
        Assert.Equal(PairedCpuNativeQualification.Absent, capture.NativeEvidence.Cpu!.NativeQualification);
        Assert.False(capture.NativeEvidence.Cpu.TargetComparisonAdmitted);
        Assert.Equal(CpuTcpProcessEpoch.StillRunningWaitResult, capture.NativeEvidence.BeforeTcp!.Frame!.SqlProcess.WaitResultAfter);
        Assert.Equal(CpuTcpProcessEpoch.StillRunningWaitResult, capture.NativeEvidence.AfterTcp!.Frame!.SqlProcess.WaitResultAfter);
        Assert.Equal(CpuTcpProcessEpoch.StillRunningWaitResult, capture.NativeEvidence.BeforeTcp.Frame.CollectorProcess.WaitResultAfter);
        Assert.Equal(CpuTcpProcessEpoch.StillRunningWaitResult, capture.NativeEvidence.AfterTcp.Frame.CollectorProcess.WaitResultAfter);
    }

    private void EmitCapture(string scenario, Context context, SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult> capture)
    {
        var native = capture.NativeEvidence;
        var association = native.Qualify(capture.Before, capture.After);
        Emit(scenario, new
        {
            targetRevision = context.Contract.TargetRevision.Value, runId = capture.Before.RunId.Value,
            sqlSchemaVersion = capture.Before.SourceSchemaVersion, observedSqlMajor = context.Profile.ServerIdentity!.Version.Major,
            sqlBeforeUtc = capture.Before.SourceObservedAtUtc, sqlAfterUtc = capture.After.SourceObservedAtUtc,
            continuityReason = capture.ContinuityGap.ToString(), associationReason = association.Reason.ToString(), association.CandidateLocalAssociation,
            nativeComplete = native.Complete, nativeReason = SafeToken(native.Reason),
            tcpBefore = Frame(native.BeforeTcp?.Frame), tcpAfter = Frame(native.AfterTcp?.Frame),
            cpuBefore = Endpoint(native.Cpu?.Before), cpuAfter = Endpoint(native.Cpu?.After),
            processStatusBefore = native.Cpu?.ProcessStatusBefore, processStatusAfter = native.Cpu?.ProcessStatusAfter,
            osStatusBefore = native.Cpu?.OsStatusBefore, osStatusAfter = native.Cpu?.OsStatusAfter,
            processMappingAmbiguousBefore = native.Cpu?.BeforeWitness?.ProcessMappingAmbiguous,
            processMappingAmbiguousAfter = native.Cpu?.AfterWitness?.ProcessMappingAmbiguous,
            processReason = SafeToken(native.Cpu?.ProcessReason), nativeQualification = association.NativeQualification.ToString(),
            association.TargetComparisonAdmitted, association.HardwareIdentityVerified,
        });
    }

    private static PairedCpuEndpoint TestOnlyNumericalViewAfterActualCandidate(PairedCpuEndpoint original) => new(
        new PairedCpuBinding(original.Binding.TargetId, original.Binding.TargetRevision, original.Binding.RunId,
            original.Binding.ExpectedHostFingerprint, original.Binding.SqlProcessId, PairedCpuSqlProcessWitness.TargetEngineProcessId),
        original.QueryId, original.ObservedAtUtc, original.MonotonicTicks, original.MonotonicFrequency, original.Host,
        original.SqlProcessCreationFileTime100ns, original.LogicalProcessorCount, original.ProcessCounter, original.OsCounter);
    private static object Component(PairedCpuComponent component) => new { component.Available, reason = component.Reason.ToString(),
        percent = component.Percent?.ToString(CultureInfo.InvariantCulture), fromNative100ns = Number(component.FromNative100ns), toNative100ns = Number(component.ToNative100ns) };
    private static object? Endpoint(PairedCpuEndpoint? endpoint) => endpoint is null ? null : new
    {
        endpoint.QueryId, endpoint.ObservedAtUtc, monotonic100ns = Number(endpoint.MonotonicTicks), monotonicFrequency = Number(endpoint.MonotonicFrequency),
        creationFileTime100ns = Number(endpoint.SqlProcessCreationFileTime100ns), endpoint.LogicalProcessorCount,
        process = Counter(endpoint.ProcessCounter), os = Counter(endpoint.OsCounter),
        expectedHostMatched = endpoint.Host.ActualHostFingerprint == endpoint.Binding.ExpectedHostFingerprint,
        boot100ns = Number(endpoint.Host.BootFileTime100ns), nativeHostWitness = endpoint.Host.Witness.ToString(),
    };
    private static object? Frame(CpuTcpNativeFrame? frame) => frame is null ? null : new
    {
        frame.ObservedUtc, monotonic100ns = Number(frame.Mono100ns), frame.Af4Status, frame.Af6Status, frame.RowCount4, frame.RowCount6,
        matchingRows = frame.Rows.Count, sqlCreationBefore100ns = Number(frame.SqlProcess.CreationBefore100ns),
        sqlCreationAfter100ns = Number(frame.SqlProcess.CreationAfter100ns), sqlWaitResult = frame.SqlProcess.WaitResultAfter,
        collectorCreationBefore100ns = Number(frame.CollectorProcess.CreationBefore100ns), collectorCreationAfter100ns = Number(frame.CollectorProcess.CreationAfter100ns),
        collectorWaitResult = frame.CollectorProcess.WaitResultAfter, expectedHostMatched = frame.Host.ActualHostFingerprint == frame.Binding.ExpectedHostFingerprint,
    };
    private static object? Counter(PdhCpuRawCounter? counter) => counter is null ? null : new
    {
        counter.CounterType, counter.CStatus, counter.ApiStatus, counter.MultiCount,
        firstValue = Number(counter.FirstValue), secondValue = Number(counter.SecondValue),
        timeBase = Number(counter.TimeBase), collectedLocalFileTime100ns = Number(counter.CollectedLocalFileTime100ns),
    };
    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static string SafeToken(string? value) => value is { Length: > 0 and <= 80 } && value.All(static c => c is >= 'a' and <= 'z' or '_') ? value : "unclassified_source_result";
    private void Emit(string scenario, object evidence) => output.WriteLine("PAIRED_CPU_NATIVE_EVIDENCE " +
        JsonSerializer.Serialize(new { schemaVersion = 1, scenario, evidence }, JsonOptions));
    private XunitException SafeFailure(string stage, Exception failure)
    {
        string reason = failure switch { SqlException => "sql_source_failure", OperationCanceledException => "owned_deadline_or_cancellation",
            UnauthorizedAccessException => "source_access_denied", CpuNativeTerminationUnprovenException => "native_cleanup_unproven",
            _ => "native_source_or_proof_failure" };
        Emit("safe_failure", new { stage, reason, exceptionDetailsWithheld = true, targetComparisonAdmitted = false });
        return new XunitException("Native paired CPU qualification failed at " + stage + ": " + reason + ". Provider messages and private identities are withheld; this is not source admission.");
    }
    private sealed record Context(PairedCpuQualificationLabContract Contract, CapabilityProfile Profile, WindowsCpuAssociationSamplingReader Native);
}
