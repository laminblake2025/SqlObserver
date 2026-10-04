using System.Text;
using System.Text.Json.Nodes;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class WindowsTargetCpuPairReaderTests
{
    private const string Machine = "11111111-1111-1111-1111-111111111111";
    private static readonly IdentityFingerprintKey Key = new(Enumerable.Repeat((byte)0xA5, 32).ToArray());

    [Fact]
    public async Task ExactRawAnchorsStatusesAndInt64ValuesSurviveWithoutPromotingSqlInstanceAssociation()
    {
        var helper = new Helper(Output()); var reader = new WindowsTargetCpuPairReader(Key, helper);
        WindowsTargetCpuPairRequest request = Request();
        WindowsTargetCpuPairReadResult result = await reader.ReadAsync(request, CancellationToken.None);
        Assert.Equal("partial", result.State); Assert.Equal("source_qualification_absent", result.Reason);
        Assert.Equal(PairedCpuNativeQualification.Absent, result.NativeQualification); Assert.False(result.TargetComparisonAdmitted);
        PairedCpuEndpoint before = Assert.IsType<PairedCpuEndpoint>(result.Before), after = Assert.IsType<PairedCpuEndpoint>(result.After);
        Assert.Equal(request.Binding.RunId, before.Binding.RunId); Assert.Equal(before.QueryId, after.QueryId);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, after.Binding.SqlProcessWitness);
        Assert.Equal(long.MaxValue - 100, before.ProcessCounter!.FirstValue);
        Assert.Equal(long.MaxValue, after.ProcessCounter!.FirstValue);
        Assert.Equal(128U, after.LogicalProcessorCount); Assert.Equal(10_000_000L, after.MonotonicFrequency);
        Assert.Equal(100_000_000L, before.ProcessCounter.SecondValue); Assert.Equal(105_000_000L, after.ProcessCounter.SecondValue);
        Assert.Equal(140_000_000_000_000_001L, before.ProcessCounter.CollectedLocalFileTime100ns);
        Assert.Equal(10_000_000L, after.ProcessCounter.TimeBase);
        Assert.Equal(10L, after.SqlProcessCreationFileTime100ns); Assert.Equal("sqlservr.exe", result.ProcessNameAfter);
        Assert.NotNull(result.ProcessExecutableFingerprintAfter); Assert.NotEqual(after.Host.ActualHostFingerprint, after.Host.MachineFingerprint);
        Assert.Equal(10, helper.Pid); Assert.Equal(1, helper.Calls);
        PairedCpuComparison math = PairedCpuQualifier.Qualify(before, after, TimeSpan.Zero);
        Assert.Equal(80M, math.OperatingSystem.Percent); Assert.Null(math.SqlProcess.Percent);
        Assert.Equal(PairedCpuGapReason.SourceQualificationAbsent, math.SqlProcess.Reason);
        Assert.False(math.TargetComparisonAdmitted);
    }

    [Fact]
    public async Task WrongHostRemovesAllRawTargetEvidenceAndNeverFallsBackToTheCollectorMachine()
    {
        JsonObject json = Output(); json["after"]!["machineUuid"] = "22222222-2222-2222-2222-222222222222";
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("wrong_host", result.Reason); Assert.Null(result.Before); Assert.Null(result.After);
    }

    [Theory]
    [InlineData("idProcess", "firstValue", "11")]
    [InlineData("idProcess", "cStatus", "bad_status")]
    [InlineData("idProcess", "counterType", "bad_type")]
    public async Task BadIdProcessMappingDropsOnlyProcessEvidenceWhilePreservingOperatingSystemRawData(string objectName, string field, string value)
    {
        JsonObject json = Output();
        json["after"]![objectName]![field] = value switch { "bad_status" => JsonValue.Create(0xC0000BC6U), "bad_type" => JsonValue.Create(1U), _ => JsonValue.Create(value) };
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Null(result.After!.ProcessCounter); Assert.NotNull(result.After.OsCounter);
        Assert.NotNull(result.IdProcessAfter); Assert.Equal("process_counter_unavailable", result.ProcessReason);
    }

    [Theory]
    [InlineData("processName", "another.exe")]
    [InlineData("processCreation100ns", null)]
    public async Task MissingOrNonSqlProcessRetainsIndependentOsEvidence(string field, string? value)
    {
        JsonObject json = Output(); json["after"]![field] = value;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Null(result.After!.ProcessCounter); Assert.NotNull(result.After.OsCounter);
    }

    [Fact]
    public async Task PidReuseAndPermissionDeniedRemainExplicitRawBoundaries()
    {
        JsonObject json = Output(); json["after"]!["processCreation100ns"] = "20";
        var reader = new WindowsTargetCpuPairReader(Key, new Helper(json));
        Assert.Equal("process_race", (await reader.ReadAsync(Request(), CancellationToken.None)).ProcessReason);
        json["after"]!["processStatus"] = 5U; json["after"]!["processCreation100ns"] = null;
        var denied = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("permission_denied", denied.ProcessReason); Assert.NotNull(denied.After!.OsCounter);
    }

    [Fact]
    public async Task UnequalCounterIntervalsAndProviderFailuresArePreservedForQualificationRatherThanAligned()
    {
        JsonObject json = Output(); json["after"]!["os"]!["secondValue"] = "104999999";
        json["after"]!["process"]!["apiStatus"] = 5U;
        json["after"]!["process"]!["timeBase"] = "1000";
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal(104_999_999L, result.After!.OsCounter!.SecondValue);
        Assert.Equal(5U, result.After.ProcessCounter!.ApiStatus); Assert.Equal(1000L, result.After.ProcessCounter.TimeBase);
        Assert.Equal(105_000_000L, result.After.ProcessCounter.SecondValue);
    }

    [Theory]
    [InlineData("processCreationAfter100ns", "20")]
    public async Task SameHandlePostReadProcessRaceRemovesProcessCounterAndRetainsExactWitness(string field, string value)
    {
        JsonObject json = Output(); json["after"]![field] = value;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("process_race", result.ProcessReason); Assert.Null(result.After!.ProcessCounter);
        Assert.Null(result.After.SqlProcessCreationFileTime100ns); Assert.NotNull(result.After.OsCounter);
        Assert.NotNull(result.AfterWitness); Assert.Equal(10L, result.AfterWitness.ProcessCreationBefore100ns);
        Assert.Equal(field == "processCreationAfter100ns" ? 20L : 10L, result.AfterWitness.ProcessCreationAfter100ns);
        Assert.Equal(field == "processExitAfter100ns" ? 11L : 0L, result.AfterWitness.ProcessExitAfter100ns);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(0xffffffffU)]
    [InlineData(null)]
    public async Task OnlyExplicitHeldHandleWaitProvesProcessLiveness(uint? waitResult)
    {
        JsonObject json = Output(); json["after"]!["processWaitResultAfter"] = waitResult;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("process_race", result.ProcessReason);
        Assert.Null(result.After!.ProcessCounter); Assert.Null(result.After.SqlProcessCreationFileTime100ns);
        Assert.NotNull(result.After.OsCounter); Assert.Equal(waitResult, result.AfterWitness!.ProcessWaitResultAfter);
    }

    [Theory]
    [InlineData(12345L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public async Task UndefinedRunningExitFileTimeIsRetainedWithoutOverridingExplicitLiveness(long undefinedExit)
    {
        JsonObject json = Output(); json["after"]!["processExitAfter100ns"] = undefinedExit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.NotNull(result.After!.ProcessCounter); Assert.Equal(10L, result.After.SqlProcessCreationFileTime100ns);
        Assert.Equal(undefinedExit, result.AfterWitness!.ProcessExitAfter100ns);
        Assert.Equal(258U, result.AfterWitness.ProcessWaitResultAfter);
        Assert.False(result.TargetComparisonAdmitted);
    }

    [Fact]
    public async Task TopologyChangeWithinAFrameRemovesDenominatorWithoutDiscardingRawCounters()
    {
        JsonObject json = Output(); json["after"]!["logicalProcessorsAfter"] = 129;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Null(result.After!.LogicalProcessorCount); Assert.NotNull(result.After.OsCounter);
        Assert.Equal(128U, result.AfterWitness!.LogicalProcessorsBefore); Assert.Equal(129U, result.AfterWitness.LogicalProcessorsAfter);
        Assert.False(PairedCpuQualifier.Qualify(result.Before, result.After, TimeSpan.Zero).NumericallyComparable);
    }

    [Fact]
    public async Task MissingProcessArrayRetainsIndependentOperatingSystemRawCounters()
    {
        JsonObject json = Output(); json["after"]!["process"] = null; json["after"]!["idProcess"] = null;
        json["after"]!["processStatus"] = 0x800007D5U;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Null(result.After!.ProcessCounter); Assert.NotNull(result.After.OsCounter);
        Assert.Equal(0x800007D5U, result.ProcessStatusAfter); Assert.Equal("process_counter_unavailable", result.ProcessReason);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task AmbiguousLegacyProcessMappingPreservesOsAndActualEnumerationWithoutJoiningByOrdinal(string frame)
    {
        JsonObject json = Output(); json[frame]!["processMappingAmbiguous"] = true;
        json[frame]!["processInstances"] = 1024;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("process_mapping_ambiguous", result.ProcessReason);
        PairedCpuEndpoint selected = frame == "before" ? result.Before! : result.After!;
        WindowsCpuPairFrameWitness witness = frame == "before" ? result.BeforeWitness! : result.AfterWitness!;
        Assert.Null(selected.ProcessCounter); Assert.NotNull(selected.OsCounter);
        Assert.Null(frame == "before" ? result.IdProcessBefore : result.IdProcessAfter);
        Assert.True(witness.ProcessMappingAmbiguous); Assert.Equal(1024, witness.ProcessInstances);
        Assert.Equal(80M, PairedCpuQualifier.Qualify(result.Before, result.After!, TimeSpan.Zero).OperatingSystem.Percent);
        Assert.False(result.TargetComparisonAdmitted); Assert.Equal(PairedCpuNativeQualification.Absent, result.NativeQualification);
    }

    [Theory]
    [InlineData("machineUuid")]
    [InlineData("bootFileTime100ns")]
    public async Task MissingNativeHostIdentityIsAnExplicitGapWithoutInventedWitness(string field)
    {
        JsonObject json = Output(); json["before"]![field] = null;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.False(result.TargetComparisonAdmitted);
        if (field == "machineUuid") { Assert.Equal("host_identity_unavailable", result.Reason); Assert.Null(result.Before); }
        else { Assert.Equal(PairedCpuHostWitness.Unavailable, result.Before!.Host.Witness); Assert.Null(result.Before.Host.BootFileTime100ns); }
    }

    [Theory]
    [InlineData("processInstances", 1025)]
    [InlineData("logicalProcessors", 4097)]
    public async Task NativeEnumerationAndTopologyCapsRefuseOversizedEvidence(string field, int value)
    {
        JsonObject json = Output(); json["before"]![field] = value;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("invalid_source_output", result.Reason); Assert.Null(result.Before);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("9223372036854775808")]
    [InlineData("1.0")]
    public async Task NativeIntegerStringsRejectLossOfPrecisionAndNonCanonicalNumbers(string value)
    {
        JsonObject json = Output(); json["after"]!["process"]!["firstValue"] = value;
        var result = await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("invalid_source_output", result.Reason);
    }

    [Fact]
    public async Task ByteCapAndClosedSchemaRefuseMalformedOutput()
    {
        var reader = new WindowsTargetCpuPairReader(Key, new Helper(new byte[WindowsTargetCpuPairReader.MaximumOutputBytes + 1]));
        Assert.Equal("invalid_source_output", (await reader.ReadAsync(Request(), CancellationToken.None)).Reason);
        JsonObject json = Output(); json["remoteComputer"] = "not-allowed";
        Assert.Equal("invalid_source_output", (await new WindowsTargetCpuPairReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None)).Reason);
        byte[] duplicate = Encoding.UTF8.GetBytes(Output().ToJsonString().Replace("\"schema\":2", "\"schema\":2,\"schema\":2", StringComparison.Ordinal));
        Assert.Equal("invalid_source_output", (await new WindowsTargetCpuPairReader(Key, new Helper(duplicate)).ReadAsync(Request(), CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task ForeignBindingAndDeniedHostNeverStartTheNativeHelper()
    {
        var helper = new Helper(Output()); var reader = new WindowsTargetCpuPairReader(Key, helper);
        WindowsTargetCpuPairRequest request = Request();
        var foreign = new PairedCpuBinding(new MonitoredInstanceId(Guid.NewGuid()), request.Binding.TargetRevision, request.Binding.RunId, request.Binding.ExpectedHostFingerprint, 10);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(request with { Binding = foreign }, CancellationToken.None).AsTask());
        Assert.Equal("host_binding_unavailable", (await reader.ReadAsync(Request(HostBindingState.Denied), CancellationToken.None)).Reason);
        Assert.Equal(0, helper.Calls);
    }

    [Fact]
    public void SqlOwnedRequestFactoryPreservesExactRunPidAndBindingWithoutAdmission()
    {
        WindowsTargetCpuPairRequest original = Request(); SqlCpuTargetWitness witness = SqlWitness(original, 77);
        WindowsTargetCpuPairRequest request = WindowsTargetCpuPairRequest.FromSqlWitness(original.Target, witness);
        Assert.Equal(witness.RunId, request.Binding.RunId); Assert.Equal(77, request.Binding.SqlProcessId);
        Assert.Equal(original.Target.Binding.HostFingerprint.Value, request.Binding.ExpectedHostFingerprint);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, request.Binding.SqlProcessWitness);
        WindowsTargetCpuPairRequest missing = WindowsTargetCpuPairRequest.FromSqlWitness(original.Target, SqlWitness(original, null));
        Assert.Null(missing.Binding.SqlProcessId); Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, missing.Binding.SqlProcessWitness);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SqlOwnedRequestFactoryRejectsForeignTargetOrRevision(bool targetChanged)
    {
        WindowsTargetCpuPairRequest original = Request(); SqlCpuTargetWitness witness = SqlWitness(original, 10, targetChanged);
        Assert.Throws<ArgumentException>(() => WindowsTargetCpuPairRequest.FromSqlWitness(original.Target, witness));
    }

    [Fact]
    public async Task CallerCancellationReachesTheOwnedHelperWithoutReturningAReceivedPair()
    {
        var helper = new HeldHelper(); var reader = new WindowsTargetCpuPairReader(Key, helper);
        using var cancel = new CancellationTokenSource();
        Task<WindowsTargetCpuPairReadResult> read = reader.ReadAsync(Request(), cancel.Token).AsTask();
        await helper.Started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await helper.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UnprovenTerminationQuarantinesTargetAndCannotStartOverlappingNativeWork()
    {
        var helper = new IgnoringHelper(); var reader = new WindowsTargetCpuPairReader(Key, helper);
        WindowsTargetCpuPairRequest request = Request();
        ICpuNativeSamplingOperation<WindowsTargetCpuPairReadResult> operation = reader.StartOperation(request, CancellationToken.None);
        await helper.Started.Task;
        Assert.Throws<InvalidOperationException>(() => reader.StartOperation(request, CancellationToken.None));
        Assert.False(await operation.TerminateAsync(TimeSpan.FromMilliseconds(10)));
        Assert.Throws<CpuNativeTerminationUnprovenException>(() => reader.StartOperation(request, CancellationToken.None));
        Assert.Equal(1, helper.Calls); Assert.False(operation.Completion.IsCompleted);
        helper.Release.SetResult(Encoding.UTF8.GetBytes(Output().ToJsonString()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
        Assert.True(await operation.TerminateAsync(TimeSpan.FromMilliseconds(10)));
        await operation.DisposeAsync();
        await using var recovered = reader.StartOperation(request, CancellationToken.None);
        WindowsTargetCpuPairReadResult fresh = await recovered.Completion;
        Assert.Equal("partial", fresh.State); Assert.Equal(2, helper.Calls);
    }

    [Fact]
    public async Task ConfirmedCancellationReleasesOwnershipOnlyAfterHelperCleanupCompletes()
    {
        var helper = new HeldHelper(); var reader = new WindowsTargetCpuPairReader(Key, helper);
        WindowsTargetCpuPairRequest request = Request();
        await using var operation = reader.StartOperation(request, CancellationToken.None);
        await helper.Started.Task;
        Assert.True(await operation.TerminateAsync(TimeSpan.FromSeconds(1)));
        Assert.True(helper.Cancelled.Task.IsCompleted); Assert.True(operation.Completion.IsCompleted);
        await using var next = reader.StartOperation(request, CancellationToken.None);
        Assert.Equal(2, helper.Calls);
        Assert.True(await next.TerminateAsync(TimeSpan.FromSeconds(1)));
    }

    private sealed class Helper : IWindowsCpuPairHelper
    {
        private readonly byte[] bytes;
        internal int Pid { get; private set; }
        internal int Calls { get; private set; }
        internal Helper(JsonObject json) => bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        internal Helper(byte[] output) => bytes = output;
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(int processId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Pid = processId; Calls++; return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes); }
    }
    private sealed class HeldHelper : IWindowsCpuPairHelper
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int processId, CancellationToken cancellationToken)
        { _ = processId; Calls++; Started.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); } finally { Cancelled.TrySetResult(); } return ReadOnlyMemory<byte>.Empty; }
    }
    private sealed class IgnoringHelper : IWindowsCpuPairHelper
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ReadOnlyMemory<byte>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int processId, CancellationToken cancellationToken)
        { _ = processId; _ = cancellationToken; Calls++; Started.TrySetResult(); return await Release.Task; }
    }
    private static WindowsTargetCpuPairRequest Request(HostBindingState state = HostBindingState.Bound)
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        HostIdentityFingerprint identity = HostIdentityFingerprint.FromOpaqueIdentity(Machine, Key);
        var host = new HostTarget(new HostTargetBinding(target, identity, new(1), state),
            new HostObservationProfile(target, revision, new(1), HostMetricCapability.Cpu), revision);
        return new(host, new PairedCpuBinding(target, revision, new(Guid.NewGuid()), identity.Value, 10, PairedCpuSqlProcessWitness.TargetEngineProcessId));
    }
    private static SqlCpuTargetWitness SqlWitness(WindowsTargetCpuPairRequest request, int? pid, bool? foreign = null) =>
        new(foreign == true ? new MonitoredInstanceId(Guid.NewGuid()) : request.Binding.TargetId,
            foreign == false ? new ObservationTargetRevision(2) : request.Binding.TargetRevision,
            new CollectorRunId(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), 1,
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            new ServerLifecycleEngineEvidence(null, null, pid, null, null, null), SqlCpuPhysicalTransport.Tcp, null, null, null);
    private static JsonObject Output()
    {
        static JsonObject Counter(uint type, string first, string second, string time, string timeBase = "10000000") => new()
        { ["counterType"] = type, ["cStatus"] = 0U, ["firstValue"] = first, ["secondValue"] = second, ["timestamp100ns"] = time, ["multiCount"] = 0U, ["apiStatus"] = 0U, ["timeBase"] = timeBase };
        static JsonObject Frame(bool after) => new()
        {
            ["observedAtUtc"] = after ? "2026-10-03T12:00:00.5000000Z" : "2026-10-03T12:00:00.0000000Z",
            ["monotonic100ns"] = after ? "2005000000" : "2000000000", ["machineUuid"] = Machine,
            ["bootFileTime100ns"] = "100", ["processCreation100ns"] = "10", ["processCreationAfter100ns"] = "10",
            ["processExitAfter100ns"] = "0", ["processWaitResultAfter"] = 258U, ["processName"] = "sqlservr.exe",
            ["processImage"] = "C:\\SQL\\sqlservr.exe", ["processStatus"] = 0U, ["osStatus"] = 0U,
            ["logicalProcessors"] = 128, ["logicalProcessorsAfter"] = 128, ["processInstances"] = 20, ["processMappingAmbiguous"] = false,
            ["process"] = Counter(PdhCpuRawCounter.ProcessTimerType, after ? "9223372036854775807" : "9223372036854775707", after ? "105000000" : "100000000", "140000000000000001"),
            ["os"] = Counter(PdhCpuRawCounter.OsIdleInverseTimerType, after ? "51000000" : "50000000", after ? "105000000" : "100000000", "140000000000000002"),
            ["idProcess"] = Counter(0x10000, "10", "0", "140000000000000003", "0"),
        };
        return new() { ["schema"] = 2, ["queryId"] = Guid.NewGuid().ToString("D"), ["before"] = Frame(false), ["after"] = Frame(true) };
    }
}
