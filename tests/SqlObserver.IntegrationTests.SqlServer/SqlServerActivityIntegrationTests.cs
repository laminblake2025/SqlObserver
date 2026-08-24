using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class SqlServerActivityIntegrationTests
{
    private static readonly MonitoredInstanceId TargetId =
        new(Guid.Parse("53535353-5353-5353-5353-535353535353"));
    private static readonly ObservationTargetRevision Revision = new(5);
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 23, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void M5AssetsAreSeparateChecksumPinnedPassiveSafeAndOrdered()
    {
        SqlServerActivityCollectorAssetCatalog catalog = SqlServerActivityCollectorAssetCatalog.LoadEmbedded();

        Assert.Equal(
            ["activity.sessions", "activity.requests", "waits.server", "blocking.current"],
            catalog.Collectors.Select(static asset => asset.Manifest.Id.Value));
        Assert.Equal("dbd280ef6db0f16a300e53aadf951ebc837ba0dc79837398264bdd82a802e9ff", catalog.BundleChecksum);
        Assert.Equal(
            "c2bd727d3c2f6278cea09c37acde007244cb681452fe865cfab1aadf7c84accf",
            SqlServerCollectorAssetCatalog.LoadEmbedded().BundleChecksum);

        string[][] expectedDependencies =
        [
            ["capability.connection", "engine.core"],
            ["capability.connection", "engine.core", "activity.sessions"],
            ["capability.connection", "engine.core", "activity.sessions", "activity.requests"],
            ["capability.connection", "engine.core", "activity.sessions", "activity.requests", "waits.server"],
        ];
        CollectorOutputKind[] expectedKinds =
        [
            CollectorOutputKind.ActivitySessions,
            CollectorOutputKind.ActivityRequests,
            CollectorOutputKind.ServerWaits,
            CollectorOutputKind.CurrentBlocking,
        ];
        string[] expectedDmvs =
        [
            "sys.dm_exec_sessions",
            "sys.dm_exec_requests",
            "sys.dm_os_wait_stats",
            "sys.dm_os_waiting_tasks",
        ];
        int[] maximumOutputItems = [512, 512, 2_048, 1_024];
        string[][] ownSessionPredicates =
        [
            ["sessions.session_id <> @@SPID"],
            ["requests.session_id <> @@SPID"],
            [],
            ["waiting.session_id <> @@SPID", "waiting.blocking_session_id <> @@SPID"],
        ];
        for (int collectorIndex = 0; collectorIndex < catalog.Collectors.Count; collectorIndex++)
        {
            SqlServerCollectorAsset asset = catalog.Collectors[collectorIndex];
            CollectorManifest manifest = asset.Manifest;
            Assert.Equal(1, manifest.ManifestVersion.Value);
            Assert.Equal(1, manifest.OutputSchemaVersion.Value);
            Assert.Equal(expectedKinds[collectorIndex], manifest.OutputKind);
            Assert.Equal(CollectorOperationalMode.Passive, manifest.OperationalMode);
            Assert.Equal(TimeSpan.FromSeconds(5), manifest.Limits.ConnectTimeout);
            Assert.Equal(TimeSpan.FromSeconds(5), manifest.Limits.CommandTimeout);
            Assert.Equal(maximumOutputItems[collectorIndex] + 1, manifest.Limits.MaxRows);
            Assert.Equal(2, manifest.Resilience.MaximumAttempts);
            Assert.Equal(3, manifest.Resilience.CircuitFailureThreshold);
            Assert.Equal(TimeSpan.FromMinutes(5), manifest.Resilience.CircuitOpenDuration);
            Assert.Equal(expectedDependencies[collectorIndex], manifest.DependsOn.Select(static item => item.Value));
            Assert.Equal(64, asset.ManifestChecksum.Length);
            for (int major = 15; major <= 17; major++)
            {
                string sql = asset.GetQuery(major);
                Assert.StartsWith("SET NOCOUNT ON;\n", sql, StringComparison.Ordinal);
                Assert.Contains("TOP (@maximum_rows)", sql, StringComparison.Ordinal);
                Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(expectedDmvs[collectorIndex], sql, StringComparison.Ordinal);
                int selectEnd = sql.IndexOf("\nFROM ", StringComparison.Ordinal);
                Assert.True(selectEnd > 0);
                Assert.DoesNotContain("@@SPID", sql[..selectEnd], StringComparison.Ordinal);
                Assert.Equal(ownSessionPredicates[collectorIndex].Length, CountOccurrences(sql, "@@SPID"));
                Assert.All(
                    ownSessionPredicates[collectorIndex],
                    predicate => Assert.Equal(1, CountOccurrences(sql, predicate)));
                AssertSafePassiveSql(sql);
            }
        }

        Assert.Equal(512, SqlServerActivitySessionsCollector.OutputContract.MaxActivitySessionObservations);
        Assert.Equal(512, SqlServerActivityRequestsCollector.OutputContract.MaxActivityRequestObservations);
        Assert.Equal(2_048, SqlServerServerWaitsCollector.OutputContract.MaxServerWaitObservations);
        Assert.Equal(1_024, SqlServerCurrentBlockingCollector.OutputContract.MaxBlockingEdgeObservations);
    }

    [Fact]
    public void BlockingChainsAreCycleAndDepthSafeAndEnforceTheNodeLimit()
    {
        var waitType = new SqlServerWaitType("LCK_M_S");
        BlockingChainBuildResult cycle = BlockingChainBuilder.Build(
            TargetId,
            Revision,
            [
                Source(1, 2, waitType),
                Source(2, 1, waitType),
            ]);
        Assert.Equal(2, cycle.Observations.Count);
        Assert.All(cycle.Observations, static item => Assert.Equal(BlockingChainState.Cycle, item.ChainState));
        Assert.All(cycle.Observations, static item => Assert.InRange(item.ChainDepth, 1, BlockingChainLimits.MaximumDepth));

        BlockingSourceEdge[] deepSources = Enumerable.Range(10, BlockingChainLimits.MaximumDepth + 1)
            .Select(blocked => Source(blocked, blocked + 1, waitType))
            .ToArray();
        BlockingChainBuildResult deep = BlockingChainBuilder.Build(TargetId, Revision, deepSources);
        BlockingEdgeObservation head = Assert.Single(deep.Observations, static item => item.BlockedSessionId == 10);
        Assert.Equal(BlockingChainState.DepthLimit, head.ChainState);
        Assert.Equal(BlockingChainLimits.MaximumDepth, head.ChainDepth);

        BlockingSourceEdge[] tooManyNodes = Enumerable.Range(0, 129)
            .Select(index => Source(index * 2 + 1, index * 2 + 2, waitType))
            .ToArray();
        BlockingChainBuildResult bounded = BlockingChainBuilder.Build(TargetId, Revision, tooManyNodes);
        Assert.Equal(128, bounded.Observations.Count);
        Assert.Equal(1, bounded.LostEdges);
        Assert.InRange(
            bounded.Observations.SelectMany(static edge => new[] { edge.BlockedSessionId, edge.BlockerSessionId!.Value })
                .Distinct()
                .Count(),
            1,
            BlockingChainLimits.MaximumNodes);
    }

    [Fact]
    public void SpecialBlockersUseAClosedKindWithoutResourceDescriptions()
    {
        BlockingChainBuildResult result = BlockingChainBuilder.Build(
            TargetId,
            Revision,
            [new BlockingSourceEdge(
                60,
                BlockingBlockerKind.OrphanedDistributedTransaction,
                null,
                new SqlServerWaitType("DTC_STATE"),
                1,
                10,
                ObservedAt)]);

        BlockingEdgeObservation edge = Assert.Single(result.Observations);
        Assert.Equal(BlockingBlockerKind.OrphanedDistributedTransaction, edge.BlockerKind);
        Assert.Null(edge.BlockerSessionId);
        Assert.Null(edge.RootBlockerSessionId);
        Assert.Equal(BlockingChainState.ExternalBlocker, edge.ChainState);
        Assert.DoesNotContain(
            typeof(BlockingEdgeObservation).GetProperties(),
            static property => property.Name.Contains("Resource", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BudgetTruncationConservativelyIncludesKnownGraphLoss()
    {
        var rowBudget = new BoundedCollectorReadBudget(maximumRows: 2, maximumResponseBytes: 100);
        Assert.True(rowBudget.TryBeginRow());
        Assert.True(rowBudget.TryAcceptResponseBytes(10));
        Assert.False(rowBudget.TryBeginRow());
        var graphLoss = new CollectorLossEvidence(
            CollectorLossKind.BlockingGraphLimit,
            minimumLostItems: 3,
            countIsExact: true);

        CollectorLossEvidence combinedRows = ActivityCollectorLossAccounting.Combine(rowBudget, graphLoss);
        Assert.Equal(CollectorLossKind.SourceRowLimit, combinedRows.Kind);
        Assert.Equal(4, combinedRows.MinimumLostItems);
        Assert.False(combinedRows.CountIsExact);

        var byteBudget = new BoundedCollectorReadBudget(maximumRows: 3, maximumResponseBytes: 10);
        Assert.True(byteBudget.TryBeginRow());
        Assert.False(byteBudget.TryAcceptResponseBytes(11));
        CollectorLossEvidence combinedBytes = ActivityCollectorLossAccounting.Combine(byteBudget, graphLoss);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, combinedBytes.Kind);
        Assert.Equal(4, combinedBytes.MinimumLostItems);
        Assert.Equal(1, combinedBytes.MinimumLostBytes);
        Assert.False(combinedBytes.CountIsExact);
    }

    [Fact]
    public async Task LocalSqlServerActivityCollectorsProduceOnlyTheirTypedOutputKinds()
    {
        SqlServerActivityCollectorAssetCatalog catalog = SqlServerActivityCollectorAssetCatalog.LoadEmbedded();
        var connectionFactory = new LabSqlServerConnectionFactory();
        ISqlServerCollector[] collectors =
        [
            new SqlServerActivitySessionsCollector(catalog, connectionFactory),
            new SqlServerActivityRequestsCollector(catalog, connectionFactory),
            new SqlServerServerWaitsCollector(catalog, connectionFactory),
            new SqlServerCurrentBlockingCollector(catalog, connectionFactory),
        ];
        var results = new List<CollectorExecutionResult>();
        foreach (ISqlServerCollector collector in collectors)
        {
            CollectorExecutionResult result = await collector.CollectAsync(CreateRequest(), CancellationToken.None);
            Assert.True(
                result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial,
                $"{collector.Manifest.Id.Value} returned {result.Outcome}/{result.Reason}.");
            Assert.InRange(result.Accounting.SourceRowsRead, 0, collector.Manifest.Limits.MaxRows);
            Assert.InRange(result.Accounting.ResponseBytes, 0, collector.Manifest.Limits.MaxResponseBytes);
            results.Add(result);
        }

        Assert.NotEmpty(results[0].Payload.ActivitySessions.Items);
        Assert.Empty(results[0].Payload.ActivityRequests.Items);
        Assert.Empty(results[1].Payload.ActivitySessions.Items);
        Assert.NotEmpty(results[2].Payload.ServerWaits.Items);
        Assert.All(results[2].Payload.ServerWaits.Items, static item =>
        {
            Assert.True(item.WaitingTasksCount >= 0);
            Assert.True(item.WaitTimeMilliseconds >= item.SignalWaitTimeMilliseconds);
            Assert.Matches("^[A-Z][A-Z0-9_]*$", item.WaitType.Value);
        });
        Assert.All(results[3].Payload.BlockingEdges.Items, static item =>
        {
            Assert.InRange(item.ChainDepth, 1, BlockingChainLimits.MaximumDepth);
            Assert.True(Enum.IsDefined(item.BlockerKind));
            Assert.True(Enum.IsDefined(item.ChainState));
        });
    }

    private static BlockingSourceEdge Source(int blocked, int blocker, SqlServerWaitType waitType) =>
        new(
            blocked,
            BlockingBlockerKind.Session,
            blocker,
            waitType,
            1,
            10,
            ObservedAt);

    private static CollectorExecutionRequest CreateRequest()
    {
        var profile = new CapabilityProfile(
            TargetId,
            Revision,
            new CollectorId("capability.connection"),
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            new SqlServerIdentity(
                new SqlServerVersion(16, 0, 1000, 0),
                new SqlServerEditionName("Express Edition"),
                SqlServerEngineEdition.Express,
                SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
            capabilities: [],
            permissions: [],
            TimeSpan.FromMilliseconds(1),
            evidenceBytes: 1,
            ObservedAt,
            ObservedAt.AddMinutes(5));
        return new CollectorExecutionRequest(
            new CollectorRunId(Guid.NewGuid()),
            TargetId,
            Revision,
            new SqlServerConnectionPolicy(
                new SqlServerEndpoint(
                    new SqlServerHostName("DESKTOP-IORRV3E"),
                    new SqlServerInstanceName("SQLEXPRESS")),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))),
            profile,
            new CollectorAttemptNumber(1),
            new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));
    }

    private static void AssertSafePassiveSql(string sql)
    {
        string normalized = string.Concat(" ", sql.ToUpperInvariant(), " ");
        string[] forbidden =
        [
            " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " CREATE ", " ALTER ", " DROP ",
            " TRUNCATE ", " EXEC ", " EXECUTE ", " DBCC ", " BACKUP ", " RESTORE ", " KILL ",
            "QUERY_TEXT", "SQL_TEXT", "SQL_HANDLE", "PLAN_HANDLE", "QUERY_PLAN",
            "LOGIN_NAME", "HOST_NAME", "PROGRAM_NAME", "CLIENT_INTERFACE_NAME", "CLIENT_NET_ADDRESS",
            "LOCAL_NET_ADDRESS", "REMOTE_NET_ADDRESS", "RESOURCE_DESCRIPTION", "TASK_ADDRESS",
            "WORKER_ADDRESS", "WAITING_TASK_ADDRESS",
        ];
        Assert.DoesNotContain(forbidden, token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private sealed class LabSqlServerConnectionFactory : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(
            SqlServerConnectionPolicy policy,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = "DESKTOP-IORRV3E\\SQLEXPRESS",
                InitialCatalog = "master",
                IntegratedSecurity = true,
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true,
                ApplicationName = "SqlObserver.M5IntegrationTests",
                ConnectTimeout = 5,
                Pooling = false,
                Enlist = false,
            };
            var connection = new SqlConnection(builder.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }
}
