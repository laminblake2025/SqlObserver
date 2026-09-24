using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M4CollectorPersistenceIntegrationTests
{
    [Fact]
    public async Task OverviewSqlMemoryUpgradeExposesExistingSamplesWithoutChangingWorkloadRates()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new MigrationApplyRequest(111, DefaultTimeout), CancellationToken.None)).HasFailures);

        var target = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, target, 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = Assert.Single(
            (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items,
            item => item.TargetId == target && item.CollectorId.Value == "engine.core");
        await CommitSuccessAsync(runtime, leases, work, CreateEnginePayload(work));
        DateTimeOffset cutoff = DateTimeOffset.UtcNow;
        var history = new PostgreSqlOverviewHistoryPort(server);
        var before = await history.ReadAsync(target, 1, cutoff.AddHours(-1), cutoff, cutoff, CancellationToken.None);
        Assert.DoesNotContain(before, item => item.Metric == "engine.process_physical_memory_bytes");

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(112, Assert.Single(upgrade.Results).Migration.Number.Value);
        var after = await history.ReadAsync(target, 1, cutoff.AddHours(-1), cutoff, cutoff, CancellationToken.None);
        Assert.Single(after, item => item.Metric == "engine.process_physical_memory_bytes");
        Assert.Equal(before.Single(item => item.Metric == "engine.user_connections").Points.ToArray(),
            after.Single(item => item.Metric == "engine.user_connections").Points.ToArray());
        Assert.Equal(before.Single(item => item.Metric == "engine.batch_requests_per_second").Points.ToArray(),
            after.Single(item => item.Metric == "engine.batch_requests_per_second").Points.ToArray());
    }
}
