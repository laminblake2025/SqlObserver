using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class QueryStoreGroupLiveSourceTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task TwoDisposableQueryStoreDatabasesProduceBoundedTargetGroups()
    {
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        {
            InitialCatalog = "master",
        };
        await using var admin = new SqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        int major = Assert.IsType<int>(await ScalarAsync(admin,
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));"));
        Assert.Equal(16, major);

        string suffix = Guid.NewGuid().ToString("N");
        string[] names = [$"SqlObserverGroupA_{suffix}", $"SqlObserverGroupB_{suffix}"];
        var created = new List<string>();
        try
        {
            foreach (string name in names)
            {
                await ExecuteAsync(admin, $"CREATE DATABASE [{name}];");
                created.Add(name);
                await ExecuteAsync(admin, $"ALTER DATABASE [{name}] SET QUERY_STORE = ON; " +
                    $"ALTER DATABASE [{name}] SET QUERY_STORE (OPERATION_MODE = READ_WRITE, " +
                    "QUERY_CAPTURE_MODE = ALL, INTERVAL_LENGTH_MINUTES = 1440, " +
                    "WAIT_STATS_CAPTURE_MODE = ON);");
                var databaseSettings = new SqlConnectionStringBuilder(settings.ConnectionString)
                {
                    InitialCatalog = name,
                };
                await using var database = new SqlConnection(databaseSettings.ConnectionString);
                await database.OpenAsync();
                await ExecuteAsync(database,
                    "CREATE TABLE dbo.group_probe (value int NOT NULL); " +
                    "INSERT dbo.group_probe VALUES (1),(2),(3);");
                await ExecuteAsync(database,
                    "CREATE PROCEDURE dbo.run_group_probe AS " +
                    "SELECT SUM(value) AS group_probe_sum FROM dbo.group_probe WHERE value > 0;");
                for (int run = 0; run < 3; run++)
                    await ExecuteAsync(database, "EXEC dbo.run_group_probe;");
                await ExecuteAsync(database, "EXEC sys.sp_query_store_flush_db;");
            }

            int[] ids = new int[names.Length];
            for (int index = 0; index < names.Length; index++)
                ids[index] = Assert.IsType<int>(await ScalarAsync(admin,
                    $"SELECT CONVERT(int, DB_ID(N'{names[index]}'));"));

            CollectorExecutionRequest request = Request();
            DateTimeOffset to = DateTimeOffset.UtcNow;
            DateTimeOffset from = to.AddMinutes(-10);
            var source = new QueryStoreGroupedTargetSource(
                new LabConnectionFactory(settings.ConnectionString),
                QueryStoreGroupedSourceBundle.LoadEmbedded());
            QueryStoreGroupedTargetRead read = await source.ReadAsync(request,
                from, to, CancellationToken.None);

            Assert.True(read.Output.RuntimeComplete);
            Assert.False(read.Loss.HasLoss);
            Assert.True(read.SourceRowsRead <= QueryStoreGroupedTargetAssembler.MaximumSourceRows);
            foreach (int id in ids)
            {
                Assert.Contains(read.Output.Items, item =>
                    item.Runtime.Key.Query.DatabaseId == id &&
                    item.Runtime.Counters.Executions >= 3);
            }

            var window = new QueryStoreGroupSourceWindow(request.TargetId,
                request.TargetRevision, from, to, null, to, false);
            var collector = new SqlServerQueryStoreGroupCollector(
                new FixedWindowPort(window), source);
            CollectorExecutionResult result = await collector.CollectAsync(request,
                CancellationToken.None);
            Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
            new CollectorOutputValidator(SqlServerQueryStoreGroupCollector.OutputContract)
                .Validate(collector.Manifest, request, result);
            foreach (int id in ids)
                Assert.Contains(result.Payload.QueryStoreGroups!.Items, item =>
                    item.Runtime.Key.Query.DatabaseId == id);
        }
        finally
        {
            foreach (string name in created.AsEnumerable().Reverse())
                await ExecuteAsync(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER " +
                    $"WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    private static CollectorExecutionRequest Request()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var revision = new ObservationTargetRevision(1);
        var profile = new CapabilityProfile(target, revision,
            new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(16, 0, 1, 0),
                new SqlServerEditionName("Express"), SqlServerEngineEdition.Express,
                SqlServerPlatform.Windows), CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            true, false, [], [], TimeSpan.FromSeconds(1), 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));
        return new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()),
            target, revision, SqlServerLabContract.ConnectionPolicy, profile,
            new CollectorAttemptNumber(1),
            new CollectorExecutionTimeout(TimeSpan.FromMinutes(2)));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        return await command.ExecuteScalarAsync();
    }

    private sealed class LabConnectionFactory(string connectionString)
        : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(
            SqlServerConnectionPolicy policy, CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(connectionString);
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


    private sealed class FixedWindowPort(QueryStoreGroupSourceWindow window)
        : IQueryStoreGroupWindowPort
    {
        public ValueTask<QueryStoreGroupSourceWindow> ReadNextAsync(
            MonitoredInstanceId targetId, ObservationTargetRevision revision,
            RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
            ValueTask.FromResult(window);
    }
}
