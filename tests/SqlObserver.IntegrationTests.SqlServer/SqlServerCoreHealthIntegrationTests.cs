using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class SqlServerCoreHealthIntegrationTests
{
    private static readonly MonitoredInstanceId TargetId =
        new(Guid.Parse("91919191-9191-9191-9191-919191919191"));
    private static readonly ObservationTargetRevision TargetRevision = new(7);
    private static readonly int[] SupportedMajorVersions = [15, 16, 17];

    [Fact]
    public void M4AssetsAreChecksumPinnedStrictBoundedPassiveAndOrdered()
    {
        SqlServerCollectorAssetCatalog catalog = SqlServerCollectorAssetCatalog.LoadEmbedded();

        Assert.Equal(
            ["engine.core", "database.inventory", "database.files"],
            catalog.Collectors.Select(static item => item.Manifest.Id.Value));
        Assert.Equal(64, catalog.BundleChecksum.Length);
        Assert.All(catalog.Collectors, asset =>
        {
            CollectorManifest manifest = asset.Manifest;
            Assert.Equal(1, manifest.ManifestVersion.Value);
            Assert.Equal(1, manifest.OutputSchemaVersion.Value);
            Assert.Equal(CollectorOperationalMode.Passive, manifest.OperationalMode);
            Assert.Equal([SqlServerPlatform.Windows], manifest.SupportedPlatforms);
            Assert.Equal(
                [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express],
                manifest.SupportedEngineEditions);
            Assert.Equal(2, manifest.Resilience.MaximumAttempts);
            Assert.Equal(TimeSpan.FromSeconds(5), manifest.Limits.ConnectTimeout);
            Assert.Equal(TimeSpan.FromSeconds(5), manifest.Limits.CommandTimeout);
            Assert.Equal(CollectorFallbackMode.Unsupported, manifest.Fallback.Mode);
            Assert.Equal(64, asset.ManifestChecksum.Length);

            foreach (int major in SupportedMajorVersions)
            {
                string sql = asset.GetQuery(major);
                Assert.StartsWith("SET NOCOUNT ON;\n", sql, StringComparison.Ordinal);
                Assert.Contains("TOP (@maximum_rows)", sql, StringComparison.Ordinal);
                Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("physical_name", sql, StringComparison.OrdinalIgnoreCase);
                AssertPassiveSql(sql);
            }
        });

        Assert.Equal(
            ["capability.connection"],
            catalog.Collectors[0].Manifest.DependsOn.Select(static item => item.Value));
        Assert.Equal(
            ["capability.connection", "engine.core"],
            catalog.Collectors[1].Manifest.DependsOn.Select(static item => item.Value));
        Assert.Equal(
            ["capability.connection", "engine.core", "database.inventory"],
            catalog.Collectors[2].Manifest.DependsOn.Select(static item => item.Value));

        Assert.Equal(
            [
                "engine.batch_requests_total",
                "engine.committed_memory_bytes",
                "engine.page_life_expectancy_seconds",
                "engine.process_physical_memory_bytes",
                "engine.sql_compilations_total",
                "engine.sql_recompilations_total",
                "engine.target_memory_bytes",
                "engine.user_connections",
            ],
            SqlServerCoreEngineCollector.OutputContract.Metrics
                .Select(static metric => metric.MetricId.Value)
                .Order(StringComparer.Ordinal));
        Assert.Equal(8, SqlServerCoreEngineCollector.OutputContract.MaxMetricSamples);
        Assert.Equal(1_000, SqlServerDatabaseInventoryCollector.OutputContract.MaxDatabaseObservations);
        Assert.Equal(1_000, SqlServerDatabaseFilesCollector.OutputContract.MaxDatabaseFileObservations);
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task LocalSqlServerCollectorsAreBoundedPassiveAndProduceExactOutputKinds()
    {
        SqlServerCollectorAssetCatalog catalog = SqlServerCollectorAssetCatalog.LoadEmbedded();
        var connectionFactory = new LabSqlServerConnectionFactory();
        ISqlServerCollector[] collectors =
        [
            new SqlServerCoreEngineCollector(catalog, connectionFactory),
            new SqlServerDatabaseInventoryCollector(catalog, connectionFactory),
            new SqlServerDatabaseFilesCollector(catalog, connectionFactory),
        ];
        TargetStateSnapshot before = await CaptureTargetStateAsync(connectionFactory);

        var results = new List<CollectorExecutionResult>();
        foreach (ISqlServerCollector collector in collectors)
        {
            CollectorExecutionResult result = await collector.CollectAsync(
                CreateRequest(),
                CancellationToken.None);
            Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
            Assert.Equal(CollectorRunReason.Completed, result.Reason);
            Assert.False(result.Loss.HasLoss);
            Assert.InRange(result.Accounting.SourceRowsRead, 1, collector.Manifest.Limits.MaxRows - 1);
            Assert.InRange(result.Accounting.ResponseBytes, 1, collector.Manifest.Limits.MaxResponseBytes);
            results.Add(result);
        }

        TargetStateSnapshot after = await CaptureTargetStateAsync(connectionFactory);
        Assert.Equal(before, after);

        CollectorExecutionResult core = results[0];
        Assert.Equal(8, core.Payload.Metrics.Count);
        Assert.Equal(
            SqlServerCoreEngineCollector.OutputContract.Metrics
                .Select(static metric => metric.MetricId.Value)
                .Order(StringComparer.Ordinal),
            core.Payload.Metrics
                .Select(static metric => metric.MetricId.Value)
                .Order(StringComparer.Ordinal));
        Assert.All(core.Payload.Metrics, sample =>
        {
            Assert.StartsWith("engine.", sample.MetricId.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("_per_second", sample.MetricId.Value, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.Zero, sample.ObservedAtUtc.Offset);
            Assert.Empty(sample.Dimensions);
        });

        CollectorExecutionResult inventory = results[1];
        Assert.NotEmpty(inventory.Payload.Databases.Items);
        Assert.All(inventory.Payload.Databases.Items, item =>
        {
            Assert.Equal(TargetId, item.TargetId);
            Assert.NotEmpty(item.Name.Value);
            Assert.Equal(TimeSpan.Zero, item.ObservedAtUtc.Offset);
        });

        CollectorExecutionResult files = results[2];
        Assert.NotEmpty(files.Payload.DatabaseFiles.Items);
        Assert.All(files.Payload.DatabaseFiles.Items, item =>
        {
            Assert.Equal(TargetId, item.TargetId);
            Assert.True(item.SizeBytes > 0);
            Assert.NotEmpty(item.LogicalName.Value);
        });
        Assert.DoesNotContain(
            typeof(DatabaseFileObservation).GetProperties(),
            static property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Physical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InternalDeadlineCancelsBlockedTargetWorkAsSafeTimeout()
    {
        var collector = new SqlServerCoreEngineCollector(
            SqlServerCollectorAssetCatalog.LoadEmbedded(),
            new BlockingConnectionFactory());
        Stopwatch stopwatch = Stopwatch.StartNew();
        CollectorExecutionResult result = await collector.CollectAsync(
            CreateRequest(TimeSpan.FromMilliseconds(200)),
            CancellationToken.None);

        Assert.Equal(CollectorRunOutcome.TimedOut, result.Outcome);
        Assert.Equal(CollectorRunReason.DeadlineExceeded, result.Reason);
        Assert.Empty(result.Payload.Metrics);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CallerCancellationPropagatesFromBlockedTargetWork()
    {
        var collector = new SqlServerCoreEngineCollector(
            SqlServerCollectorAssetCatalog.LoadEmbedded(),
            new BlockingConnectionFactory());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await collector.CollectAsync(CreateRequest(TimeSpan.FromSeconds(5)), cancellation.Token));
    }

    [Fact]
    public void FinalManifestRowIsAConservativeTruncationProbe()
    {
        var budget = new BoundedCollectorReadBudget(maximumRows: 3, maximumResponseBytes: 100);

        Assert.True(budget.TryBeginRow());
        Assert.True(budget.TryAcceptResponseBytes(10));
        Assert.True(budget.TryBeginRow());
        Assert.True(budget.TryAcceptResponseBytes(20));
        Assert.False(budget.TryBeginRow());

        Assert.Equal(3, budget.SourceRowsRead);
        Assert.Equal(30, budget.ResponseBytes);
        Assert.True(budget.RowLimitReached);
        Assert.False(budget.ByteLimitReached);
        CollectorLossEvidence loss = SqlServerHealthCollector.CreateLossEvidence(
            budget.ByteLimitReached,
            budget.RowLimitReached);
        Assert.Equal(CollectorLossKind.SourceRowLimit, loss.Kind);
        Assert.Equal(1, loss.MinimumLostItems);
        Assert.False(loss.CountIsExact);
    }

    [Fact]
    public void ResponseByteBoundaryRejectsWholeRowAndExposesConservativeLoss()
    {
        var budget = new BoundedCollectorReadBudget(maximumRows: 4, maximumResponseBytes: 30);

        Assert.True(budget.TryBeginRow());
        Assert.True(budget.TryAcceptResponseBytes(30));
        Assert.True(budget.TryBeginRow());
        Assert.False(budget.TryAcceptResponseBytes(1));

        Assert.Equal(2, budget.SourceRowsRead);
        Assert.Equal(30, budget.ResponseBytes);
        Assert.False(budget.RowLimitReached);
        Assert.True(budget.ByteLimitReached);
        CollectorLossEvidence loss = SqlServerHealthCollector.CreateLossEvidence(
            budget.ByteLimitReached,
            budget.RowLimitReached);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, loss.Kind);
        Assert.Equal(1, loss.MinimumLostItems);
        Assert.Equal(1, loss.MinimumLostBytes);
        Assert.False(loss.CountIsExact);
    }

    [Fact]
    public void MalformedRowPreservesReadAndAcceptedOutputAccounting()
    {
        var budget = new BoundedCollectorReadBudget(maximumRows: 8, maximumResponseBytes: 100);

        Assert.True(budget.TryBeginRow());
        Assert.True(budget.TryAcceptResponseBytes(11));
        Assert.True(budget.TryBeginRow());
        Assert.True(budget.TryAcceptResponseBytes(13));
        Assert.True(budget.TryBeginRow());

        var exception = new CollectorReadValidationException(
            budget,
            acceptedOutputItems: 2,
            acceptedOutputBytes: 24,
            new InvalidDataException("malformed third row"));

        Assert.Equal(3, exception.Accounting.SourceRowsRead);
        Assert.Equal(3, exception.Accounting.OutputItemsProduced);
        Assert.Equal(24, exception.Accounting.ResponseBytes);
        Assert.Equal(24, exception.Accounting.OutputBytes);
    }

    private static CollectorExecutionRequest CreateRequest(TimeSpan? timeout = null)
    {
        DateTimeOffset checkedAt = new(2026, 8, 23, 17, 0, 0, TimeSpan.Zero);
        var profile = new CapabilityProfile(
            TargetId,
            TargetRevision,
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
            TimeSpan.FromMilliseconds(10),
            evidenceBytes: 64,
            checkedAt,
            checkedAt.AddMinutes(5));
        return new CollectorExecutionRequest(
            new CollectorRunId(Guid.NewGuid()),
            TargetId,
            TargetRevision,
            new SqlServerConnectionPolicy(
                new SqlServerEndpoint(new SqlServerHostName("test"), new SqlServerInstanceName("SQLEXPRESS")),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))),
            profile,
            new CollectorAttemptNumber(1),
            new CollectorExecutionTimeout(timeout ?? TimeSpan.FromSeconds(10)));
    }

    private static void AssertPassiveSql(string sql)
    {
        string normalized = string.Concat(" ", sql.ToUpperInvariant(), " ");
        foreach (string token in new[]
        {
            " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " CREATE ", " ALTER ",
            " DROP ", " TRUNCATE ", " EXEC ", " EXECUTE ", " DBCC ", " BACKUP ",
            " RESTORE ", " RECONFIGURE ", " KILL ",
        })
        {
            Assert.DoesNotContain(token, normalized, StringComparison.Ordinal);
        }
    }

    private static async Task<TargetStateSnapshot> CaptureTargetStateAsync(
        ISqlServerConnectionFactory connectionFactory)
    {
        await using SqlConnection connection = await connectionFactory.OpenConnectionAsync(
            CreateRequest().ConnectionPolicy,
            CancellationToken.None);
        const string sql = """
            SET NOCOUNT ON;

            SELECT
                (SELECT COUNT_BIG(*) FROM sys.configurations),
                (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(configuration_id, value, value_in_use)) FROM sys.configurations),
                (SELECT COUNT_BIG(*) FROM sys.server_event_sessions),
                (SELECT COUNT_BIG(*) FROM sys.server_permissions),
                (SELECT COUNT_BIG(*) FROM sys.databases);
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return new TargetStateSnapshot(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private sealed class LabSqlServerConnectionFactory : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(
            SqlServerConnectionPolicy policy,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
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

    private sealed class BlockingConnectionFactory : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(
            SqlServerConnectionPolicy policy,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(policy);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocked test seam returned unexpectedly.");
        }
    }

    private sealed record TargetStateSnapshot(
        long ConfigurationCount,
        int? ConfigurationChecksum,
        long EventSessionCount,
        long PermissionCount,
        long DatabaseCount);
}
