using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class AvailabilityGroupV2ParserTests
{
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);

    [Fact]
    public async Task PrimaryPreservesRemoteNullsFaultsUnitsAndOffsetFreeCommitTime()
    {
        object?[] remote = Replica(0x22);
        remote[8] = null; // A disconnected remote role can be unknown.
        remote[10] = "DISCONNECTED";
        object?[] localDatabase = Database(0x11);
        localDatabase[15] = null;
        localDatabase[19] = new DateTime(2026, 9, 29, 8, 15, 0).AddTicks(7);
        object?[] remoteDatabase = Database(0x22);
        remoteDatabase[12] = "NOT_HEALTHY";
        remoteDatabase[13] = "SUSPECT";
        remoteDatabase[14] = true;
        remoteDatabase[15] = 0L;
        remoteDatabase[16] = 123L;
        remoteDatabase[17] = long.MaxValue;
        remoteDatabase[18] = 456L;

        AvailabilityGroupV2ParseResult result = await Parse([Replica(0x11), remote, localDatabase, remoteDatabase]);

        Assert.Equal(OperationalObservationState.Complete, result.Snapshot.State);
        Assert.False(result.Loss.HasLoss);
        Assert.InRange(result.ResponseBytes, result.Snapshot.EstimatedSizeBytes, 2_097_152);
        AvailabilityReplicaEvidence secondary = Assert.Single(result.Snapshot.Replicas,
            static item => item.Identity.ReplicaIsLocal == false);
        Assert.Null(secondary.OperationalState);
        Assert.Null(secondary.Role);
        Assert.Equal("DISCONNECTED", secondary.ConnectedState);
        Assert.Equal(2, result.Snapshot.Databases.Select(static item => item.Identity.ReplicaFingerprint).Distinct().Count());
        Assert.Single(result.Snapshot.Databases.Select(static item => item.DatabaseFingerprint).Distinct());
        AvailabilityDatabaseReplicaEvidence local = Assert.Single(result.Snapshot.Databases,
            static item => item.Identity.ReplicaIsLocal == true);
        Assert.Null(local.LogSendQueueKb);
        Assert.Equal(new DateTime(2026, 9, 29, 8, 15, 0), local.SourceLastCommitLocal);
        Assert.Equal(DateTimeKind.Unspecified, local.SourceLastCommitLocal!.Value.Kind);
        AvailabilityDatabaseReplicaEvidence fault = Assert.Single(result.Snapshot.Databases,
            static item => item.Identity.ReplicaIsLocal == false);
        Assert.Equal("SUSPECT", fault.DatabaseState);
        Assert.Equal("NOT_HEALTHY", fault.SynchronizationHealth);
        Assert.True(fault.IsSuspended);
        Assert.Equal(0, fault.LogSendQueueKb);
        Assert.Equal(123, fault.LogSendRateKbPerSecond);
        Assert.Equal(long.MaxValue, fault.RedoQueueKb);
        Assert.Equal(456, fault.RedoRateKbPerSecond);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AvailabilityDatabaseReplicaEvidence>)result.Snapshot.Databases).Clear());
    }

    [Fact]
    public async Task SecondaryRetainsUnobservedConfiguredRowsAndReportsLimitedCoverage()
    {
        object?[] local = Replica(0x11, scope: 2);
        local[8] = "SECONDARY";
        object?[] remote = Replica(0x22, scope: 2, observed: false);
        object?[] localDatabase = Database(0x11, scope: 2);
        object?[] remoteDatabase = Database(0x22, scope: 2, observed: false);

        AvailabilityGroupV2ParseResult result = await Parse([local, remote, localDatabase, remoteDatabase]);

        Assert.Equal(OperationalObservationState.Degraded, result.Snapshot.State);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, result.Loss.Kind);
        Assert.Equal(4, result.Snapshot.SourceRowsRead);
        Assert.All(result.Snapshot.Replicas, static item =>
            Assert.Equal(AvailabilityVisibilityScope.SecondaryLocalOnly, item.Identity.VisibilityScope));
        AvailabilityDatabaseReplicaEvidence unavailable = Assert.Single(result.Snapshot.Databases,
            static item => !item.StateObserved);
        Assert.Null(unavailable.SynchronizationState);
        Assert.Null(unavailable.LogSendQueueKb);
    }

    [Fact]
    public async Task MissingObserverStaysResolvingAndDoesNotInventLocality()
    {
        object?[] row = Replica(0x11, scope: 3, observed: false);
        row[4] = null;
        row[5] = null;
        AvailabilityGroupV2ParseResult result = await Parse([row]);
        AvailabilityReplicaEvidence item = Assert.Single(result.Snapshot.Replicas);
        Assert.Null(item.Identity.ObserverReplicaFingerprint);
        Assert.Null(item.Identity.ReplicaIsLocal);
        Assert.Equal(OperationalObservationState.Degraded, result.Snapshot.State);
    }

    [Fact]
    public async Task IdentityDomainsAndSecretsAreSeparatedButObserverMatchesReplica()
    {
        object?[] replica = Replica(0x11);
        object?[] database = Database(0x11);
        replica[1] = Digest(0x11);
        database[1] = Digest(0x11);
        database[3] = Digest(0x11);
        AvailabilityGroupV2ParseResult first = await Parse([replica, database]);
        AvailabilityDatabaseReplicaEvidence item = Assert.Single(first.Snapshot.Databases);
        Assert.NotEqual(item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint);
        Assert.NotEqual(item.Identity.ReplicaFingerprint, item.DatabaseFingerprint);
        Assert.Equal(item.Identity.ReplicaFingerprint, item.Identity.ObserverReplicaFingerprint);
        Assert.NotEqual(Convert.ToHexString(Digest(0x11)).ToLowerInvariant(), item.Identity.ReplicaFingerprint);
        AvailabilityGroupV2ParseResult changed = await SqlServerAvailabilityGroupV2Parser.ParseAsync(
            M4TestData.CreateExecutionRequest(), new SequentialReader([replica, database]),
            new IdentityFingerprintKey(Enumerable.Repeat((byte)7, 32).ToArray()),
            M4TestData.RepositoryTime, CancellationToken.None);
        Assert.NotEqual(item.DatabaseFingerprint, Assert.Single(changed.Snapshot.Databases).DatabaseFingerprint);
    }

    [Fact]
    public async Task LookaheadAndByteBoundsExposeLossWithoutCompletenessClaims()
    {
        object?[][] rows = [Replica(0x11), Replica(0x22), Database(0x11), Database(0x22)];
        var reader = new SequentialReader(rows);
        AvailabilityGroupV2ParseResult capped = await SqlServerAvailabilityGroupV2Parser.ParseAsync(
            M4TestData.CreateExecutionRequest(), reader, Key, M4TestData.RepositoryTime,
            CancellationToken.None, maximumRows: 2);
        Assert.Equal(3, reader.RowsRead);
        Assert.Equal(2, capped.Snapshot.Replicas.Count);
        Assert.Empty(capped.Snapshot.Databases);
        Assert.True(capped.Snapshot.Truncated);
        Assert.Equal(OperationalObservationState.Partial, capped.Snapshot.State);
        Assert.Equal(CollectorLossKind.SourceRowLimit, capped.Loss.Kind);
        Assert.InRange(capped.ResponseBytes, capped.Snapshot.EstimatedSizeBytes, 2_097_152);

        AvailabilityGroupV2ParseResult bytes = await SqlServerAvailabilityGroupV2Parser.ParseAsync(
            M4TestData.CreateExecutionRequest(), new SequentialReader(rows), Key, M4TestData.RepositoryTime,
            CancellationToken.None, maximumResponseBytes: 1000);
        Assert.Empty(bytes.Snapshot.Replicas);
        Assert.Empty(bytes.Snapshot.Databases);
        Assert.Equal(2, bytes.Snapshot.SourceRowsRead);
        Assert.Equal(2, bytes.Loss.MinimumLostItems);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, bytes.Loss.Kind);
        Assert.Equal(OperationalObservationState.Partial, bytes.Snapshot.State);
        Assert.InRange(bytes.ResponseBytes, bytes.Snapshot.EstimatedSizeBytes, 1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task InconsistentOrUnreviewedSourceEvidenceFailsClosed(int mutation)
    {
        object?[] replica = Replica(0x11);
        object?[] database = Database(0x11);
        switch (mutation)
        {
            case 0: replica[0] = 2; break;
            case 1: replica[1] = new byte[31]; break;
            case 2: replica[5] = false; break;
            case 3: replica[6] = (short)4; break;
            case 4: replica[8] = "UNREVIEWED_STATE"; break;
            case 5: database[15] = -1L; break;
            case 6: database[19] = DateTime.SpecifyKind(new DateTime(2026, 9, 29), DateTimeKind.Utc); break;
            case 7: database[7] = false; break;
            case 8: replica[3] = Digest(0xdd); break;
            case 9: database[2] = Digest(0x22); database[5] = false; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([replica, database]));
    }

    [Fact]
    public async Task DuplicateUnorderedAndWrongShapeRowsAreRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([Replica(0x11), Replica(0x11)]));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([Replica(0x22), Replica(0x11)]));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlServerAvailabilityGroupV2Parser.ParseAsync(
            M4TestData.CreateExecutionRequest(), new SequentialReader([], fieldCount: 19), Key,
            M4TestData.RepositoryTime, CancellationToken.None));
    }

    [Fact]
    public async Task MissingExpectedStateFieldsDegradeCoverageAndSnapshotsOwnTheirLists()
    {
        object?[] replica = Replica(0x11);
        replica[9] = null;
        AvailabilityGroupV2ParseResult missingOperation = await Parse([replica]);
        Assert.Equal(OperationalObservationState.Degraded, missingOperation.Snapshot.State);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, missingOperation.Loss.Kind);

        object?[] database = Database(0x11);
        database[12] = null;
        AvailabilityGroupV2ParseResult missingHealth = await Parse([Replica(0x11), database]);
        Assert.Equal(OperationalObservationState.Degraded, missingHealth.Snapshot.State);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, missingHealth.Loss.Kind);

        var replicas = missingHealth.Snapshot.Replicas.ToList();
        var databases = missingHealth.Snapshot.Databases.ToList();
        var copied = new AvailabilityGroupsV2Snapshot(M4TestData.TargetId, M4TestData.TargetRevision,
            M4TestData.CreateExecutionRequest().RunId, M4TestData.RepositoryTime, replicas, databases, 2, false);
        replicas.Clear();
        databases.Clear();
        Assert.Single(copied.Replicas);
        Assert.Single(copied.Databases);
    }

    [Fact]
    public async Task InconsistentGroupObserversAreRejectedAndCancellationStopsTheScan()
    {
        object?[] remote = Replica(0x22);
        remote[4] = Digest(0x22);
        remote[5] = true;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse([Replica(0x11), remote]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SqlServerAvailabilityGroupV2Parser.ParseAsync(M4TestData.CreateExecutionRequest(),
                new SequentialReader([Replica(0x11)]), Key, M4TestData.RepositoryTime, cancelled.Token));
    }

    [Fact]
    public async Task EmptyScanIsNoDataAndV1ValidatorCannotPublishV2Evidence()
    {
        AvailabilityGroupV2ParseResult empty = await Parse([]);
        Assert.Equal(OperationalObservationState.NoData, empty.Snapshot.State);
        Assert.False(empty.Loss.HasLoss);
        Assert.Equal(empty.Snapshot.EstimatedSizeBytes, empty.ResponseBytes);
        AvailabilityGroupV2ParseResult parsed = await Parse([Replica(0x11)]);
        var collector = new SqlServerAvailabilityGroupsHealthCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(parsed.Snapshot,
            parsed.Snapshot.Replicas.Count, parsed.Snapshot.EstimatedSizeBytes));
        var result = new CollectorExecutionResult(M4TestData.TargetId, M4TestData.TargetRevision,
            collector.Manifest.Id, 1, 1, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            payload, new CollectorRunAccounting(1, 1, parsed.ResponseBytes, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        var validator = new CollectorOutputValidator(new CollectorOutputContract(new CollectorOutputSchemaVersion(1),
            [], 0, 0, 0, maxOperationalHealthObservations: 2048));
        Assert.Throws<InvalidDataException>(() => validator.Validate(collector.Manifest, M4TestData.CreateExecutionRequest(), result));
    }

    private static ValueTask<AvailabilityGroupV2ParseResult> Parse(object?[][] rows) =>
        SqlServerAvailabilityGroupV2Parser.ParseAsync(M4TestData.CreateExecutionRequest(),
            new SequentialReader(rows), Key, M4TestData.RepositoryTime, CancellationToken.None);

    private static byte[] Digest(byte value) => Enumerable.Repeat(value, 32).ToArray();
    private static object?[] Replica(byte replica, short scope = 1, bool observed = true) =>
    [
        0, Digest(0xaa), Digest(replica), null, Digest(0x11), replica == 0x11,
        scope, observed, observed ? replica == 0x11 ? "PRIMARY" : "SECONDARY" : null,
        observed && replica == 0x11 ? "ONLINE" : null, observed ? "CONNECTED" : null,
        null, null, null, null, null, null, null, null, null,
    ];
    private static object?[] Database(byte replica, short scope = 1, bool observed = true) =>
    [
        1, Digest(0xaa), Digest(replica), Digest(0xdd), Digest(0x11), replica == 0x11,
        scope, observed, null, null, null, observed ? "SYNCHRONIZED" : null,
        observed ? "HEALTHY" : null, observed ? "ONLINE" : null, observed ? false : null,
        null, null, null, null, null,
    ];

    private sealed class SequentialReader(object?[][] rows, int fieldCount = 20) : IOperationalHealthRowReader
    {
        private int index = -1, ordinal = -1;
        public int RowsRead { get; private set; }
        public int FieldCount => fieldCount;
        private object? Value(int next)
        {
            if (next < ordinal) throw new InvalidOperationException("The parser moved backwards in a sequential source row.");
            ordinal = next;
            return rows[index][next];
        }
        public bool IsDBNull(int next) => Value(next) is null;
        public byte[] GetBinary(int next) => (byte[])Value(next)!;
        public bool GetBoolean(int next) => (bool)Value(next)!;
        public DateTime GetDateTime(int next) => (DateTime)Value(next)!;
        public Guid GetGuid(int next) => (Guid)Value(next)!;
        public short GetInt16(int next) => (short)Value(next)!;
        public int GetInt32(int next) => (int)Value(next)!;
        public long GetInt64(int next) => (long)Value(next)!;
        public string GetString(int next) => (string)Value(next)!;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal = -1;
            bool read = ++index < rows.Length;
            if (read) RowsRead++;
            return ValueTask.FromResult(read);
        }
    }
}
