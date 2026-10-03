using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class ServerLifecycleParserTests
{
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);

    [Fact]
    public async Task SourceReceiptPreservesUtcAndLocalStartupWithoutInventingOccurrenceTime()
    {
        ServerLifecycleParseResult result = await Parse([ServerLifecycleTestRows.Engine(), ServerLifecycleTestRows.Member()]);
        var snapshot = result.Snapshot;
        Assert.Equal(M4TestData.RepositoryTime, snapshot.SourceObservedAtUtc);
        Assert.Equal(ServerLifecycleTestRows.Request.RunId, snapshot.RunId);
        Assert.Equal(ServerLifecycleCoverage.Complete, snapshot.EngineCoverage);
        Assert.Equal(ServerLifecycleCoverage.Complete, snapshot.AvailabilityCoverage);
        Assert.Equal(ServerLifecycleTestRows.StartLocal, snapshot.Engine.SourceStartLocal);
        Assert.Equal(DateTimeKind.Unspecified, snapshot.Engine.SourceStartLocal!.Value.Kind);
        Assert.Null(snapshot.Engine.OccurredAtUtc);
        Assert.Equal(101_000L, snapshot.Engine.CurrentMsTicks);
        Assert.Equal(100_000L, snapshot.Engine.StartMsTicks);
        Assert.Equal(2, snapshot.SourceRowsRead);
        Assert.False(result.Loss.HasLoss);
        Assert.InRange(result.ResponseBytes, snapshot.EstimatedSizeBytes, 1_048_576);
        var member = Assert.Single(snapshot.Members);
        Assert.Equal(member.ReplicaFingerprint, member.ObserverReplicaFingerprint);
        Assert.Equal("PRIMARY", member.Role);
    }

    [Fact]
    public async Task IdentityIsKeyedDomainSeparatedAndStartupDoesNotChangeWithCurrentTicks()
    {
        object?[] engine = ServerLifecycleTestRows.Engine();
        engine[2] = "same-private-identity"; engine[3] = "same-private-identity";
        object?[] member = ServerLifecycleTestRows.Member(); member[2] = engine[2]; member[3] = engine[3];
        var first = await Parse([engine, member]);
        object?[] later = (object?[])engine.Clone(); later[7] = 102_000L;
        object?[] laterMember = (object?[])member.Clone(); laterMember[7] = later[7];
        var second = await Parse([later, laterMember]);
        var changedKey = await SqlServerLifecycleParser.ParseAsync(ServerLifecycleTestRows.Request,
            new ServerLifecycleTestRows.Reader([engine, member]),
            new IdentityFingerprintKey(Enumerable.Repeat((byte)7, 32).ToArray()), CancellationToken.None);
        Assert.NotEqual("same-private-identity", first.Snapshot.Engine.PhysicalNodeFingerprint);
        Assert.NotEqual(first.Snapshot.Engine.PhysicalNodeFingerprint, first.Snapshot.Engine.LogicalInstanceFingerprint);
        Assert.Equal(first.Snapshot.Engine.StartupIncarnationFingerprint, second.Snapshot.Engine.StartupIncarnationFingerprint);
        Assert.NotEqual(first.Snapshot.Engine.PhysicalNodeFingerprint, changedKey.Snapshot.Engine.PhysicalNodeFingerprint);
        Assert.NotEqual(first.Snapshot.Engine.StartupIncarnationFingerprint, changedKey.Snapshot.Engine.StartupIncarnationFingerprint);
        Assert.NotEqual(Assert.Single(first.Snapshot.Members).GroupFingerprint, Assert.Single(first.Snapshot.Members).ReplicaFingerprint);
    }

    [Fact]
    public async Task MissingIdentityAndUnknownRoleRemainExplicitlyIncomplete()
    {
        object?[] engine = ServerLifecycleTestRows.Engine(); engine[2] = null;
        object?[] member = ServerLifecycleTestRows.Member(); member[2] = null;
        member[11] = null; member[12] = null; member[13] = null; member[14] = false;
        var result = await Parse([engine, member]);
        Assert.Null(result.Snapshot.Engine.PhysicalNodeFingerprint);
        Assert.Null(result.Snapshot.Engine.StartupIncarnationFingerprint);
        Assert.Equal(ServerLifecycleCoverage.Unknown, result.Snapshot.EngineCoverage);
        Assert.NotEqual(ServerLifecycleCoverage.Complete, result.Snapshot.AvailabilityCoverage);
        var actual = Assert.Single(result.Snapshot.Members);
        Assert.Null(actual.Role); Assert.Null(actual.ObserverReplicaFingerprint); Assert.False(actual.StateObserved);
        Assert.True(result.Loss.HasLoss);
    }

    [Fact]
    public async Task StandaloneEngineDoesNotInventAnAvailabilityGroup()
    {
        var result = await Parse([ServerLifecycleTestRows.Engine(hadr: false)]);
        Assert.Empty(result.Snapshot.Members);
        Assert.False(result.Snapshot.IsHadrEnabled);
        Assert.Equal(ServerLifecycleCoverage.Complete, result.Snapshot.EngineCoverage);
        Assert.False(result.Loss.HasLoss);
    }

    [Fact]
    public async Task NullableStartupPiecesStayUnknownWithoutSubstitution()
    {
        foreach (int ordinal in new[] { 2, 3, 4, 5, 6, 7 })
        {
            object?[] header = ServerLifecycleTestRows.Engine(hadr: false); header[ordinal] = null;
            var result = await Parse([header]);
            Assert.Equal(ServerLifecycleCoverage.Unknown, result.Snapshot.EngineCoverage);
            Assert.True(result.Loss.HasLoss);
            Assert.Null(result.Snapshot.Engine.OccurredAtUtc);
            if (ordinal != 7) Assert.Null(result.Snapshot.Engine.StartupIncarnationFingerprint);
        }
    }

    [Fact]
    public async Task SourceUtcRejectsLocalAndSubmicrosecondValuesAndStartupRejectsUtcKind()
    {
        foreach (DateTime at in new[] { DateTime.SpecifyKind(ServerLifecycleTestRows.At, DateTimeKind.Local), ServerLifecycleTestRows.At.AddTicks(1) })
        {
            object?[] header = ServerLifecycleTestRows.Engine(hadr: false); header[1] = at;
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([header]));
        }
        object?[] startup = ServerLifecycleTestRows.Engine(hadr: false);
        startup[5] = DateTime.SpecifyKind(ServerLifecycleTestRows.StartLocal, DateTimeKind.Utc);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([startup]));
        object?[] unspecifiedUtc = ServerLifecycleTestRows.Engine(hadr: false);
        unspecifiedUtc[1] = DateTime.SpecifyKind(ServerLifecycleTestRows.At, DateTimeKind.Unspecified);
        Assert.Equal(M4TestData.RepositoryTime, (await Parse([unspecifiedUtc])).Snapshot.SourceObservedAtUtc);
    }

    [Fact]
    public async Task RawMembershipOrderingAndEmptyIdentifiersAreRejected()
    {
        object?[] later = ServerLifecycleTestRows.Member(expected: 2, replica: Guid.Parse("22222222-2222-4222-8222-222222222222"));
        later[12] = "SECONDARY";
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(
            [ServerLifecycleTestRows.Engine(), later, ServerLifecycleTestRows.Member(expected: 2)]));
        foreach (int ordinal in new[] { 9, 10, 11 })
        {
            object?[] member = ServerLifecycleTestRows.Member(); member[ordinal] = Guid.Empty;
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([ServerLifecycleTestRows.Engine(), member]));
        }
    }

    [Fact]
    public async Task HeaderIsRequiredUniqueAndMustPrecedeMembers()
    {
        foreach (object?[][] rows in new[]
        {
            Array.Empty<object?[]>(), new[] { ServerLifecycleTestRows.Member() },
            new[] { ServerLifecycleTestRows.Engine(), ServerLifecycleTestRows.Engine() },
            new[] { ServerLifecycleTestRows.Member(), ServerLifecycleTestRows.Engine() },
        })
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(rows));
    }

    [Fact]
    public async Task RepeatedCaptureFieldsCannotMixSourceReceipts()
    {
        foreach (int ordinal in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 16 })
        {
            object?[] member = ServerLifecycleTestRows.Member();
            member[ordinal] = ordinal switch
            {
                1 => ServerLifecycleTestRows.At.AddSeconds(1), 2 or 3 => "another-private-source",
                4 => 43, 5 => ServerLifecycleTestRows.StartLocal.AddSeconds(1),
                6 => 99_000L, 7 => 102_000L, 8 => false, _ => 2,
            };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([ServerLifecycleTestRows.Engine(), member]));
        }
    }

    [Fact]
    public async Task MalformedTypesBoundsAndDuplicateMembershipRejectTheEntireScan()
    {
        foreach ((int Ordinal, object? Value) invalid in new (int, object?)[]
        {
            (0, 2), (1, null), (2, ""), (3, "bad\nidentity"), (4, -1),
            (6, -1L), (16, 2),
        })
        {
            object?[] header = ServerLifecycleTestRows.Engine(); header[invalid.Ordinal] = invalid.Value;
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([header]));
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(
            [ServerLifecycleTestRows.Engine(), ServerLifecycleTestRows.Member(), ServerLifecycleTestRows.Member()]));
        var wrongShape = new ServerLifecycleTestRows.Reader([ServerLifecycleTestRows.Engine()], fieldCount: 16);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlServerLifecycleParser.ParseAsync(
            ServerLifecycleTestRows.Request, wrongShape, Key, CancellationToken.None));
    }

    [Fact]
    public async Task InconsistentUptimeAndLargeConfiguredMembershipRemainPartialEvidence()
    {
        object?[] engine = ServerLifecycleTestRows.Engine(); engine[7] = 99_999L;
        object?[] member = ServerLifecycleTestRows.Member(expected: int.MaxValue); member[7] = engine[7];
        var result = await Parse([engine, member]);
        Assert.Equal(ServerLifecycleCoverage.Partial, result.Snapshot.EngineCoverage);
        Assert.Equal(ServerLifecycleCoverage.Partial, result.Snapshot.AvailabilityCoverage);
        Assert.Equal(99_999L, result.Snapshot.Engine.CurrentMsTicks);
        Assert.Equal(int.MaxValue, Assert.Single(result.Snapshot.Members).ExpectedGroupMembers);
        Assert.True(result.Loss.HasLoss);
    }

    [Fact]
    public async Task OneLookaheadStopsTheSourceAndDisclosesMembershipLoss()
    {
        object?[] first = ServerLifecycleTestRows.Member(expected: 2);
        object?[] second = ServerLifecycleTestRows.Member(expected: 2, replica: Guid.Parse("22222222-2222-4222-8222-222222222222"));
        second[12] = "SECONDARY";
        var reader = new ServerLifecycleTestRows.Reader([ServerLifecycleTestRows.Engine(), first, second, second]);
        var result = await SqlServerLifecycleParser.ParseAsync(ServerLifecycleTestRows.Request, reader, Key,
            CancellationToken.None, maximumRows: 2);
        Assert.Equal(3, reader.RowsRead);
        Assert.True(result.Snapshot.Truncated);
        Assert.NotEqual(ServerLifecycleCoverage.Complete, result.Snapshot.AvailabilityCoverage);
        Assert.Equal(CollectorLossKind.SourceRowLimit, result.Loss.Kind);
        Assert.Equal(1, result.Loss.MinimumLostItems);
    }

    [Fact]
    public async Task ByteLimitKeepsOnlyTheEngineAndWithholdsMembershipPrefix()
    {
        var header = await Parse([ServerLifecycleTestRows.Engine()]);
        int bytes = header.Snapshot.EstimatedSizeBytes;
        var result = await SqlServerLifecycleParser.ParseAsync(ServerLifecycleTestRows.Request,
            new ServerLifecycleTestRows.Reader([ServerLifecycleTestRows.Engine(), ServerLifecycleTestRows.Member()]),
            Key, CancellationToken.None, maximumResponseBytes: bytes);
        Assert.Empty(result.Snapshot.Members);
        Assert.True(result.Snapshot.Truncated);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, result.Loss.Kind);
        Assert.NotEqual(ServerLifecycleCoverage.Complete, result.Snapshot.AvailabilityCoverage);
    }

    [Fact]
    public async Task CancellationStopsReadingWithoutPublishingAPrefix()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ServerLifecycleTestRows.Reader([ServerLifecycleTestRows.Engine(), ServerLifecycleTestRows.Member()])
        { BeforeRead = index => { if (index == 1) cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await SqlServerLifecycleParser.ParseAsync(
            ServerLifecycleTestRows.Request, reader, Key, cancellation.Token));
        Assert.Equal(1, reader.RowsRead);
    }

    private static ValueTask<ServerLifecycleParseResult> Parse(object?[][] rows) =>
        SqlServerLifecycleParser.ParseAsync(ServerLifecycleTestRows.Request, new ServerLifecycleTestRows.Reader(rows), Key, CancellationToken.None);
}

