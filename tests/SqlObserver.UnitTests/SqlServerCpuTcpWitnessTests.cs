using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerCpuTcpWitnessTests
{
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);
    private static readonly Guid OwnedSession = Guid.Parse("d1ed957b-1c7b-49eb-9e2b-34e322cae1b7");
    private static readonly Guid ClientConnection = Guid.Parse("ddf18696-970f-4aed-8ee9-6d9021cac560");
    private static readonly Guid ServerConnection = Guid.Parse("52a18f9a-d4a6-4a89-b33c-1769958f4900");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int CapturedCollectorPid = 1234567;

    private static object?[] Row() => [ServerLifecycleTestRows.At, "private-sql-node", "private-sql-instance", 42,
        ServerLifecycleTestRows.StartLocal, 100_000L, 101_000L, 100L, 200L, 16, "TCP",
        "127.0.0.1", 1433, "127.0.0.1", 51000, ServerConnection, 57, 2];

    private static ValueTask<SqlCpuTargetWitness> Parse(object?[] row, int ordinal = 1,
        int collectorPid = CapturedCollectorPid, CancellationToken cancellationToken = default) =>
        SqlServerCpuTargetWitnessReader.ParseTcpAsync(M4TestData.CreateExecutionRequest(), new SequentialReader([row]),
            Key, OwnedSession, ClientConnection, ordinal, collectorPid, cancellationToken);

    [Fact]
    public async Task CompleteNativeTupleRetainsIndependentConnectionIdsAndSourceOwnedCollectorPid()
    {
        object?[] row = Row(); row[7] = long.MaxValue; row[8] = 9_007_199_254_740_993L;
        var reader = new SequentialReader([row]);
        var request = M4TestData.CreateExecutionRequest();
        SqlCpuTargetWitness result = await SqlServerCpuTargetWitnessReader.ParseTcpAsync(request, reader, Key,
            OwnedSession, ClientConnection, 1, CapturedCollectorPid, CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 18), reader.FieldsRead.Distinct());
        Assert.Equal(request.TargetId, result.TargetId); Assert.Equal(request.TargetRevision, result.TargetRevision);
        Assert.Equal(request.RunId, result.RunId); Assert.Equal(OwnedSession, result.ConnectionSessionId);
        Assert.Equal(ClientConnection, result.ClientConnectionId);
        Assert.Equal(2, result.SourceSchemaVersion); Assert.Equal(1, result.Ordinal);
        Assert.Equal(42, result.Engine.ProcessId); Assert.Equal(long.MaxValue, result.ProcessUserTimeMs);
        Assert.Equal(9_007_199_254_740_993L, result.ProcessKernelTimeMs);
        var tcp = Assert.IsType<SqlCpuTcpConnectionEvidence>(result.TcpConnection);
        Assert.Equal(ServerConnection, tcp.ServerConnectionId); Assert.NotEqual(ClientConnection, tcp.ServerConnectionId);
        Assert.Equal(57, tcp.ServerSessionId); Assert.Equal(CapturedCollectorPid, tcp.CollectorProcessId);
        Assert.Equal(1433, tcp.ServerEndpoint!.Port); Assert.Equal(51000, tcp.ClientEndpoint!.Port);
        Assert.Equal(CpuTcpSocketCompleteness.Complete, tcp.Completeness);
        Assert.False(result.LocalWindowsProcessAssociationQualified); Assert.False(result.HardwareIdentityVerified);
        string json = JsonSerializer.Serialize(result, JsonOptions);
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyCurrentSessionSocketRowRemainsAbsentWithoutDroppingEngineEvidence()
    {
        object?[] row = Row(); Array.Fill(row, null, 11, 6);
        SqlCpuTargetWitness result = await Parse(row);
        Assert.Null(result.TcpConnection); Assert.Equal(SqlCpuPhysicalTransport.Tcp, result.PhysicalTransport);
        Assert.Equal(ServerLifecycleCoverage.Complete, result.Engine.Coverage);
        Assert.Equal(2, result.SourceSchemaVersion); Assert.False(result.LocalWindowsProcessAssociationQualified);
    }

    [Theory]
    [InlineData(15)] [InlineData(16)]
    public async Task MissingOneSqlConnectionIdentityCannotInventTheOther(int field)
    {
        object?[] row = Row(); Array.Fill(row, null, 11, 4); row[field] = null;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(row));
    }

    [Theory]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public async Task NetworkEvidenceWithoutSqlConnectionIdentityIsRejected(int retainedField)
    {
        object?[] row = Row(); object? value = row[retainedField]; Array.Fill(row, null, 11, 6); row[retainedField] = value;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(row));
    }

    [Theory]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public async Task KnownSessionWithIncompleteNetworkTupleDisclosesIncompleteSocketWitness(int field)
    {
        object?[] row = Row(); row[field] = null;
        SqlCpuTargetWitness result = await Parse(row);
        Assert.Equal(CpuTcpSocketCompleteness.Incomplete, result.TcpConnection!.Completeness);
        Assert.Equal(ServerLifecycleCoverage.Complete, result.Engine.Coverage);
        Assert.False(result.LocalWindowsProcessAssociationQualified);
    }

    [Theory]
    [InlineData("Named pipe", SqlCpuPhysicalTransport.NamedPipe)]
    [InlineData("Shared memory", SqlCpuPhysicalTransport.SharedMemory)]
    [InlineData(null, SqlCpuPhysicalTransport.Unknown)]
    public async Task NonTcpSourcePreservesEngineWitnessWithoutAClaimedSocket(string? transport, SqlCpuPhysicalTransport expected)
    {
        object?[] row = Row(); row[10] = transport; Array.Fill(row, null, 11, 6);
        SqlCpuTargetWitness result = await Parse(row);
        Assert.Equal(expected, result.PhysicalTransport); Assert.Null(result.TcpConnection);
        Assert.Equal(ServerLifecycleCoverage.Complete, result.Engine.Coverage); Assert.Equal(2, result.SourceSchemaVersion);
    }

    [Theory]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)]
    public async Task NonTcpTransportCannotBorrowAnyOtherConnectionsNetworkField(int field)
    {
        object?[] row = Row(); object? value = row[field]; row[10] = "Shared memory";
        Array.Fill(row, null, 11, 6); row[field] = value;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(row));
    }

    [Theory]
    [InlineData("sql-private-name")]
    [InlineData("127.1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1%3")]
    public async Task UnsupportedAddressNeverCausesDnsAliasOrLocalitySubstitution(string value)
    {
        object?[] row = Row(); row[11] = value;
        SqlCpuTargetWitness result = await Parse(row);
        Assert.Equal(CpuTcpSocketCompleteness.UnsupportedAddress, result.TcpConnection!.Completeness);
        Assert.Null(result.TcpConnection.ServerEndpoint);
        Assert.False(result.LocalWindowsProcessAssociationQualified);
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1", 4)]
    [InlineData("2001:db8::1", "2001:db8::2", 6)]
    [InlineData("fe80::1%9", "fe80::2%9", 6)]
    public async Task SupportedNumericFamilyAndScopeArePreservedAsCandidateEvidence(string server, string client, int family)
    {
        object?[] row = Row(); row[11] = server; row[13] = client;
        SqlCpuTargetWitness result = await Parse(row);
        var tcp = Assert.IsType<SqlCpuTcpConnectionEvidence>(result.TcpConnection);
        Assert.Equal(CpuTcpSocketCompleteness.Complete, tcp.Completeness);
        Assert.Equal(family, tcp.ServerEndpoint!.Family); Assert.Equal(family, tcp.ClientEndpoint!.Family);
        Assert.False(result.LocalWindowsProcessAssociationQualified);
    }

    [Theory]
    [InlineData(0)] [InlineData(32768)]
    public async Task InvalidNativeSqlSessionIdCannotEnterTypedOwnership(int sessionId)
    {
        object?[] row = Row(); row[16] = sessionId;
        await Assert.ThrowsAsync<ArgumentException>(async () => await Parse(row));
    }

    [Fact]
    public async Task EmptyNativeConnectionIdAndUnownedCollectorPidAreRejected()
    {
        object?[] row = Row(); row[15] = Guid.Empty;
        await Assert.ThrowsAsync<ArgumentException>(async () => await Parse(row));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Parse(Row(), collectorPid: 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public async Task UnsupportedOrMissingSchemaIsNotSilentlyParsedAsVersionOne(int schema)
    {
        object?[] row = Row(); row[17] = schema == 0 ? null : schema;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(row));
    }

    [Fact]
    public async Task ExtraMissingAndWrongWidthRowsCannotHideAmbiguousDmvOwnership()
    {
        foreach (var reader in new[] { new SequentialReader([]), new SequentialReader([Row(), Row()]), new SequentialReader([Row()], 17) })
            await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlServerCpuTargetWitnessReader.ParseTcpAsync(
                M4TestData.CreateExecutionRequest(), reader, Key, OwnedSession, ClientConnection, 1, CapturedCollectorPid, CancellationToken.None));
    }

    [Fact]
    public async Task CanceledSourceDoesNotReadAFieldOrReturnAUsableWitness()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var reader = new SequentialReader([Row()]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await SqlServerCpuTargetWitnessReader.ParseTcpAsync(
            M4TestData.CreateExecutionRequest(), reader, Key, OwnedSession, ClientConnection, 1, CapturedCollectorPid, cancellation.Token));
        Assert.Empty(reader.FieldsRead);
    }

    [Fact]
    public void PinnedVersionTwoReadsExactCurrentPhysicalSessionWithoutMaskingCardinality()
    {
        string query = SqlServerCpuTargetWitnessReader.LoadTcpQuery();
        Assert.Contains("sys.dm_exec_connections", query, StringComparison.Ordinal);
        Assert.Contains("@@SPID", query, StringComparison.Ordinal);
        Assert.Contains("parent_connection_id IS NULL", query, StringComparison.Ordinal);
        Assert.Contains("TSQL", query, StringComparison.Ordinal);
        string executableQuery = string.Join('\n', query.Split('\n').Where(static line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.DoesNotContain("TOP", executableQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host_process_id", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r", query, StringComparison.Ordinal);
        Assert.Equal("04AE4D76F6B750ED3112483A483636FBE29E2A42CD788478BBE7454B6A68B87C",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query))));
        string versionOne = SqlServerCpuTargetWitnessReader.LoadQuery();
        Assert.Equal("86C64863D16F99CF7FD2AC969356C11F307969B3A07A348BCBE8218B3ACFCE99",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionOne))));
    }

    // Rejects backwards reads as SqlDataReader SequentialAccess does; equal-ordinal null/value checks are allowed.
    private sealed class SequentialReader(object?[][] rows, int width = 18) : IOperationalHealthRowReader
    {
        private int row = -1, last = -1;
        internal List<int> FieldsRead { get; } = [];
        public int FieldCount => width;
        private object? Value(int ordinal)
        {
            if (ordinal < last) throw new InvalidDataException("Backwards sequential field access.");
            last = ordinal; FieldsRead.Add(ordinal); return rows[row][ordinal];
        }
        public bool IsDBNull(int ordinal) => Value(ordinal) is null;
        public int GetInt32(int ordinal) => (int)Value(ordinal)!;
        public long GetInt64(int ordinal) => (long)Value(ordinal)!;
        public Guid GetGuid(int ordinal) => (Guid)Value(ordinal)!;
        public DateTime GetDateTime(int ordinal) => (DateTime)Value(ordinal)!;
        public string GetString(int ordinal) => (string)Value(ordinal)!;
        public byte[] GetBinary(int ordinal) => throw new NotSupportedException();
        public bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public short GetInt16(int ordinal) => throw new NotSupportedException();
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); last = -1; return ValueTask.FromResult(++row < rows.Length); }
    }
}
