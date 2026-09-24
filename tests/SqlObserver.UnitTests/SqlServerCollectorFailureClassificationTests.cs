using System.Reflection;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerCollectorFailureClassificationTests
{
    public static IEnumerable<object[]> FailureCases()
    {
        foreach (string path in new[] { "health", "activity", "operational", "replication", "query-inventory", "query-read" })
        {
            foreach (int number in new[] { 20, 53, 64, 233, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613, 11001, -1, 26, 258, 121, 10061, 1205, 1222 })
                yield return [path, number, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure];
            foreach (int number in new[] { 229, 297, 300, 916 })
                yield return [path, number, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing];
            yield return [path, -2, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded];
            foreach (int number in new[] { 18456, 18452, 102, 50000 })
                yield return [path, number, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure];
        }
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task ThrownSqlErrorsRetainTheirCollectorOutcome(string path, int number, CollectorRunOutcome outcome, CollectorRunReason reason)
    {
        int failuresThrown = 0;
        ISqlServerCollector collector = CreateCollector(path, () => { failuresThrown++; return TestSqlErrors.Create(number); });
        CollectorExecutionResult result = await collector.CollectAsync(CreateRequest(), CancellationToken.None);
        Assert.Equal(1, failuresThrown);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(0, result.Accounting.SourceRowsRead);
        Assert.False(result.Loss.HasLoss);
        if (path.StartsWith("query-", StringComparison.Ordinal))
        {
            QueryPerformanceTargetStatus target = Assert.IsType<QueryPerformanceTargetStatus>(result.Payload.QueryPerformanceTargetStatus);
            Assert.Equal(outcome == CollectorRunOutcome.TimedOut ? "deadline_exceeded" : "connection_failure", target.Status);
            Assert.Equal(reason switch
            {
                CollectorRunReason.DeadlineExceeded => "deadline_exceeded",
                CollectorRunReason.RequiredPermissionMissing => "required_permission_missing",
                CollectorRunReason.TransientTargetFailure => "transient_target_failure",
                _ => "permanent_target_failure",
            }, target.Reason);
        }
    }

    [Theory]
    [InlineData("health")]
    [InlineData("activity")]
    [InlineData("operational")]
    [InlineData("replication")]
    [InlineData("query-inventory")]
    [InlineData("query-read")]
    public async Task CallerCancellationDuringIoPropagates(string path)
    {
        using var caller = new CancellationTokenSource();
        ISqlServerCollector collector = CreateCollector(path, () =>
        {
            caller.Cancel();
            return new OperationCanceledException(caller.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await collector.CollectAsync(CreateRequest(), caller.Token));
    }

    [Fact]
    public async Task RealConnectionFailureUsesExistingBoundedEngineRetry()
    {
        var factory = new ThrowingConnectionFactory(() => TestSqlErrors.Create(11001));
        var collector = new SqlServerCoreEngineCollector(SqlServerCollectorAssetCatalog.LoadEmbedded(), factory);
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorRegistration registration = M4TestData.CreateRegistration(manifest, collector.CollectAsync);
        var result = await new CollectorExecutionEngine().ExecuteAsync(registration, M4TestData.CreateWork(manifest), new CollectorRunId(Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(2, factory.Calls);
        Assert.Equal(2, result.Summary.AttemptCount);
        Assert.Equal(1, result.Summary.RetryCount);
        Assert.Equal(CollectorRunOutcome.TransientFailure, result.Summary.Outcome);
    }

    private static ISqlServerCollector CreateCollector(string path, Func<Exception> failure)
    {
        var factory = new ThrowingConnectionFactory(failure);
        return path switch
        {
            "health" => new SqlServerCoreEngineCollector(SqlServerCollectorAssetCatalog.LoadEmbedded(), factory),
            "activity" => new SqlServerActivitySessionsCollector(SqlServerActivityCollectorAssetCatalog.LoadEmbedded(), factory),
            "operational" => new TestOperationalCollector(factory),
            "replication" => new SqlServerReplicationCollector(SqlServerReplicationAssetCatalog.LoadEmbedded(), "distribution", new IdentityFingerprintKey(new byte[IdentityFingerprintKey.RequiredLength]), factory),
            "query-inventory" => new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new ThrowingExecutionPort(failure, true)),
            "query-read" => new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new ThrowingExecutionPort(failure, false)),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
    }

    private static CollectorExecutionRequest CreateRequest()
    {
        CapabilityProfile profile = M4TestData.CreateProfile();
        var capabilities = profile.Capabilities.Concat([new CapabilityEvidence(new CapabilityId("feature.replication"), CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)]).ToArray();
        var permissions = profile.Permissions.Concat([new PermissionEvidence(new SqlServerPermissionId("replication.replmonitor"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.Granted)]).ToArray();
        profile = new CapabilityProfile(profile.TargetId, profile.TargetRevision, profile.CollectorId, profile.CollectorManifestVersion, profile.OutputSchemaVersion, profile.ServerIdentity, profile.Outcome, profile.Reason, profile.AuthenticationScheme, profile.TransportEncrypted, profile.IsSysAdmin, capabilities, permissions, profile.DiscoveryDuration, profile.EvidenceBytes, profile.CheckedAtUtc, profile.ValidUntilUtc);
        return new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), profile.TargetId, profile.TargetRevision, M4TestData.CreateConnectionPolicy(), profile, new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(1)));
    }

    private sealed class ThrowingConnectionFactory(Func<Exception> failure) : ISqlServerConnectionFactory
    {
        internal int Calls { get; private set; }
        public ValueTask<SqlConnection> OpenConnectionAsync(SqlServerConnectionPolicy policy, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromException<SqlConnection>(failure());
        }
    }

    private sealed class ThrowingExecutionPort(Func<Exception> failure, bool inventoryFailure) : ISqlServerQueryPerformanceExecutionPort
    {
        public ValueTask<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest request, CancellationToken cancellationToken) => inventoryFailure
            ? ValueTask.FromException<IReadOnlyList<SqlServerDatabaseIdentity>>(failure())
            : ValueTask.FromResult<IReadOnlyList<SqlServerDatabaseIdentity>>([new SqlServerDatabaseIdentity(5, "inventory")]);
        public ValueTask<QueryPerformanceExecutionBatch> ReadDatabasesAsync(IReadOnlyList<SqlServerDatabaseIdentity> databases, CollectorExecutionRequest request, SharedResponseBudget responseBudget, CancellationToken cancellationToken) => ValueTask.FromException<QueryPerformanceExecutionBatch>(failure());
    }

    private sealed class TestOperationalCollector(ISqlServerConnectionFactory factory) : SqlServerOperationalHealthCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded(), factory)
    {
        public override CollectorManifest Manifest { get; } = new SqlServerTempDbHealthCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded()).Manifest;
        protected override string QueryName => "tempdb.health";
        protected override ValueTask<(IOperationalHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss)> ReadAsync(CollectorExecutionRequest request, IOperationalHealthRowReader reader, CancellationToken cancellationToken) => throw new InvalidOperationException("The throwing connection factory must prevent row reading.");
    }
}