internal static class ServerLifecycleTestRows
{
    internal static readonly CollectorExecutionRequest Request = M4TestData.CreateExecutionRequest();
    internal static readonly DateTime At = M4TestData.RepositoryTime.UtcDateTime;
    internal static readonly DateTime StartLocal = new(2026, 8, 23, 13, 59, 0, DateTimeKind.Unspecified);
    internal static readonly Guid Group = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    internal static readonly Guid Replica = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static object?[] Engine(bool hadr = true) =>
        [0, At, "private-node", "private-instance", 42, StartLocal, 100_000L, 101_000L, hadr, null, null, null, null, null, null, null, 1];
    internal static object?[] Member(int expected = 1, Guid? replica = null) =>
        [1, At, "private-node", "private-instance", 42, StartLocal, 100_000L, 101_000L, true,
         Group, replica ?? Replica, Replica, "PRIMARY", "PRIMARY", true, expected, 1];
    internal sealed class Reader(object?[][] rows, int fieldCount = 17) : IOperationalHealthRowReader
    {
        private int index = -1;
        internal int RowsRead { get; private set; }
        internal Action<int>? BeforeRead { get; init; }
        public int FieldCount => fieldCount;
        public bool IsDBNull(int ordinal) => rows[index][ordinal] is null;
        public byte[] GetBinary(int ordinal) => (byte[])rows[index][ordinal]!;
        public bool GetBoolean(int ordinal) => (bool)rows[index][ordinal]!;
        public DateTime GetDateTime(int ordinal) => (DateTime)rows[index][ordinal]!;
        public Guid GetGuid(int ordinal) => (Guid)rows[index][ordinal]!;
        public short GetInt16(int ordinal) => (short)rows[index][ordinal]!;
        public int GetInt32(int ordinal) => (int)rows[index][ordinal]!;
        public long GetInt64(int ordinal) => (long)rows[index][ordinal]!;
        public string GetString(int ordinal) => (string)rows[index][ordinal]!;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke(index + 1); cancellationToken.ThrowIfCancellationRequested();
            bool present = ++index < rows.Length; if (present) RowsRead++;
            return ValueTask.FromResult(present);
        }
    }
}
