using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class WindowsCpuTcpAssociationReaderTests
{
    private const string Machine = "11111111-1111-1111-1111-111111111111";
    private static readonly IdentityFingerprintKey Key = new(Enumerable.Repeat((byte)0x51, 32).ToArray());

    [Fact]
    public async Task ExactForwardAndReverseOwnersAndRawLifetimeWitnessesSurviveWithoutAdmission()
    {
        WindowsCpuTcpAssociationRequest request = Request(); var helper = new Helper(Output());
        var result = await new WindowsCpuTcpAssociationReader(Key, helper).ReadAsync(request, CancellationToken.None);
        Assert.Equal("partial", result.State); Assert.Equal("source_qualification_absent", result.Reason);
        Assert.Equal(PairedCpuNativeQualification.Absent, result.NativeQualification); Assert.False(result.TargetComparisonAdmitted);
        CpuTcpNativeFrame frame = Assert.IsType<CpuTcpNativeFrame>(result.Frame);
        Assert.Equal(request.Witness.RunId, frame.Binding.RunId); Assert.Equal(request.Witness.TargetRevision, frame.Binding.TargetRevision);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, frame.Binding.SqlProcessWitness);
        Assert.Equal(2, frame.Rows.Count); Assert.Equal(1433, frame.Rows[0].Local.Port); Assert.Equal(50000, frame.Rows[0].Remote.Port);
        Assert.Equal(10U, frame.Rows[0].OwnerProcessId); Assert.Equal((uint)Environment.ProcessId, frame.Rows[1].OwnerProcessId);
        Assert.Equal(140_000_000_000_000_001L, frame.SqlProcess.CreationBefore100ns);
        Assert.Equal(long.MinValue, frame.SqlProcess.ExitAfter100ns); Assert.Equal(258U, frame.SqlProcess.WaitResultAfter);
        Assert.Equal(128, frame.RowCount4); Assert.Equal(32, frame.RowCount6);
        Assert.NotEqual(frame.Host.MachineFingerprint, frame.Host.ActualHostFingerprint);
        Assert.Same(request, helper.Request); Assert.Equal(Environment.ProcessId, request.Witness.TcpConnection!.CollectorProcessId);
        string diagnostic = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("7f000001", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", frame.Rows[0].Local.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedIpv6DecodesNetworkOrderWithoutInferringInterfaceEquality()
    {
        JsonObject json = Output(ipv6: true);
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(ipv6: true), CancellationToken.None);
        Assert.Equal(6, result.Frame!.Rows[0].Local.Family); Assert.Equal(7U, result.Frame.Rows[0].Local.ScopeId);
        Assert.Equal(1433, result.Frame.Rows[0].Local.Port); Assert.Equal(50000, result.Frame.Rows[0].Remote.Port);
        Assert.False(result.TargetComparisonAdmitted);
    }

    [Fact]
    public async Task Ipv4MappedNativeRowMatchesOnlyExplicitZeroScopeSourceNormalization()
    {
        JsonObject json = Output(); foreach (JsonNode? item in json["rows"]!.AsArray())
        {
            item!["family"] = 6; item["localAddress"] = "00000000000000000000ffff7f000001";
            item["remoteAddress"] = "00000000000000000000ffff7f000001";
        }
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal(4, result.Frame!.Rows[0].Local.Family);
        json["rows"]![0]!["localScopeRaw"] = BinaryPrimitives.ReverseEndianness(1U);
        Assert.Equal("invalid_source_output", (await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None)).Reason);
    }

    [Theory]
    [InlineData("af4Status")]
    [InlineData("af6Status")]
    public async Task NativePermissionAndBufferRacesRemainVisibleWithoutAnEmptyHealthyTable(string field)
    {
        JsonObject json = Output(); json[field] = field == "af4Status" ? 5U : 122U;
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("native_table_unavailable", result.Reason); Assert.NotNull(result.Frame);
        Assert.Equal(field == "af4Status" ? 5U : 122U, field == "af4Status" ? result.Frame.Af4Status : result.Frame.Af6Status);
        Assert.False(result.TargetComparisonAdmitted);
    }

    [Theory]
    [InlineData("apiStatus", "denied", "native_process_denied")]
    [InlineData("creationAfter100ns", "140000000000000002", "process_epoch_changed")]
    [InlineData("waitResultAfter", "exited", "process_exited")]
    [InlineData("waitResultAfter", "unknown", "process_lifetime_unknown")]
    public async Task SameHandlePermissionExitAndPidReuseRemainExplicit(string field, string value, string reason)
    {
        JsonObject json = Output(); json["sqlProcess"]![field] = value switch
        { "denied" => JsonValue.Create(5U), "exited" => JsonValue.Create(0U), "unknown" => null, _ => JsonValue.Create(value) };
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal(reason, result.Reason); Assert.Equal(2, result.Frame!.Rows.Count); Assert.False(result.TargetComparisonAdmitted);
    }

    [Fact]
    public async Task UndefinedRunningExitFileTimeIsNotUsedAsAliveOrExitedProof()
    {
        JsonObject json = Output(); json["sqlProcess"]!["exitAfter100ns"] = "9223372036854775807";
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("source_qualification_absent", result.Reason);
        Assert.Equal(long.MaxValue, result.Frame!.SqlProcess.ExitAfter100ns); Assert.Equal(258U, result.Frame.SqlProcess.WaitResultAfter);
    }

    [Theory]
    [InlineData("rowCount4", 4097)]
    [InlineData("rowCount6", 4000)]
    public async Task CombinedNativeTableCapRefusesRatherThanTruncates(string field, int count)
    {
        JsonObject json = Output(); json[field] = count;
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("invalid_source_output", result.Reason); Assert.Null(result.Frame);
    }

    [Fact]
    public async Task MatchingRowCapAndUnknownOrDuplicatePropertiesAreRejected()
    {
        JsonObject json = Output(); var rows = json["rows"]!.AsArray();
        while (rows.Count <= WindowsCpuTcpAssociationReader.MaximumMatchingRows) rows.Add(rows[0]!.DeepClone());
        Assert.Equal("invalid_source_output", (await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None)).Reason);
        json = Output(); json["remoteComputer"] = "not-accepted";
        Assert.Equal("invalid_source_output", (await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None)).Reason);
        byte[] duplicate = Encoding.UTF8.GetBytes(Output().ToJsonString().Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal));
        Assert.Equal("invalid_source_output", (await new WindowsCpuTcpAssociationReader(Key, new Helper(duplicate)).ReadAsync(Request(), CancellationToken.None)).Reason);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("9223372036854775808")]
    [InlineData("1.0")]
    public async Task LifetimeInt64StringsAreNeverCoercedOrRounded(string value)
    {
        JsonObject json = Output(); json["sqlProcess"]!["creationBefore100ns"] = value;
        Assert.Equal("invalid_source_output", (await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task WrongHostDropsAllEndpointAndProcessEvidence()
    {
        JsonObject json = Output(); json["machineUuid"] = "22222222-2222-2222-2222-222222222222";
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("wrong_host", result.Reason); Assert.Null(result.Frame);
    }

    [Fact]
    public async Task UnrelatedEndpointRowsAndOversizedOutputCannotEscapeIntoReceipt()
    {
        JsonObject json = Output(); json["rows"]![0]!["remoteAddress"] = "7f000002";
        Assert.Equal("invalid_source_output", (await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None)).Reason);
        Assert.Equal("invalid_source_output", (await new WindowsCpuTcpAssociationReader(Key,
            new Helper(new byte[WindowsCpuTcpAssociationReader.MaximumOutputBytes + 1])).ReadAsync(Request(), CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task CompleteTablesWithoutMatchingSocketsRemainAnExplicitCandidateGap()
    {
        JsonObject json = Output(); json["rows"] = new JsonArray();
        var result = await new WindowsCpuTcpAssociationReader(Key, new Helper(json)).ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("partial", result.State); Assert.Equal("socket_not_observed", result.Reason);
        Assert.Empty(result.Frame!.Rows); Assert.Equal(128, result.Frame.RowCount4); Assert.Equal(32, result.Frame.RowCount6);
        Assert.False(result.TargetComparisonAdmitted);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("no_tcp")]
    [InlineData("no_pid")]
    [InlineData("old_schema")]
    [InlineData("scoped_address_missing_scope")]
    public async Task AbsentSourceOrDeniedBindingNeverStartsHelper(string missing)
    {
        var helper = new Helper(Output()); var reader = new WindowsCpuTcpAssociationReader(Key, helper);
        var result = await reader.ReadAsync(Request(missing: missing), CancellationToken.None);
        Assert.Equal("unavailable", result.State); Assert.Null(result.Frame); Assert.Equal(0, helper.Calls);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("revision")]
    [InlineData("collector_pid")]
    public void RequestFactoryRejectsForeignTargetRevisionAndCallerDeclaredCollectorPid(string changed)
    {
        var request = Request(); var source = request.Witness;
        var tcp = new SqlCpuTcpConnectionEvidence(Guid.NewGuid(), 17, "127.0.0.1", 1433, "127.0.0.1", 50000,
            changed == "collector_pid" ? Environment.ProcessId + 1 : Environment.ProcessId);
        var foreign = new SqlCpuTargetWitness(changed == "target" ? new MonitoredInstanceId(Guid.NewGuid()) : source.TargetId,
            changed == "revision" ? new ObservationTargetRevision(2) : source.TargetRevision, source.RunId,
            source.ConnectionSessionId, source.ClientConnectionId, 1, source.SourceObservedAtUtc, source.Engine,
            source.PhysicalTransport, null, null, null, tcpConnection: tcp, sourceSchemaVersion: source.SourceSchemaVersion);
        Assert.Throws<ArgumentException>(() => WindowsCpuTcpAssociationRequest.FromSqlWitness(request.Target, foreign));
    }

    [Fact]
    public async Task CooperativeCancellationWaitsForCleanupThenReleasesTargetOwnership()
    {
        var helper = new HeldHelper(); var reader = new WindowsCpuTcpAssociationReader(Key, helper); var request = Request();
        await using var operation = reader.StartOperation(request, CancellationToken.None); await helper.Started.Task;
        Assert.Throws<InvalidOperationException>(() => reader.StartOperation(request, CancellationToken.None));
        Assert.True(await operation.TerminateAsync(TimeSpan.FromSeconds(1))); Assert.True(helper.Stopped.Task.IsCompleted);
        await using var next = reader.StartOperation(request, CancellationToken.None);
        Assert.Equal(2, helper.Calls); Assert.True(await next.TerminateAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CallerCancellationPropagatesOnlyAfterOwnedHelperCanBeStopped()
    {
        var helper = new HeldHelper(); var reader = new WindowsCpuTcpAssociationReader(Key, helper);
        using var cancel = new CancellationTokenSource();
        Task<WindowsCpuTcpAssociationReadResult> pending = reader.ReadAsync(Request(), cancel.Token).AsTask();
        await helper.Started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(helper.Stopped.Task.IsCompleted);
    }

    [Fact]
    public async Task IgnoredCancellationQuarantinesUntilActualCompletionAndDiscardsLatePayload()
    {
        var helper = new IgnoringHelper(); var reader = new WindowsCpuTcpAssociationReader(Key, helper); var request = Request();
        var operation = reader.StartOperation(request, CancellationToken.None); await helper.Started.Task;
        Assert.False(await operation.TerminateAsync(TimeSpan.FromMilliseconds(10)));
        Assert.Throws<CpuNativeTerminationUnprovenException>(() => reader.StartOperation(request, CancellationToken.None));
        Assert.False(operation.Completion.IsCompleted); Assert.Equal(1, helper.Calls);
        helper.Release.SetResult(Encoding.UTF8.GetBytes(Output().ToJsonString()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
        await operation.DisposeAsync(); await using var recovered = reader.StartOperation(request, CancellationToken.None);
        Assert.NotNull((await recovered.Completion).Frame); Assert.Equal(2, helper.Calls);
    }

    private sealed class Helper : IWindowsCpuTcpAssociationHelper
    {
        private readonly byte[] _bytes;
        internal Helper(JsonObject json) => _bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        internal Helper(byte[] bytes) => _bytes = bytes;
        internal int Calls { get; private set; }
        internal WindowsCpuTcpAssociationRequest? Request { get; private set; }
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(WindowsCpuTcpAssociationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Request = request; return ValueTask.FromResult<ReadOnlyMemory<byte>>(_bytes); }
    }
    private sealed class HeldHelper : IWindowsCpuTcpAssociationHelper
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(WindowsCpuTcpAssociationRequest request, CancellationToken token)
        { _ = request; Calls++; Started.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } finally { Stopped.TrySetResult(); } return ReadOnlyMemory<byte>.Empty; }
    }
    private sealed class IgnoringHelper : IWindowsCpuTcpAssociationHelper
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ReadOnlyMemory<byte>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(WindowsCpuTcpAssociationRequest request, CancellationToken token)
        { _ = request; _ = token; Calls++; Started.TrySetResult(); return await Release.Task; }
    }
    private static WindowsCpuTcpAssociationRequest Request(bool ipv6 = false, string? missing = null)
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var fingerprint = HostIdentityFingerprint.FromOpaqueIdentity(Machine, Key);
        var host = new HostTarget(new HostTargetBinding(target, fingerprint, new(1), missing == "denied" ? HostBindingState.Denied : HostBindingState.Bound),
            new HostObservationProfile(target, revision, new(1), HostMetricCapability.Cpu), revision);
        var tcp = new SqlCpuTcpConnectionEvidence(Guid.NewGuid(), 17, missing == "scoped_address_missing_scope" ? "fe80::1" : ipv6 ? "fe80::1%7" : "127.0.0.1", 1433,
            ipv6 ? "fe80::2%7" : "127.0.0.1", 50000, Environment.ProcessId);
        var witness = new SqlCpuTargetWitness(target, revision, new(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), 1,
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), new ServerLifecycleEngineEvidence(null, null, missing == "no_pid" ? null : 10, null, null, null),
            SqlCpuPhysicalTransport.Tcp, null, null, null, tcpConnection: missing == "no_tcp" ? null : tcp, sourceSchemaVersion: missing == "old_schema" ? 1 : 2);
        return WindowsCpuTcpAssociationRequest.FromSqlWitness(host, witness);
    }
    private static JsonObject Output(bool ipv6 = false)
    {
        static uint NativePort(int port) => checked((uint)((port >> 8) | ((port & 255) << 8)));
        static JsonObject Epoch() => new() { ["creationBefore100ns"] = "140000000000000001", ["creationAfter100ns"] = "140000000000000001",
            ["exitAfter100ns"] = "-9223372036854775808", ["apiStatus"] = 0U, ["waitResultAfter"] = 258U };
        string server = ipv6 ? "fe800000000000000000000000000001" : "7f000001", client = ipv6 ? "fe800000000000000000000000000002" : "7f000001";
        uint scope = ipv6 ? BinaryPrimitives.ReverseEndianness(7U) : 0;
        JsonObject Row(bool reverse) => new() { ["family"] = ipv6 ? 6 : 4, ["localAddress"] = reverse ? client : server,
            ["remoteAddress"] = reverse ? server : client, ["localScopeRaw"] = scope, ["remoteScopeRaw"] = scope,
            ["localPortRaw"] = NativePort(reverse ? 50000 : 1433), ["remotePortRaw"] = NativePort(reverse ? 1433 : 50000),
            ["state"] = 5U, ["pid"] = reverse ? (uint)Environment.ProcessId : 10U };
        return new() { ["schema"] = 1, ["observedAtUtc"] = "2026-10-03T12:00:00.0000000Z", ["monotonic100ns"] = "123456789000",
            ["machineUuid"] = Machine, ["boot100ns"] = "130000000000000001", ["sqlProcess"] = Epoch(), ["collectorProcess"] = Epoch(),
            ["af4Status"] = 0U, ["af6Status"] = 0U, ["rowCount4"] = 128, ["rowCount6"] = 32, ["rows"] = new JsonArray(Row(false), Row(true)) };
    }
}
