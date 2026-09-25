using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlVolumeCommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task ExactCapacityReplayAndLeaseLossAreAtomic()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout),
                CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result => result.Outcome == MigrationOutcome.Failed)
                .Select(static result => $"{result.Migration.Number.Value}:{result.FailureCode}")));

        // The production manifest is intentionally not registered yet. A
        // test-only immutable contract exercises the writer without enabling
        // a collector on an installed repository.
        await ExecuteAsync(database, """
            INSERT INTO control.collector_contract
             (collector_id,collector_version,execution_order,manifest_schema_version,
              output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,
              minimum_interval,execution_timeout,maximum_rows,maximum_response_bytes,
              estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
            VALUES ('storage.volume',1,999,3,1,decode(repeat('a',64),'hex'),
                    decode(repeat('b',64),'hex'),interval '2 minutes',interval '1 minute',
                    interval '5 seconds',1000,4194304,'moderate',2,3,interval '5 minutes');
            """);

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity lease = await AcquireAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout),
                CancellationToken.None)).Status);

        CollectorPayload payload = Volumes(work, long.MaxValue - 1);
        CommitCollectorRunRequest request = Success(work, runId, lease, payload);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        await using (var count = database.DataSource.CreateCommand("""
            SELECT count(*),max(total_bytes),max(available_bytes),
                   count(*) FILTER (WHERE total_bytes IS NULL AND available_bytes IS NULL)
            FROM telemetry.sql_volume_snapshot WHERE run_id=@run;
            """))
        {
            count.Parameters.AddWithValue("run", runId.Value);
            await using NpgsqlDataReader reader = await count.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(long.MaxValue, reader.GetInt64(1));
            Assert.Equal(long.MaxValue - 1, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
        }

        PostgresException divergent = await Assert.ThrowsAsync<PostgresException>(() =>
            runtime.CommitRunAsync(Success(work, runId, lease, Volumes(work, long.MaxValue - 2)),
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
            (await runtime.CommitRunAsync(Success(lostWork, lostRun, lostLease,
                Volumes(lostWork, 10)), CancellationToken.None)).Status);
        Assert.Equal(0L, await CountRowsAsync(database, lostRun));
        await using var outcome = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;");
        outcome.Parameters.AddWithValue("run", lostRun.Value);
        Assert.Equal(0L, await outcome.ExecuteScalarAsync());
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
            VALUES (@target,@key,'Volume commit target','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule SET enabled=false
            WHERE instance_id=@target AND collector_id<>'storage.volume';
            """);
        seed.Parameters.AddWithValue("target", target);
        seed.Parameters.AddWithValue("key", $"volume.commit.{target:N}");
        await seed.ExecuteNonQueryAsync();
        return Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target && item.CollectorId.Value == "storage.volume");
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(
        PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/storage.volume/{work.TargetId.Value:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquisition.Status);
        return Assert.IsType<WorkerLease>(acquisition.Lease).Identity;
    }

    private static CollectorPayload Volumes(CollectorDueWorkItem work, long available) =>
        new(sqlVolumes: new SqlVolumeObservationBatch([
            new SqlVolumeObservation(work.TargetId, work.TargetRevision, new string('a', 64),
                SqlVolumeIdentityKind.VolumeId, 3, long.MaxValue, available, work.RepositoryTimeUtc),
            new SqlVolumeObservation(work.TargetId, work.TargetRevision, new string('b', 64),
                SqlVolumeIdentityKind.FileScopedUnknown, 1, null, null, work.RepositoryTimeUtc),
        ]));

    private static CommitCollectorRunRequest Success(
        CollectorDueWorkItem work, CollectorRunId runId, WorkerLeaseIdentity lease, CollectorPayload payload)
    {
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(4, payload.ItemCount,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<long> CountRowsAsync(RepositoryTestDatabase database, CollectorRunId runId)
    {
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.sql_volume_snapshot WHERE run_id=@run;");
        count.Parameters.AddWithValue("run", runId.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
