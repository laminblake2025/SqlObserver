using System.Text.Json;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class CpuTcpAssociationObservationsTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly CollectorRunId Run = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly Guid OwnedConnection = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid ClientConnection = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid ServerConnection = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid Query = Guid.Parse("66666666-6666-4666-8666-666666666666");
    private static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Host = new('a', 64), Machine = new('b', 64), Logical = new('c', 64);
    private const long Boot = 130_000_000_000_000_000, SqlCreation = Boot + 10_000_000, ClientCreation = Boot + 20_000_000;
    private const int SqlPid = 42, ClientPid = 99;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static SqlCpuTcpConnectionEvidence Connection(Guid? id = null, int session = 57,
        string? server = "127.0.0.1", int? serverPort = 1433, string? client = "127.0.0.1",
        int? clientPort = 51000, int clientPid = ClientPid) =>
        new(id ?? ServerConnection, session, server, serverPort, client, clientPort, clientPid);

    private static SqlCpuTargetWitness Sql(bool after = false, MonitoredInstanceId? target = null,
        long revision = 1, CollectorRunId? run = null, Guid? owned = null, Guid? clientId = null,
        SqlCpuPhysicalTransport transport = SqlCpuPhysicalTransport.Tcp, SqlCpuTcpConnectionEvidence? tcp = null,
        bool missingTcp = false, int pid = SqlPid, string? node = null, int schema = 2,
        DateTimeOffset? observed = null) =>
        new(target ?? Target, new(revision), run ?? Run, owned ?? OwnedConnection, clientId ?? ClientConnection,
            after ? 2 : 1, observed ?? At.AddSeconds(after ? 4 : 0),
            new(node ?? Host, Logical, pid, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified),
                1000, after ? 5000 : 1001), transport, after ? 200 : 100, after ? 300 : 200, 4,
            missingTcp ? null : tcp ?? Connection(), schema);

    private static PairedCpuBinding Binding(MonitoredInstanceId? target = null, long revision = 1,
        CollectorRunId? run = null, string? expectedHost = null, int pid = SqlPid) =>
        new(target ?? Target, new(revision), run ?? Run, expectedHost ?? Host, pid);

    private static PairedCpuHostEvidence HostEvidence(string? actual = null, string? machine = null,
        long boot = Boot, bool missing = false) => new(actual ?? Host, machine ?? Machine, missing ? null : boot,
            missing ? PairedCpuHostWitness.Unavailable : PairedCpuHostWitness.NativeMachineAndBoot);

    private static CpuTcpProcessEpoch Epoch(long creation = SqlCreation, long? secondCreation = null,
        uint api = 0, uint? wait = CpuTcpProcessEpoch.StillRunningWaitResult, long? exit = 0) =>
        new(creation, secondCreation ?? creation, exit, api, wait);

    private static CpuNativeTcpRow[] Rows(SqlCpuTcpConnectionEvidence? connection = null)
    {
        SqlCpuTcpConnectionEvidence source = connection ?? Connection();
        return [new(source.ServerEndpoint!, source.ClientEndpoint!, 5, SqlPid),
            new(source.ClientEndpoint!, source.ServerEndpoint!, 5, ClientPid)];
    }

    private static CpuTcpNativeFrame Native(bool after = false, PairedCpuBinding? binding = null,
        PairedCpuHostEvidence? host = null, CpuTcpProcessEpoch? sql = null, CpuTcpProcessEpoch? client = null,
        CpuNativeTcpRow[]? rows = null, uint af4 = 0, uint af6 = 0,
        long? mono = null, DateTimeOffset? observed = null) =>
        new(binding ?? Binding(), observed ?? At.AddSeconds(after ? 3 : 1), mono ?? (after ? 30_000_000 : 10_000_000),
            host ?? HostEvidence(), sql ?? Epoch(), client ?? Epoch(ClientCreation), af4, af6,
            rows?.Length ?? 2, 0, rows ?? Rows());

    private static PairedCpuEndpoint Cpu(bool after = false, PairedCpuBinding? binding = null,
        PairedCpuHostEvidence? host = null, long? creation = SqlCreation, Guid? query = null,
        long? mono = null, long frequency = 10_000_000, DateTimeOffset? observed = null) =>
        new(binding ?? Binding(), query ?? Query, observed ?? At.AddSeconds(after ? 2.5 : 1.5),
            mono ?? (after ? 25_000_000 : 15_000_000), frequency, host ?? HostEvidence(), creation, 4, null, null);

    private static CpuTcpAssociation Evaluate(SqlCpuTargetWitness? beforeSql = null, SqlCpuTargetWitness? afterSql = null,
        CpuTcpNativeFrame? before = null, CpuTcpNativeFrame? after = null,
        PairedCpuEndpoint? beforeCpu = null, PairedCpuEndpoint? afterCpu = null) =>
        CpuTcpAssociationQualifier.Evaluate(beforeSql ?? Sql(), afterSql ?? Sql(true), before ?? Native(),
            after ?? Native(true), beforeCpu ?? Cpu(), afterCpu ?? Cpu(true));

    [Fact]
    public void ExactTwoDirectionsProduceOnlyCandidateAssociationNotCpuAdmission()
    {
        var result = Evaluate();
        Assert.True(result.CandidateLocalAssociation);
        Assert.Equal(PairedCpuNativeQualification.Absent, result.NativeQualification);
        Assert.False(result.TargetComparisonAdmitted); Assert.False(result.HardwareIdentityVerified);
        Assert.NotEqual(ServerConnection, ClientConnection); // Independent native IDs need not be equal.
        Assert.Null(Cpu().ProcessCounter);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, Cpu().Binding.SqlProcessWitness);
    }

    [Theory]
    [InlineData("127.0.0.1", "::ffff:127.0.0.1")]
    [InlineData("2001:db8::1", "2001:0db8:0:0:0:0:0:1")]
    [InlineData("fe80::1%9", "fe80:0:0:0:0:0:0:1%9")]
    public void NumericRepresentationsCompareOnlyCanonicalBytesAndScope(string first, string second)
    {
        Assert.True(CpuTcpEndpoint.TryParse(first, 1433, out var left));
        Assert.True(CpuTcpEndpoint.TryParse(second, 1433, out var right));
        Assert.Equal(left, right); Assert.Equal(left!.GetHashCode(), right!.GetHashCode());
        byte[] copy = left.CopyAddressBytes(); copy[0] ^= 255;
        Assert.Equal(left, right); // Caller cannot mutate the address identity.
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.1")]
    [InlineData("0x7f000001")]
    [InlineData("127.000.0.1")]
    [InlineData(" 127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%ethernet")]
    [InlineData("fe80::1%4294967296")]
    [InlineData("::ffff:127.0.0.1%2")]
    [InlineData("::")]
    [InlineData("0.0.0.0")]
    [InlineData("ff02::1%1")]
    [InlineData("255.255.255.255")]
    public void UnsupportedAddressesNeverBecomeEquivalentViaAliasesOrGuesses(string value)
    {
        Assert.False(CpuTcpEndpoint.TryParse(value, 1433, out _));
        Assert.Equal(CpuTcpSocketCompleteness.UnsupportedAddress, Connection(server: value).Completeness);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(65536)]
    public void InvalidPortsAndMissingFieldsKeepWitnessIncomplete(int port)
    {
        Assert.False(CpuTcpEndpoint.TryParse("127.0.0.1", port, out _));
        Assert.Equal(CpuTcpSocketCompleteness.Incomplete, Connection(clientPort: null).Completeness);
        Assert.Equal(CpuTcpAssociationGap.SocketWitnessIncomplete, Evaluate(afterSql: Sql(true, missingTcp: true)).Reason);
    }

    [Fact]
    public void NativeBytesUseNetworkOrderAndScopeRemainsPartOfIdentity()
    {
        var endpoint = CpuTcpEndpoint.FromNativeHex(4, "7f000001", 0, 1433);
        Assert.Equal(Connection().ServerEndpoint, endpoint);
        var scoped = CpuTcpEndpoint.FromNativeHex(6, "fe800000000000000000000000000001", 9, 1433);
        CpuTcpEndpoint.TryParse("fe80::1%9", 1433, out var sql); Assert.Equal(sql, scoped);
        Assert.NotEqual(scoped, CpuTcpEndpoint.FromNativeHex(6, "fe800000000000000000000000000001", 10, 1433));
        Assert.Throws<ArgumentException>(() => CpuTcpEndpoint.FromNativeHex(4, "7f000001", 1, 1433));
    }

    [Fact]
    public void RawAddressesAreNotGenericJsonOrDiagnosticProperties()
    {
        string json = JsonSerializer.Serialize(Connection(), JsonOptions);
        string diagnostic = Connection().ToString();
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("7f000001", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("target", CpuTcpAssociationGap.TargetBoundary)]
    [InlineData("revision", CpuTcpAssociationGap.RevisionBoundary)]
    [InlineData("run", CpuTcpAssociationGap.RunBoundary)]
    [InlineData("owned", CpuTcpAssociationGap.ConnectionBoundary)]
    [InlineData("client", CpuTcpAssociationGap.ConnectionBoundary)]
    [InlineData("server", CpuTcpAssociationGap.ConnectionBoundary)]
    [InlineData("session", CpuTcpAssociationGap.ConnectionBoundary)]
    public void ForeignOrReconnectedSqlReceiptCannotRetargetCandidate(string change, CpuTcpAssociationGap expected)
    {
        var after = change switch
        {
            "target" => Sql(true, target: new(Guid.NewGuid())), "revision" => Sql(true, revision: 2),
            "run" => Sql(true, run: new(Guid.NewGuid())), "owned" => Sql(true, owned: Guid.NewGuid()),
            "client" => Sql(true, clientId: Guid.NewGuid()), "server" => Sql(true, tcp: Connection(id: Guid.NewGuid())),
            _ => Sql(true, tcp: Connection(session: 58)),
        };
        Assert.Equal(expected, Evaluate(afterSql: after).Reason);
    }

    [Theory]
    [InlineData("target", CpuTcpAssociationGap.TargetBoundary)]
    [InlineData("revision", CpuTcpAssociationGap.RevisionBoundary)]
    [InlineData("run", CpuTcpAssociationGap.RunBoundary)]
    [InlineData("pid", CpuTcpAssociationGap.ProcessRestart)]
    public void SocketOrCpuCaptureFromAnotherOwnerIsRefused(string change, CpuTcpAssociationGap expected)
    {
        var binding = change switch
        {
            "target" => Binding(target: new(Guid.NewGuid())), "revision" => Binding(revision: 2),
            "run" => Binding(run: new(Guid.NewGuid())), _ => Binding(pid: 43),
        };
        Assert.Equal(expected, Evaluate(after: Native(true, binding: binding)).Reason);
        Assert.Equal(expected, Evaluate(afterCpu: Cpu(true, binding: binding)).Reason);
    }

    [Fact]
    public void TcpTupleAndCollectorPidCannotChangeAcrossOwnedSqlBracket()
    {
        Assert.Equal(CpuTcpAssociationGap.TupleChanged, Evaluate(afterSql: Sql(true, tcp: Connection(clientPort: 51001))).Reason);
        Assert.Equal(CpuTcpAssociationGap.CollectorProcessChanged, Evaluate(afterSql: Sql(true, tcp: Connection(clientPid: 100))).Reason);
        Assert.Equal(CpuTcpAssociationGap.SqlEngineBoundary, Evaluate(afterSql: Sql(true, pid: 43)).Reason);
    }

    [Fact]
    public void LegacySourceCannotClaimNativeTcpTupleWitnessFromAnOptionalCallerField()
    {
        Assert.Equal(CpuTcpAssociationGap.SourceSchemaBoundary,
            Evaluate(beforeSql: Sql(schema: 1), afterSql: Sql(true, schema: 1)).Reason);
        Assert.Equal(CpuTcpAssociationGap.SourceSchemaBoundary, Evaluate(afterSql: Sql(true, schema: 1)).Reason);
    }

    [Theory]
    [InlineData(SqlCpuPhysicalTransport.SharedMemory)] [InlineData(SqlCpuPhysicalTransport.NamedPipe)]
    public void OtherTransportsNeverInheritTcpAssociation(SqlCpuPhysicalTransport transport)
    {
        Assert.Equal(CpuTcpAssociationGap.UnsupportedTransport,
            Evaluate(beforeSql: Sql(transport: transport), afterSql: Sql(true, transport: transport)).Reason);
    }

    [Theory]
    [InlineData("missing", CpuTcpAssociationGap.HostWitnessIncomplete)]
    [InlineData("binding", CpuTcpAssociationGap.HostBindingMismatch)]
    [InlineData("machine", CpuTcpAssociationGap.MachineBoundary)]
    [InlineData("boot", CpuTcpAssociationGap.BootBoundary)]
    public void IndependentlyObservedHostEpochMustMatchEveryCpuAndSocketCapture(string change, CpuTcpAssociationGap expected)
    {
        var host = change switch
        {
            "missing" => HostEvidence(missing: true), "binding" => HostEvidence(actual: new string('d', 64)),
            "machine" => HostEvidence(machine: new string('d', 64)), _ => HostEvidence(boot: Boot + 1),
        };
        Assert.Equal(expected, Evaluate(after: Native(true, host: host)).Reason);
        Assert.Equal(expected, Evaluate(afterCpu: Cpu(true, host: host)).Reason);
    }

    [Fact]
    public void ProcessCreationMustRemainStableWithinAndAcrossAllFourNativeWitnesses()
    {
        Assert.Equal(CpuTcpAssociationGap.ProcessRestart, Evaluate(after: Native(true, sql: Epoch(secondCreation: SqlCreation + 1))).Reason);
        Assert.Equal(CpuTcpAssociationGap.ProcessRestart, Evaluate(after: Native(true, sql: Epoch(SqlCreation + 1))).Reason);
        Assert.Equal(CpuTcpAssociationGap.ProcessRestart, Evaluate(afterCpu: Cpu(true, creation: SqlCreation + 1)).Reason);
        Assert.Equal(CpuTcpAssociationGap.ProcessUnavailable, Evaluate(afterCpu: Cpu(true, creation: null)).Reason);
        Assert.Equal(CpuTcpAssociationGap.ProcessRestart, Evaluate(after: Native(true, client: Epoch(ClientCreation + 1))).Reason);
    }

    [Theory]
    [InlineData(0u, CpuTcpAssociationGap.ProcessExited)]
    [InlineData(uint.MaxValue, CpuTcpAssociationGap.ProcessLifetimeUnknown)]
    public void SamePidCreationAndZeroExitValueCannotOverrideActualHandleLifetime(uint wait, CpuTcpAssociationGap expected)
    {
        Assert.Equal(expected, Evaluate(after: Native(true, sql: Epoch(wait: wait, exit: 0))).Reason);
        Assert.Equal(CpuTcpAssociationGap.ProcessLifetimeUnknown, Evaluate(after: Native(true, client: Epoch(ClientCreation, wait: null))).Reason);
        Assert.True(Evaluate(after: Native(true, sql: Epoch(exit: long.MinValue))).CandidateLocalAssociation);
    }

    [Fact]
    public void ProcessOrTableAccessFailureIsNotMissingHealthyEvidence()
    {
        Assert.Equal(CpuTcpAssociationGap.ProcessUnavailable, Evaluate(after: Native(true, sql: Epoch(api: 5))).Reason);
        Assert.Equal(CpuTcpAssociationGap.NativeTableUnavailable, Evaluate(after: Native(true, af4: 5)).Reason);
        Assert.Equal(CpuTcpAssociationGap.NativeTableUnavailable, Evaluate(after: Native(true, af6: 50)).Reason);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void BothSocketDirectionsAreRequiredIncludingTheCollectorOwner(bool serverMissing)
    {
        var rows = Rows();
        Assert.Equal(serverMissing ? CpuTcpAssociationGap.ServerSocketMissing : CpuTcpAssociationGap.ReverseSocketMissing,
            Evaluate(after: Native(true, rows: [rows[serverMissing ? 1 : 0]])).Reason);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ProxyOrForeignOwnerCannotMatchOnTupleAlone(bool server)
    {
        var rows = Rows(); int index = server ? 0 : 1;
        rows[index] = rows[index] with { OwnerProcessId = 123 };
        Assert.Equal(CpuTcpAssociationGap.SocketOwnerMismatch, Evaluate(after: Native(true, rows: rows)).Reason);
    }

    [Fact]
    public void DuplicateMatchingRowsAreAmbiguousEvenWhenPidsAndStateAgree()
    {
        var rows = Rows();
        Assert.Equal(CpuTcpAssociationGap.SocketAmbiguous, Evaluate(after: Native(true, rows: [rows[0], rows[1], rows[0]])).Reason);
        rows[0] = rows[0] with { State = 2 };
        Assert.Equal(CpuTcpAssociationGap.SocketNotEstablished, Evaluate(after: Native(true, rows: rows)).Reason);
    }

    [Fact]
    public void MisorderedOrNonBracketingCapturesCannotAssociateAnotherCpuInterval()
    {
        Assert.Equal(CpuTcpAssociationGap.CaptureDoesNotBracketCpu, Evaluate(before: Native(mono: 16_000_000)).Reason);
        Assert.Equal(CpuTcpAssociationGap.CaptureDoesNotBracketCpu, Evaluate(after: Native(true, mono: 24_000_000)).Reason);
        Assert.Equal(CpuTcpAssociationGap.NativeClockBoundary, Evaluate(after: Native(true, mono: 10_000_000)).Reason);
        Assert.Equal(CpuTcpAssociationGap.NativeClockBoundary, Evaluate(afterCpu: Cpu(true, frequency: 1000)).Reason);
        Assert.Equal(CpuTcpAssociationGap.QueryBoundary, Evaluate(afterCpu: Cpu(true, query: Guid.NewGuid())).Reason);
    }

    [Fact]
    public void BackwardCpuUtcCannotBorrowAnOtherwiseForwardNativeMonotonicInterval()
    {
        Assert.Equal(CpuTcpAssociationGap.NativeClockBoundary,
            Evaluate(afterCpu: Cpu(true, observed: At.AddSeconds(1))).Reason);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ActualCpuUtcMustRemainInsideBothNativeSocketCaptures(bool before)
    {
        var result = before ? Evaluate(beforeCpu: Cpu(observed: At.AddSeconds(1).AddTicks(-1)))
            : Evaluate(afterCpu: Cpu(true, observed: At.AddSeconds(3).AddTicks(1)));
        Assert.Equal(CpuTcpAssociationGap.CaptureDoesNotBracketCpu, result.Reason);
        Assert.False(result.CandidateLocalAssociation);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void NativeUtcMustRemainInsideTheExactOuterSqlReceipts(bool before)
    {
        var result = before ? Evaluate(before: Native(observed: At.AddTicks(-1)))
            : Evaluate(after: Native(true, observed: At.AddSeconds(4).AddTicks(1)));
        Assert.Equal(CpuTcpAssociationGap.CaptureDoesNotBracketCpu, result.Reason);
    }

    [Fact]
    public void CompleteCaptureCannotExtendBeyondTheOwnedThirtySecondWindow()
    {
        Assert.True(Evaluate(afterSql: Sql(true, observed: At.AddSeconds(30))).CandidateLocalAssociation);
        var expired = Evaluate(afterSql: Sql(true, observed: At.AddSeconds(30).AddTicks(1)));
        Assert.Equal(CpuTcpAssociationGap.CaptureWindowExceeded, expired.Reason);
        Assert.False(expired.CandidateLocalAssociation);
    }

    [Fact]
    public void NativeMatchingReceiptsAreImmutableAndBoundedWithoutSilentTruncation()
    {
        var rows = Rows(); var frame = Native(rows: rows); rows[0] = rows[0] with { OwnerProcessId = 123 };
        Assert.True(Evaluate(before: frame).CandidateLocalAssociation);
        Assert.Throws<ArgumentException>(() => new CpuTcpNativeFrame(Binding(), At, 1, HostEvidence(), Epoch(), Epoch(ClientCreation),
            0, 0, 4096, 1, []));
        Assert.Throws<ArgumentException>(() => new CpuTcpNativeFrame(Binding(), At, 1, HostEvidence(), Epoch(), Epoch(ClientCreation),
            0, 0, 17, 0, Enumerable.Repeat(Rows()[0], 17).ToArray()));
        Assert.Throws<ArgumentException>(() => Connection(session: 0));
        Assert.Throws<ArgumentException>(() => Connection(id: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Connection(clientPid: 0));
    }

    [Fact]
    public void GenuineDualStackOrScopedTupleCanBeCandidateWithoutAnyHardwareClaim()
    {
        foreach (var connection in new[]
        {
            Connection(server: "::ffff:127.0.0.1", client: "127.0.0.1"),
            Connection(server: "fe80::1%9", client: "fe80::2%9"),
        })
        {
            var result = Evaluate(Sql(tcp: connection), Sql(true, tcp: connection),
                Native(rows: Rows(connection)), Native(true, rows: Rows(connection)));
            Assert.True(result.CandidateLocalAssociation); Assert.False(result.TargetComparisonAdmitted);
        }
    }
}
