using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed partial class SqlServerCoreHealthIntegrationTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task CoreBaselineInputsAreBoundedAndComparableUnderOnlyTheVersionSpecificStateGrant()
    {
        Assert.NotEqual("Release", Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"));
        var privilegedFactory = new LabSqlServerConnectionFactory();
        TargetStateSnapshot before = await CaptureTargetStateAsync(privilegedFactory);
        await using var admin = new SqlConnection(new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        { Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        int major;
        SqlServerEngineEdition edition;
        await using (var identity = new SqlCommand("SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion')),CONVERT(int,SERVERPROPERTY('EngineEdition')),IS_SRVROLEMEMBER('sysadmin');", admin))
        await using (SqlDataReader reader = await identity.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); major = reader.GetInt32(0);
            edition = (SqlServerEngineEdition)reader.GetInt32(1);
            Assert.InRange(major, 15, 17); Assert.Equal(1, reader.GetInt32(2));
        }
        string login = "sqlobserver_core_probe_" + Guid.NewGuid().ToString("N");
        string password = "Aa9!" + Guid.NewGuid().ToString("N");
        string permission = major >= 16 ? "VIEW SERVER PERFORMANCE STATE" : "VIEW SERVER STATE";
        var restrictedFactory = new ImpersonatedCoreProbeConnectionFactory(login);
        var collector = new SqlServerCoreEngineCollector(SqlServerCollectorAssetCatalog.LoadEmbedded(), restrictedFactory);
        CollectorExecutionRequest request = CreateRequest(majorVersion: major, edition: edition);
        try
        {
            await ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=ON;");
            CollectorExecutionResult denied = await collector.CollectAsync(request, default);
            Assert.Equal(CollectorRunOutcome.PermissionDenied, denied.Outcome);
            Assert.Empty(denied.Payload.Metrics);
            await ExecuteAsync($"GRANT {permission} TO [{login}];");
            var samples = new List<CollectorExecutionResult>();
            for (int i = 0; i < 3; i++)
            {
                CollectorExecutionRequest sampleRequest = CreateRequest(majorVersion: major, edition: edition);
                CollectorExecutionResult result = await collector.CollectAsync(sampleRequest, default);
                Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome); Assert.False(result.Loss.HasLoss);
                Assert.Equal(14, result.Payload.Metrics.Count);
                Assert.InRange(result.Accounting.SourceRowsRead, 14, collector.Manifest.Limits.MaxRows - 1);
                Assert.InRange(result.Accounting.ResponseBytes, 1, collector.Manifest.Limits.MaxResponseBytes);
                new CollectorOutputValidator(SqlServerCoreEngineCollector.OutputContract).Validate(collector.Manifest, sampleRequest, result);
                Assert.All(result.Payload.Metrics, sample =>
                {
                    Assert.True(double.IsFinite(sample.Value)); Assert.True(sample.Value >= 0);
                    Assert.Equal(TimeSpan.Zero, sample.ObservedAtUtc.Offset); Assert.Empty(sample.Dimensions);
                });
                samples.Add(result);
                if (i < 2) await Task.Delay(500);
            }
            for (int i = 1; i < samples.Count; i++)
            {
                var previous = samples[i - 1].Payload.Metrics.ToDictionary(s => s.MetricId.Value);
                var current = samples[i].Payload.Metrics.ToDictionary(s => s.MetricId.Value);
                Assert.Equal(previous["engine.start_time_key"].Value, current["engine.start_time_key"].Value);
                double schedulers = current["engine.visible_scheduler_count"].Value;
                Assert.Equal(previous["engine.visible_scheduler_count"].Value, schedulers);
                Assert.True(schedulers > 0);
                foreach (string counter in new[] { "engine.batch_requests_total", "engine.scheduler_cpu_milliseconds_total" })
                {
                    double elapsed = (current[counter].ObservedAtUtc - previous[counter].ObservedAtUtc).TotalSeconds;
                    double delta = current[counter].Value - previous[counter].Value;
                    Assert.InRange(elapsed, .1, 120); Assert.True(delta >= 0);
                    Assert.InRange(current[counter].Value, 0, 9007199254740991d);
                    double rate = delta / elapsed;
                    Assert.True(double.IsFinite(rate));
                    if (counter == "engine.scheduler_cpu_milliseconds_total")
                    {
                        Assert.True(delta <= elapsed * 1000 * schedulers * 1.05);
                        Assert.InRange(Math.Min(100, rate / (1000 * schedulers) * 100), 0, 100);
                    }
                }
                foreach (string gauge in new[] { "engine.user_connections", "engine.process_physical_memory_bytes",
                    "engine.os_available_memory_bytes", "engine.memory_grants_pending", "engine.scheduler_runnable_tasks" })
                    Assert.True(current.ContainsKey(gauge));
            }
        }
        finally
        {
            await ExecuteAsync($"IF EXISTS(SELECT 1 FROM sys.server_principals WHERE name=N'{login}') DROP LOGIN [{login}];");
        }
        Assert.Equal(before, await CaptureTargetStateAsync(privilegedFactory));

        async Task ExecuteAsync(string sql)
        {
            await using var command = new SqlCommand(sql, admin) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class ImpersonatedCoreProbeConnectionFactory(string login) : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(SqlServerConnectionPolicy policy, CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
            { Pooling = false }.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand($"EXECUTE AS LOGIN=N'{login}'; SELECT IS_SRVROLEMEMBER('sysadmin');", connection)
                { CommandTimeout = 5 };
                Assert.Equal(0, await command.ExecuteScalarAsync(cancellationToken));
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
    }
}
