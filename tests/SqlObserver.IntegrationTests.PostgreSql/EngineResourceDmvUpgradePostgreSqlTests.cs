using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M4CollectorPersistenceIntegrationTests
{
    [Fact]
    public async Task CoreResourceUpgradePreservesHistoricalRunsAndAcceptsBoundedDmvMetrics()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new MigrationApplyRequest(112, DefaultTimeout), CancellationToken.None)).HasFailures);
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, target, 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);

        CollectorDueWorkItem oldWork = await DueCoreAsync(database, runtime, target);
        CollectorPayload oldBase = CreateEnginePayload(oldWork);
        CollectorPayload historical = WithMetrics(oldBase, target,
            ("engine.start_time_key", 1));
        await CommitSuccessAsync(runtime, leases, oldWork, historical);
        string before = await ResourceRunSummaryAsync(database, target);

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures, upgrade.Results.FirstOrDefault(x => x.Outcome == MigrationOutcome.Failed)?.FailureCode);
        Assert.Equal(113, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(before, await ResourceRunSummaryAsync(database, target));

        CollectorDueWorkItem newWork = await DueCoreAsync(database, runtime, target);
        CollectorPayload currentBase = CreateEnginePayload(newWork);
        CollectorPayload current = WithMetrics(currentBase, target,
            ("engine.start_time_key", 1),
            ("engine.os_available_memory_bytes", 4 * 1073741824d),
            ("engine.scheduler_runnable_tasks", 3));
        await CommitSuccessAsync(runtime, leases, newWork, current);
        Assert.Equal(20, await ResourceMetricCountAsync(database, target));

        DateTimeOffset cutoff = DateTimeOffset.UtcNow;
        var history = new PostgreSqlOverviewHistoryPort(server);
        var series = await history.ReadAsync(target, 1, cutoff.AddHours(-1), cutoff.AddSeconds(1), cutoff, CancellationToken.None);
        Assert.Equal(4d, Assert.Single(series.Single(x => x.Metric == "engine.os_available_memory_bytes").Points).Value);
        Assert.Equal(3d, Assert.Single(series.Single(x => x.Metric == "engine.scheduler_runnable_tasks").Points).Value);
        Assert.Equal("GiB", series.Single(x => x.Metric == "engine.os_available_memory_bytes").Unit);
        Assert.Equal("tasks", series.Single(x => x.Metric == "engine.scheduler_runnable_tasks").Unit);

        await using var grants = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator',p.prosecdef,
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              NOT EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='control' AND p.proname='commit_collection_run';
            """);
        await using NpgsqlDataReader reader = await grants.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int i = 0; i < 5; i++) Assert.True(reader.GetBoolean(i));
        Assert.False(await reader.ReadAsync());
    }

    private static CollectorPayload WithMetrics(CollectorPayload payload, MonitoredInstanceId target,
        params (string Key, double Value)[] extra)
    {
        DateTimeOffset observed = payload.Metrics[0].ObservedAtUtc;
        return new CollectorPayload(payload.Metrics.Concat(extra.Select(item =>
            new MetricSample(new MetricSampleId(Guid.NewGuid()), target, new MetricId(item.Key), observed, item.Value))).ToArray());
    }

    private static async Task<CollectorDueWorkItem> DueCoreAsync(RepositoryTestDatabase database,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, MonitoredInstanceId target)
    {
        await MakeDueAsync(database, target, "engine.core");
        return Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items,
            item => item.TargetId == target && item.CollectorId.Value == "engine.core");
    }

    private static async Task<string> ResourceRunSummaryAsync(RepositoryTestDatabase database, MonitoredInstanceId target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT coalesce(jsonb_agg(jsonb_build_object('run',r.run_id,'items',o.output_item_count,'digest',encode(o.completion_digest,'hex')) ORDER BY r.run_id)::text,'[]')
            FROM telemetry.collection_run r JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
            WHERE r.instance_id=@target AND r.collector_id='engine.core';
            """);
        command.Parameters.AddWithValue("target", target.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ResourceMetricCountAsync(RepositoryTestDatabase database, MonitoredInstanceId target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT count(*) FROM telemetry.raw_metric_sample s
            JOIN telemetry.collection_run r ON r.run_id=s.collection_run_id
            WHERE r.instance_id=@target AND r.collector_id='engine.core';
            """);
        command.Parameters.AddWithValue("target", target.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