internal static class TestSqlErrors
{
    // Test-only construction against the pinned Microsoft.Data.SqlClient 7.0.2
    // signatures. Fail explicitly on signature drift; no live SQL endpoint is used.
    internal static SqlException Create(int number)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ConstructorInfo errorConstructor = typeof(SqlError).GetConstructor(flags, null, [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)], null)
            ?? throw new InvalidOperationException("Pinned SqlError constructor changed.");
        var error = (SqlError)errorConstructor.Invoke([number, (byte)1, (byte)16, "test", "Synthetic bounded failure", "test", 1, null]);
        var errors = (SqlErrorCollection)(Activator.CreateInstance(typeof(SqlErrorCollection), true) ?? throw new InvalidOperationException("Pinned SqlErrorCollection constructor changed."));
        MethodInfo add = typeof(SqlErrorCollection).GetMethod("Add", flags) ?? throw new InvalidOperationException("Pinned SqlErrorCollection.Add changed.");
        add.Invoke(errors, [error]);
        ConstructorInfo exceptionConstructor = typeof(SqlException).GetConstructor(flags, null, [typeof(string), typeof(SqlErrorCollection), typeof(Exception), typeof(Guid)], null)
            ?? throw new InvalidOperationException("Pinned SqlException constructor changed.");
        var exception = (SqlException)exceptionConstructor.Invoke(["Synthetic bounded failure", errors, null, Guid.Empty]);
        Assert.Equal(number, exception.Number);
        return exception;
    }
}
