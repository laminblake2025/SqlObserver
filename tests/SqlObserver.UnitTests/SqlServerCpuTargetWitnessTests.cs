using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerCpuTargetWitnessTests
{
    private static readonly int[] BeforeOrdinal = [1];
    private static readonly int[] CompleteOrdinals = [1, 2];
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);
    private static readonly Guid SessionId = Guid.Parse("d1ed957b-1c7b-49eb-9e2b-34e322cae1b7");
    private static readonly Guid ClientId = Guid.Parse("ddf18696-970f-4aed-8ee9-6d9021cac560");

    [Fact]
    public async Task PinnedSourceReadsExactCountersSequentiallyWithoutInferringLocality()
    {
        object?[] row = Row(); row[7] = long.MaxValue; row[8] = 9_007_199_254_740_993L;
        SqlCpuTargetWitness receipt = await Parse(row);
        Assert.Equal(long.MaxValue, receipt.ProcessUserTimeMs);
        Assert.Equal(9_007_199_254_740_993L, receipt.ProcessKernelTimeMs);
        Assert.Equal(DateTimeKind.Unspecified, receipt.Engine.SourceStartLocal!.Value.Kind);
        Assert.Equal(SqlCpuPhysicalTransport.Tcp, receipt.PhysicalTransport);
        Assert.False(receipt.HardwareIdentityVerified);
        Assert.False(receipt.LocalWindowsProcessAssociationQualified);
        Assert.Equal(ServerLifecycleIdentityFingerprint.PhysicalNode("private-sql-node", Key), receipt.Engine.PhysicalNodeFingerprint);
        Assert.Equal(1, receipt.Ordinal);
        string query = SqlServerCpuTargetWitnessReader.LoadQuery();
        Assert.Contains("CONNECTIONPROPERTY('physical_net_transport')", query, StringComparison.Ordinal);
        Assert.Contains("FROM sys.dm_os_sys_info", query, StringComparison.Ordinal);
        Assert.DoesNotContain("cpu_ticks", query, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TCP", SqlCpuPhysicalTransport.Tcp)]
    [InlineData("Named pipe", SqlCpuPhysicalTransport.NamedPipe)]
    [InlineData("Shared memory", SqlCpuPhysicalTransport.SharedMemory)]
    [InlineData(null, SqlCpuPhysicalTransport.Unknown)]
    public async Task ActualTransportRemainsEvidenceAndNeverAdmitsLocalTargetComparison(string? value, SqlCpuPhysicalTransport expected)
    {
        object?[] row = Row(); row[10] = value;
        SqlCpuTargetWitness receipt = await Parse(row);
        Assert.Equal(expected, receipt.PhysicalTransport);
        Assert.False(receipt.LocalWindowsProcessAssociationQualified);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(7)] [InlineData(9)] [InlineData(10)]
    public async Task MissingSourceFieldsRemainExplicitInsteadOfFabricatingIdentityOrCounters(int field)
    {
        object?[] row = Row(); row[field] = null;
        if (field == 0) { await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(row)); return; }
        SqlCpuTargetWitness before = await Parse(row);
        object?[] next = Row(); next[0] = ((DateTime)next[0]!).AddSeconds(1); next[6] = 102_000L;
        SqlCpuTargetWitness after = await Parse(next, 2);
        Assert.Equal(SqlCpuWitnessGap.SourceIncomplete, SqlCpuWitnessContinuity.Evaluate(before, after));
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(7)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public async Task InvalidSourceTypesNamesCountersAndSchemaAreRejected(int field)
    {
        object?[] row = Row(); row[field] = field switch
        {
            1 => " private-sql-node", 4 => DateTime.SpecifyKind(ServerLifecycleTestRows.StartLocal, DateTimeKind.Utc),
            7 => -1L, 9 => 0, 10 => "Session", 11 => 2, _ => throw new InvalidOperationException(),
        };
        if (field is 4 or 7 or 9)
            await Assert.ThrowsAsync<ArgumentException>(async () => await Parse(row));
        else
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(row));
    }

    [Fact]
    public async Task MissingExtraRowsAndWrongWidthAreRejected()
    {
        foreach (var reader in new[] { new SequentialReader([]), new SequentialReader([Row(), Row()]), new SequentialReader([Row()], 11) })
            await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlServerCpuTargetWitnessReader.ParseAsync(
                Request(), reader, Key, SessionId, ClientId, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("pid", SqlCpuWitnessGap.EngineBoundary)]
    [InlineData("node", SqlCpuWitnessGap.EngineBoundary)]
    [InlineData("start", SqlCpuWitnessGap.EngineBoundary)]
    [InlineData("clock", SqlCpuWitnessGap.ClockBoundary)]
    [InlineData("ticks", SqlCpuWitnessGap.ClockBoundary)]
    [InlineData("reset", SqlCpuWitnessGap.CounterReset)]
    [InlineData("topology", SqlCpuWitnessGap.TopologyBoundary)]
    [InlineData("transport", SqlCpuWitnessGap.ConnectionBoundary)]
    public async Task SqlContinuitySeparatelyDisclosesEngineClockCounterAndTopologyBoundaries(string mutation, SqlCpuWitnessGap expected)
    {
        SqlCpuTargetWitness before = await Parse(Row());
        object?[] row = Row(); row[0] = ((DateTime)row[0]!).AddSeconds(1); row[6] = 102_000L;
        switch (mutation)
        {
            case "pid": row[3] = 43; break; case "node": row[1] = "different-sql-node"; break;
            case "start": row[5] = 100_001L; break; case "clock": row[0] = ServerLifecycleTestRows.At; break;
            case "ticks": row[6] = 101_000L; break; case "reset": row[7] = 10L; break;
            case "topology": row[9] = 32; break; case "transport": row[10] = "Shared memory"; break;
        }
        Assert.Equal(expected, SqlCpuWitnessContinuity.Evaluate(before, await Parse(row, 2)));
    }

    [Fact]
    public async Task OrderedBracketUsesOneOwnedSessionAndTheCanonicalSqlPid()
    {
        var source = new Source(); var reader = new SqlServerCpuTargetWitnessReader(Key, source);
        int callbacks = 0;
        var result = await reader.ReadBracketAsync(Request(), (before, token) =>
        {
            token.ThrowIfCancellationRequested(); callbacks++;
            Assert.Equal(BeforeOrdinal, source.Reads);
            Assert.Equal(42, before.Engine.ProcessId);
            return Op("native-raw-evidence");
        }, CancellationToken.None);
        Assert.Equal(SqlCpuWitnessGap.None, result.ContinuityGap);
        Assert.Equal("native-raw-evidence", result.NativeEvidence);
        Assert.False(result.TargetComparisonAdmitted);
        Assert.Equal(result.Before.ConnectionSessionId, result.After.ConnectionSessionId);
        Assert.Equal(CompleteOrdinals, source.Reads);
        Assert.Equal(1, callbacks); Assert.Equal(1, source.Opens); Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ReconnectAndWrongRunCannotProduceAnAdmittedBracket()
    {
        foreach (bool wrongRun in new[] { false, true })
        {
            var source = new Source { Reconnect = !wrongRun, WrongRun = wrongRun };
            var reader = new SqlServerCpuTargetWitnessReader(Key, source);
            if (wrongRun)
                await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadBracketAsync(Request(),
                    (_, _) => Op(1), CancellationToken.None));
            else
            {
                var result = await reader.ReadBracketAsync(Request(), (_, _) => Op(1), CancellationToken.None);
                Assert.Equal(SqlCpuWitnessGap.ConnectionBoundary, result.ContinuityGap);
                Assert.False(result.TargetComparisonAdmitted);
            }
            Assert.True(source.Disposed);
        }
    }

    [Fact]
    public async Task CallbackFailureAndCancellationDisposeTheOwnedSessionWithoutSecondRead()
    {
        foreach (bool cancel in new[] { false, true })
        {
            var source = new Source(); var reader = new SqlServerCpuTargetWitnessReader(Key, source);
            using var cancellation = new CancellationTokenSource();
            Task Read() => reader.ReadBracketAsync<int>(Request(), (_, token) =>
            {
                if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new InvalidDataException("native provider failure");
            }, cancellation.Token).AsTask();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Read);
            else await Assert.ThrowsAsync<InvalidDataException>(Read);
            Assert.Equal(BeforeOrdinal, source.Reads); Assert.True(source.Disposed);
        }
    }

    [Fact]
    public async Task OriginalDeadlineIncludesNativeSamplingAndStillDisposesTheSession()
    {
        var source = new Source(); var reader = new SqlServerCpuTargetWitnessReader(Key, source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadBracketAsync<int>(Request(), (_, token) =>
            new Operation<int>(Wait(token)), CancellationToken.None));
        Assert.Equal(BeforeOrdinal, source.Reads); Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(15, true)] [InlineData(16, true)] [InlineData(17, true)]
    [InlineData(15, false)] [InlineData(16, false)] [InlineData(17, false)]
    public async Task VersionSpecificPermissionIsRequiredBeforeOpeningSql(int major, bool granted)
    {
        var source = new Source(); var reader = new SqlServerCpuTargetWitnessReader(Key, source);
        if (!granted)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await reader.ReadBracketAsync(Request(major, false),
                (_, _) => Op(1), CancellationToken.None));
            Assert.Equal(0, source.Opens);
        }
        else
        {
            var result = await reader.ReadBracketAsync(Request(major), (_, _) => Op(1), CancellationToken.None);
            Assert.False(result.TargetComparisonAdmitted); Assert.Equal(1, source.Opens);
        }
    }

    [Fact]
    public void StagedSourcePreservesConfiguredTcpAndMandatoryValidatedIntegratedTls()
    {
        using SqlConnection connection = SqlServerIntegratedConnectionFactory.CreateConnection(Request().ConnectionPolicy);
        var policy = new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.StartsWith("tcp:", policy.DataSource, StringComparison.Ordinal);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, policy.Encrypt);
        Assert.False(policy.TrustServerCertificate); Assert.True(policy.IntegratedSecurity);
        Assert.False(policy.MultipleActiveResultSets);
    }

    [Fact]
    public async Task IgnoredCancellationCannotReleaseTheTargetUntilNativeCompletionIsProven()
    {
        var source = new Source(); var reader = new SqlServerCpuTargetWitnessReader(Key, source);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new Operation<int>(held.Task); var request = Request();
        await Assert.ThrowsAsync<CpuNativeTerminationUnprovenException>(async () => await reader.ReadBracketAsync(request,
            (_, _) => operation, CancellationToken.None));
        Assert.True(operation.Cancelled); Assert.True(operation.Disposed); Assert.False(held.Task.IsCompleted);
        Assert.Equal(BeforeOrdinal, source.Reads);
        await Assert.ThrowsAsync<CpuNativeTerminationUnprovenException>(async () => await reader.ReadBracketAsync(request,
            (_, _) => Op(1), CancellationToken.None));
        Assert.Equal(1, source.Opens);
        held.SetResult(999); // This late value never becomes a bracket; it only proves work ended.
        var next = await reader.ReadBracketAsync(request, (_, _) => Op(2), CancellationToken.None);
        Assert.Equal(2, next.NativeEvidence); Assert.Equal(2, source.Opens);
    }

    [Fact]
    public async Task DelayedNativeStopRetriesInitiallyUnprovenCleanupWithoutAcceptingTheLateValue()
    {
        var source = new Source(); var reader = new SqlServerCpuTargetWitnessReader(Key, source);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new Operation<int>(held.Task, initiallyUnprovenDisposal: true); var request = Request();
        await Assert.ThrowsAsync<CpuNativeTerminationUnprovenException>(async () => await reader.ReadBracketAsync(request,
            (_, _) => operation, CancellationToken.None));
        Assert.Equal(1, operation.DisposeCalls); Assert.Equal(BeforeOrdinal, source.Reads);
        await Assert.ThrowsAsync<CpuNativeTerminationUnprovenException>(async () => await reader.ReadBracketAsync(request,
            (_, _) => Op(1), CancellationToken.None));
        Assert.Equal(1, source.Opens);
        held.SetResult(999);
        await operation.CleanupRetried.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, operation.DisposeCalls); Assert.True(operation.Disposed);
        Assert.Equal(BeforeOrdinal, source.Reads); // No after-receipt or late bracket was admitted.
    }

    private static async Task<int> Wait(CancellationToken token)
    { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
    private static Operation<T> Op<T>(T value) => new(Task.FromResult(value));
    private sealed class Operation<T>(Task<T> completion, bool initiallyUnprovenDisposal = false) : ICpuNativeSamplingOperation<T>
    {
        public Task<T> Completion => completion;
        internal bool Cancelled { get; private set; }
        internal bool Disposed { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal TaskCompletionSource CleanupRetried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel() => Cancelled = true;
        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            try { _ = await completion.WaitAsync(timeout); }
            catch (TimeoutException) { return false; }
            catch (OperationCanceledException) { }
            return completion.IsCompleted;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (initiallyUnprovenDisposal && DisposeCalls == 1) throw new CpuNativeTerminationUnprovenException();
            Disposed = true; if (DisposeCalls > 1) CleanupRetried.TrySetResult(); return ValueTask.CompletedTask;
        }
    }

    private static object?[] Row() => [ServerLifecycleTestRows.At, "private-sql-node", "private-sql-instance", 42,
        ServerLifecycleTestRows.StartLocal, 100_000L, 101_000L, 100L, 200L, 16, "TCP", 1];
    private static ValueTask<SqlCpuTargetWitness> Parse(object?[] row, int ordinal = 1) => SqlServerCpuTargetWitnessReader.ParseAsync(
        Request(), new SequentialReader([row]), Key, SessionId, ClientId, ordinal, CancellationToken.None);
    private static CollectorExecutionRequest Request(int major = 16, bool granted = true)
    {
        var template = M4TestData.CreateProfile(majorVersion: major);
        var profile = new CapabilityProfile(template.TargetId, template.TargetRevision, template.CollectorId, 1, 1,
            template.ServerIdentity, template.Outcome, template.Reason, template.AuthenticationScheme,
            template.TransportEncrypted, template.IsSysAdmin, template.Capabilities,
            [new PermissionEvidence(new SqlServerPermissionId(major == 15 ? "server.view-state" : "server.view-performance-state"),
                PermissionEvidenceScope.Server, granted ? PermissionEvidenceOutcome.Granted : PermissionEvidenceOutcome.Denied)],
            template.DiscoveryDuration, template.EvidenceBytes, template.CheckedAtUtc, template.ValidUntilUtc);
        var request = M4TestData.CreateExecutionRequest();
        return new(request.RunId, request.TargetId, request.TargetRevision, request.ConnectionPolicy, profile, request.Attempt, request.Timeout);
    }

    private sealed class Source : ISqlCpuWitnessSource, ISqlCpuWitnessSession
    {
        private CollectorExecutionRequest? request;
        internal int Opens { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool Reconnect { get; init; }
        internal bool WrongRun { get; init; }
        internal List<int> Reads { get; } = [];
        public ValueTask<ISqlCpuWitnessSession> OpenAsync(CollectorExecutionRequest execution, IdentityFingerprintKey key, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); request = execution; Opens++; return ValueTask.FromResult<ISqlCpuWitnessSession>(this); }
        public async ValueTask<SqlCpuTargetWitness> ReadAsync(int ordinal, CancellationToken cancellationToken)
        {
            Reads.Add(ordinal); object?[] row = Row(); row[0] = ((DateTime)row[0]!).AddSeconds(ordinal); row[6] = 101_000L + ordinal;
            var actual = request!;
            if (WrongRun && ordinal == 2) actual = new(new CollectorRunId(Guid.NewGuid()), actual.TargetId, actual.TargetRevision,
                actual.ConnectionPolicy, actual.CapabilityProfile, actual.Attempt, actual.Timeout);
            return await SqlServerCpuTargetWitnessReader.ParseAsync(actual, new SequentialReader([row]), Key, SessionId,
                Reconnect && ordinal == 2 ? Guid.NewGuid() : ClientId, ordinal, cancellationToken);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    // Models SequentialAccess: backwards field reads fail instead of being hidden by a DTO mock.
    private sealed class SequentialReader(object?[][] rows, int width = 12) : IOperationalHealthRowReader
    {
        private int row = -1, last = -1;
        public int FieldCount => width;
        private object? Value(int ordinal)
        { if (ordinal < last) throw new InvalidDataException("Backwards sequential field access."); last = ordinal; return rows[row][ordinal]; }
        public bool IsDBNull(int ordinal) => Value(ordinal) is null;
        public int GetInt32(int ordinal) => (int)Value(ordinal)!;
        public long GetInt64(int ordinal) => (long)Value(ordinal)!;
        public DateTime GetDateTime(int ordinal) => (DateTime)Value(ordinal)!;
        public string GetString(int ordinal) => (string)Value(ordinal)!;
        public byte[] GetBinary(int ordinal) => throw new NotSupportedException();
        public bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public short GetInt16(int ordinal) => throw new NotSupportedException();
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); last = -1; return ValueTask.FromResult(++row < rows.Length); }
    }
}
