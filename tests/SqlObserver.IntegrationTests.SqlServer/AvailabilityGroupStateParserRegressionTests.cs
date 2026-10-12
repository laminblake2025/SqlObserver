using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class AvailabilityGroupStateParserRegressionTests
{
    // Expected vocabularies come from the documented DMV columns projected by the SQL assets:
    // https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-hadr-availability-replica-states-transact-sql
    // https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-hadr-database-replica-states-transact-sql
    [Theory]
    [InlineData(3, "PRIMARY")]
    [InlineData(3, "SECONDARY")]
    [InlineData(3, "RESOLVING")]
    [InlineData(4, "PENDING_FAILOVER")]
    [InlineData(4, "PENDING")]
    [InlineData(4, "ONLINE")]
    [InlineData(4, "OFFLINE")]
    [InlineData(4, "FAILED")]
    [InlineData(4, "FAILED_NO_QUORUM")]
    [InlineData(5, "CONNECTED")]
    [InlineData(5, "DISCONNECTED")]
    [InlineData(8, "NOT SYNCHRONIZING")]
    [InlineData(8, "SYNCHRONIZING")]
    [InlineData(8, "SYNCHRONIZED")]
    [InlineData(8, "REVERTING")]
    [InlineData(8, "INITIALIZING")]
    [InlineData(9, "ONLINE")]
    [InlineData(9, "RESTORING")]
    [InlineData(9, "RECOVERING")]
    [InlineData(9, "RECOVERY_PENDING")]
    [InlineData(9, "SUSPECT")]
    [InlineData(9, "EMERGENCY")]
    [InlineData(9, "OFFLINE")]
    public async Task ProjectedStatesRetainTheirDocumentedMeaning(int ordinal, string state)
    {
        object?[] row = CreateRow(ordinal < 6 ? 0 : 1);
        row[ordinal] = state;

        AvailabilityGroupsSnapshot snapshot = await ParseAsync(row);

        Assert.Equal(state, ReadState(snapshot, ordinal));
        Assert.Equal(OperationalObservationState.Complete, snapshot.State);
        Assert.Equal(AvailabilityVisibilityScope.PrimaryAllKnown, snapshot.VisibilityScope);
        Assert.All(snapshot.Replicas, replica => Assert.True(replica.StateAvailable));
        Assert.All(snapshot.Databases, database => Assert.True(database.StateAvailable));
    }

    [Theory]
    [InlineData(3, "ONLINE")]
    [InlineData(4, "PRIMARY")]
    [InlineData(5, "NOT_CONNECTED")]
    [InlineData(5, "FAILED")]
    [InlineData(8, "RECOVERING")]
    [InlineData(8, "NOT_HEALTHY")]
    [InlineData(9, "SYNCHRONIZED")]
    [InlineData(9, "FAILED")]
    [InlineData(3, "future-state")]
    [InlineData(4, "future-state")]
    [InlineData(5, "future-state")]
    [InlineData(8, "future-state")]
    [InlineData(9, "future-state")]
    [InlineData(3, null)]
    [InlineData(4, null)]
    [InlineData(5, null)]
    [InlineData(8, null)]
    [InlineData(9, null)]
    public async Task NullOrForeignColumnStatesRemainUnknown(int ordinal, string? state)
    {
        object?[] row = CreateRow(ordinal < 6 ? 0 : 1);
        row[ordinal] = state;

        AvailabilityGroupsSnapshot snapshot = await ParseAsync(row);

        Assert.Equal("unknown", ReadState(snapshot, ordinal));
    }

    [Theory]
    [InlineData(true, OperationalObservationState.Complete)]
    [InlineData(false, OperationalObservationState.Degraded)]
    public async Task StateAvailabilityDescribesVisibilityIndependentlyOfFaultStates(bool available, OperationalObservationState expected)
    {
        object?[] replica = CreateRow(0);
        replica[3] = "RESOLVING";
        replica[4] = "FAILED_NO_QUORUM";
        replica[5] = "DISCONNECTED";
        replica[6] = available;
        replica[10] = (short)AvailabilityVisibilityScope.ResolvingLocalOnly;
        object?[] database = CreateRow(1);
        database[8] = "NOT SYNCHRONIZING";
        database[9] = "SUSPECT";
        database[10] = (short)AvailabilityVisibilityScope.ResolvingLocalOnly;

        AvailabilityGroupsSnapshot snapshot = await ParseAsync(replica, database);

        Assert.Equal("FAILED_NO_QUORUM", Assert.Single(snapshot.Replicas).OperationalState);
        Assert.Equal("SUSPECT", Assert.Single(snapshot.Databases).DatabaseState);
        Assert.Equal(available, snapshot.Replicas[0].StateAvailable);
        Assert.True(snapshot.Databases[0].StateAvailable);
        Assert.Equal(expected, snapshot.State);
        Assert.Equal(AvailabilityVisibilityScope.ResolvingLocalOnly, snapshot.VisibilityScope);
    }

    private static async Task<AvailabilityGroupsSnapshot> ParseAsync(params object?[][] rows)
    {
        var collector = new SqlServerAvailabilityGroupsHealthCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        MonitoredInstanceId target = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        ObservationTargetRevision revision = new(1);
        DateTimeOffset checkedAt = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        var profile = new CapabilityProfile(
            target, revision, new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(16, 0, 1000, 0), new SqlServerEditionName("Enterprise Edition"), SqlServerEngineEdition.Enterprise, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true, isSysAdmin: false, capabilities: [], permissions: [], TimeSpan.FromMilliseconds(1), 64, checkedAt, checkedAt.AddMinutes(5));
        var request = new CollectorExecutionRequest(
            new CollectorRunId(Guid.NewGuid()), target, revision,
            new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql.test.example"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))),
            profile, new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));

        var result = await collector.ReadRowsForTestAsync(request, new SequentialRowReader(rows), CancellationToken.None);

        Assert.Equal(rows.Length, result.Rows);
        Assert.Equal(rows.Length, result.Items);
        Assert.False(result.Loss.HasLoss);
        return Assert.IsType<AvailabilityGroupsSnapshot>(result.Snapshot);
    }

    private static object?[] CreateRow(int kind) =>
    [
        kind,
        Enumerable.Repeat((byte)1, 32).ToArray(),
        Enumerable.Repeat((byte)2, 32).ToArray(),
        "PRIMARY", "ONLINE", "CONNECTED", true,
        kind == 1 ? Enumerable.Repeat((byte)3, 32).ToArray() : null,
        "SYNCHRONIZED", "ONLINE", (short)AvailabilityVisibilityScope.PrimaryAllKnown,
    ];

    private static string ReadState(AvailabilityGroupsSnapshot snapshot, int ordinal) => ordinal switch
    {
        3 => Assert.Single(snapshot.Replicas).Role,
        4 => Assert.Single(snapshot.Replicas).OperationalState,
        5 => Assert.Single(snapshot.Replicas).ConnectedState,
        8 => Assert.Single(snapshot.Databases).SynchronizationState,
        9 => Assert.Single(snapshot.Databases).DatabaseState,
        _ => throw new ArgumentOutOfRangeException(nameof(ordinal)),
    };

    private sealed class SequentialRowReader(object?[][] rows) : IOperationalHealthRowReader
    {
        private int index = -1;
        private int lastOrdinal = -1;
        public int FieldCount => 11;
        public bool IsDBNull(int ordinal) => ReadValue(ordinal) is null;
        public byte[] GetBinary(int ordinal) => (byte[])ReadValue(ordinal)!;
        public bool GetBoolean(int ordinal) => (bool)ReadValue(ordinal)!;
        public DateTime GetDateTime(int ordinal) => (DateTime)ReadValue(ordinal)!;
        public Guid GetGuid(int ordinal) => (Guid)ReadValue(ordinal)!;
        public short GetInt16(int ordinal) => (short)ReadValue(ordinal)!;
        public int GetInt32(int ordinal) => (int)ReadValue(ordinal)!;
        public long GetInt64(int ordinal) => (long)ReadValue(ordinal)!;
        public string GetString(int ordinal) => (string)ReadValue(ordinal)!;

        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastOrdinal = -1;
            index++;
            return ValueTask.FromResult(index < rows.Length);
        }

        private object? ReadValue(int ordinal)
        {
            Assert.True(ordinal >= lastOrdinal, $"Sequential reader moved backward from {lastOrdinal} to {ordinal}.");
            lastOrdinal = ordinal;
            return rows[index][ordinal];
        }
    }
}
