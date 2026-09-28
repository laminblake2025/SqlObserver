using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BufferNodePleCommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task NodeSamplesCommitOnceAndRespectRunAndLeaseFences()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout),
                CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result => result.Outcome == MigrationOutcome.Failed)
                .Select(static result => $"{result.Migration.Number.Value}:{result.FailureCode}")));
        await using (var partition = database.DataSource.CreateCommand(
            "SELECT control.ensure_daily_metric_partition(current_date);"))
            await partition.ExecuteNonQueryAsync();

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity lease = await AcquireAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout),
                CancellationToken.None)).Status);

        CommitCollectorRunRequest request = Success(work, runId, lease, Nodes(work, 42));
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        CollectorPayload duplicateNode = new(metrics:
        [
            request.Payload.Metrics[0],
            new MetricSample(new MetricSampleId(Guid.NewGuid()), work.TargetId,
                new MetricId("engine.buffer_node_page_life_expectancy_seconds"),
                work.RepositoryTimeUtc, 84, [new MetricDimension("node_id", "0")]),
        ]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runtime.CommitRunAsync(Success(work, runId, lease, duplicateNode),
                CancellationToken.None).AsTask());

        await using (var rows = database.DataSource.CreateCommand("""
            SELECT metric_key,dimensions->>'node_id',metric_value,collection_run_id
            FROM telemetry.raw_metric_sample WHERE collection_run_id=@run
            ORDER BY (dimensions->>'node_id')::integer;
            """))
        {
            rows.Parameters.AddWithValue("run", runId.Value);
            await using NpgsqlDataReader reader = await rows.ExecuteReaderAsync();
            for (int node = 0; node < 2; node++)
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("engine.buffer_node_page_life_expectancy_seconds", reader.GetString(0));
                Assert.Equal(node.ToString(System.Globalization.CultureInfo.InvariantCulture), reader.GetString(1));
                Assert.Equal(node == 0 ? 42d : 84d, reader.GetDouble(2));
                Assert.Equal(runId.Value, reader.GetGuid(3));
            }
            Assert.False(await reader.ReadAsync());
        }

        PostgresException divergent = await Assert.ThrowsAsync<PostgresException>(() =>
            runtime.CommitRunAsync(Success(work, runId, lease, Nodes(work, 43)),
                CancellationToken.None).AsTask());
        Assert.Equal("40001", divergent.SqlState);
        Assert.Equal(2L, await CountRowsAsync(database, runId));

        CollectorDueWorkItem lostWork = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity lostLease = await AcquireAsync(leases, lostWork);
        var lostRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(lostWork, lostRun, lostLease, Timeout),
                CancellationToken.None)).Status);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lostLease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost,
            (await runtime.CommitRunAsync(Success(lostWork, lostRun, lostLease, Nodes(lostWork, 10)),
                CancellationToken.None)).Status);
        Assert.Equal(0L, await CountRowsAsync(database, lostRun));
        await using var outcome = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;");
        outcome.Parameters.AddWithValue("run", lostRun.Value);
        Assert.Equal(0L, await outcome.ExecuteScalarAsync());

        CollectorDueWorkItem partialWork = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity partialLease = await AcquireAsync(leases, partialWork);
        var partialRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(partialWork, partialRun,
                partialLease, Timeout), CancellationToken.None)).Status);
        var partialSummary = new CollectorRunSummary(partialRun, partialWork.TargetId,
            partialWork.TargetRevision, partialWork.CollectorId,
            partialWork.CollectorManifestVersion, partialWork.OutputSchemaVersion,
            CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(129, 0, 1000, 0),
            new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, countIsExact: false));
        var partialRequest = new CommitCollectorRunRequest(partialWork, partialSummary,
            CollectorPayload.Empty, CollectorCircuitSnapshot.Closed(partialWork.RepositoryTimeUtc),
            partialLease, Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(partialRequest, CancellationToken.None)).Status);
        Assert.Equal(0L, await CountRowsAsync(database, partialRun));
        await using (var gap = database.DataSource.CreateCommand("""
            SELECT count(*),max(lost_row_count) FROM telemetry.visibility_gap WHERE run_id=@run;
            """))
        {
            gap.Parameters.AddWithValue("run", partialRun.Value);
            await using NpgsqlDataReader reader = await gap.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt32(1));
        }

        await using var privileges = database.DataSource.CreateCommand("""
            SELECT has_table_privilege('sqlobserver_collector',
                'telemetry.raw_metric_sample','INSERT');
            """);
        Assert.False((bool)(await privileges.ExecuteScalarAsync())!);
    }

    private static async Task<CollectorDueWorkItem> PrepareWorkAsync(
        RepositoryTestDatabase database, PostgreSqlCollectorRuntimeRepositoryPort runtime)
    {
        Guid target = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Buffer Node commit target','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET enabled=(collector_id IN ('engine.core','engine.buffer-nodes')),
                last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded'
                                  ELSE last_outcome END
            WHERE instance_id=@target;
            UPDATE control.collector_schedule
            SET enabled=true,next_due_at=statement_timestamp(),updated_at=statement_timestamp()
            WHERE instance_id=@target AND collector_id='engine.buffer-nodes';
            """);
        seed.Parameters.AddWithValue("target", target);
        seed.Parameters.AddWithValue("key", $"buffer.node.commit.{target:N}");
        await seed.ExecuteNonQueryAsync();
        return Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target && item.CollectorId.Value == "engine.buffer-nodes");
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(
        PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/engine.buffer-nodes/{work.TargetId.Value:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquisition.Status);
        return Assert.IsType<WorkerLease>(acquisition.Lease).Identity;
    }

    private static CollectorPayload Nodes(CollectorDueWorkItem work, double first) => new(metrics:
    [
        new MetricSample(new MetricSampleId(Guid.NewGuid()), work.TargetId,
            new MetricId("engine.buffer_node_page_life_expectancy_seconds"),
            work.RepositoryTimeUtc, first, [new MetricDimension("node_id", "0")]),
        new MetricSample(new MetricSampleId(Guid.NewGuid()), work.TargetId,
            new MetricId("engine.buffer_node_page_life_expectancy_seconds"),
            work.RepositoryTimeUtc, 84, [new MetricDimension("node_id", "1")]),
    ]);

    private static CommitCollectorRunRequest Success(CollectorDueWorkItem work, CollectorRunId runId,
        WorkerLeaseIdentity lease, CollectorPayload payload)
    {
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(payload.ItemCount, payload.ItemCount,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<long> CountRowsAsync(RepositoryTestDatabase database, CollectorRunId runId)
    {
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.raw_metric_sample WHERE collection_run_id=@run;");
        count.Parameters.AddWithValue("run", runId.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
