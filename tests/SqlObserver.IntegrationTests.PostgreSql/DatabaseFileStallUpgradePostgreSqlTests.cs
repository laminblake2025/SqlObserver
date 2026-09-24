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
    public async Task CoreCommitAcceptsStartMarkerAfterFileStallUpgrade()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, target, 1);
        await MakeDueAsync(database, target, "engine.core");
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = Assert.Single(
            (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items,
            item => item.TargetId == target && item.CollectorId.Value == "engine.core");
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, DefaultTimeout), CancellationToken.None)).Status);

        CollectorPayload basePayload = CreateEnginePayload(work);
        MetricSample marker = new(new MetricSampleId(Guid.NewGuid()), target,
            new MetricId("engine.start_time_key"), basePayload.Metrics[0].ObservedAtUtc, 1);
        CollectorPayload payload = new(basePayload.Metrics.Append(marker).ToArray());
        CollectorRunCommitResult result = await runtime.CommitRunAsync(
            CreateSuccessCommit(work, run, lease, payload), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);

        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.raw_metric_sample WHERE instance_id=@target AND collection_run_id=@run");
        count.Parameters.AddWithValue("target", target.Value);
        count.Parameters.AddWithValue("run", run.Value);
        Assert.Equal(9L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task FileStallUpgradePreservesLegacyReplayAndResumesBackfillAtChangedRowShape()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new MigrationApplyRequest(83, DefaultTimeout), CancellationToken.None)).HasFailures);
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, target, 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CommitCollectorRunRequest? fileCommit = null;
        foreach (string collectorId in new[] { "engine.core", "database.inventory", "database.files" })
        {
            CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items,
                item => item.CollectorId.Value == collectorId);
            WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
            var run = new CollectorRunId(Guid.NewGuid());
            Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, DefaultTimeout), CancellationToken.None)).Status);
            CollectorPayload payload = collectorId switch
            {
                "engine.core" => CreateEnginePayload(work),
                "database.inventory" => CreateDatabasePayload(work, 5),
                _ => CreateFilePayloads(work, 1, 2),
            };
            CommitCollectorRunRequest commit = CreateSuccessCommit(work, run, lease, payload);
            Assert.Equal("committed", await ExecuteFileStallCommitAsync(collector, commit, legacy: true));
            if (collectorId == "database.files") fileCommit = commit;
            else await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, DefaultTimeout), CancellationToken.None);
        }
        Assert.NotNull(fileCommit);
        const string outcomeSql = "SELECT jsonb_agg(to_jsonb(o) ORDER BY o.run_id)::text FROM telemetry.collection_run_outcome o;";
        string outcomes = await FileStallScalarAsync(database, outcomeSql);
        string snapshot = await FileStallScalarAsync(database, "SELECT jsonb_agg(to_jsonb(s) ORDER BY s.snapshot_id)::text FROM telemetry.database_file_snapshot s;");
        DateTimeOffset observed = fileCommit.Payload.DatabaseFiles.Items[0].ObservedAtUtc;
        DateOnly day = DateOnly.FromDateTime(observed.UtcDateTime);
        // Pause the real backfill after one of two equal-time rows. The mirror
        // has no uniqueness constraint, so a cursor reset would duplicate it.
        await using (var backfill = collector.CreateCommand("SELECT rows_copied FROM control.run_m10_backfill('telemetry','database_file_snapshot_v2',@day,1,1048576);"))
        {
            backfill.Parameters.AddWithValue("day", day);
            Assert.Equal(1, (int)(await backfill.ExecuteScalarAsync())!);
        }
        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures, upgrade.Results.FirstOrDefault(item => item.Outcome == MigrationOutcome.Failed)?.FailureCode);
        Assert.Equal(84, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(snapshot, await FileStallScalarAsync(database, "SELECT jsonb_agg(to_jsonb(s)-'io_stall_read_ms'-'io_stall_write_ms' ORDER BY s.snapshot_id)::text FROM telemetry.database_file_snapshot s;"));
        Assert.Equal(outcomes, await FileStallScalarAsync(database, outcomeSql));
        Assert.Equal("replayed", await ExecuteFileStallCommitAsync(collector, fileCommit, legacy: true));
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(fileCommit, CancellationToken.None)).Status);
        Assert.Equal(outcomes, await FileStallScalarAsync(database, outcomeSql));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        DatabaseFileHealthPage page = Assert.IsType<DatabaseFileHealthPage>(await new PostgreSqlHealthProjectionPort(server).ListDatabaseFileHealthAsync(
            new ListDatabaseFileHealthRepositoryRequest(target, 100, null, DefaultTimeout), CancellationToken.None));
        Assert.Equal(2, page.Items.Count);
        DatabaseFileObservation file = page.Items[0].Observation;
        Assert.Equal(3, file.IoStallMilliseconds);
        Assert.Null(file.ReadStallMilliseconds);
        Assert.Null(file.WriteStallMilliseconds);
        Assert.Equal(fileCommit.Payload.DatabaseFiles.Items[0].EstimatedSizeBytes, file.EstimatedSizeBytes);

        await using (var backfill = collector.CreateCommand("SELECT rows_copied FROM control.run_m10_backfill('telemetry','database_file_snapshot_v2',@day,100,1048576);"))
        {
            backfill.Parameters.AddWithValue("day", day);
            Assert.Equal(1, (int)(await backfill.ExecuteScalarAsync())!);
            Assert.Equal(0, (int)(await backfill.ExecuteScalarAsync())!);
        }
        Assert.Equal(await FileStallScalarAsync(database, "SELECT jsonb_agg(to_jsonb(s) ORDER BY s.snapshot_id)::text FROM telemetry.database_file_snapshot s;"),
            await FileStallScalarAsync(database, "SELECT jsonb_agg(to_jsonb(s) ORDER BY s.snapshot_id)::text FROM telemetry.database_file_snapshot_v2 s;"));
    }

    [Fact]
    public async Task FileStallFunctionsRetainOwnerAclAndMirrorShape()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using var command = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator',p.prosecdef,
              has_function_privilege(CASE WHEN p.proname='commit_collection_run_v2' THEN 'sqlobserver_collector' ELSE 'sqlobserver_server' END,p.oid,'EXECUTE'),
              NOT has_function_privilege(CASE WHEN p.proname='commit_collection_run_v2' THEN 'sqlobserver_server' ELSE 'sqlobserver_collector' END,p.oid,'EXECUTE'),
              NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              NOT EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE'),
              'search_path=pg_catalog'=ANY(p.proconfig),'TimeZone=UTC'=ANY(p.proconfig)
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE (n.nspname='control' AND p.proname='commit_collection_run_v2')
               OR (n.nspname='reporting' AND p.proname='list_database_file_health_v2');
            """);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        int count = 0;
        while (await reader.ReadAsync()) { count++; for (int i = 0; i < 8; i++) Assert.True(reader.GetBoolean(i)); }
        Assert.Equal(2, count);
    }

    private static async Task<string> FileStallScalarAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
}
