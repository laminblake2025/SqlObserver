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
public sealed class QueryStoreGroupCommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task GroupCommitReplayAndLeaseLossAreAtomic()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout),
                CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result => result.Outcome == MigrationOutcome.Failed)
                .Select(static result => $"{result.Migration.Number.Value}:{result.FailureCode}")));

        Guid target = Guid.NewGuid();
        Guid incarnation = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.collector_contract
             (collector_id,collector_version,execution_order,manifest_schema_version,
              output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,
              minimum_interval,execution_timeout,maximum_rows,maximum_response_bytes,
              estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
            VALUES ('queries.performance-groups',1,17,6,1,
              decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),
              interval '5 minutes',interval '1 minute',interval '5 seconds',2000,8388608,
              'moderate',2,3,interval '5 minutes');
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Query Store group commit target','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET enabled=(collector_id='queries.performance-groups')
            WHERE instance_id=@target;
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"query.group.commit.{target:N}");
            await seed.ExecuteNonQueryAsync();
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "queries.performance-groups");
        WorkerLeaseIdentity lease = await AcquireAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout),
                CancellationToken.None)).Status);

        CommitCollectorRunRequest request = Success(work, runId, lease, incarnation, 100);
        CollectorRunCommitResult committed = await runtime.CommitRunAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
        Assert.Equal(1, committed.InsertedCount);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        await using (var evidence = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM events.query_store_group_watermark WHERE instance_id=@target),
                   (SELECT count(*) FROM events.query_store_group_delta WHERE run_id=@run),
                   (SELECT runtime_transition FROM events.query_store_group_delta WHERE run_id=@run),
                   (SELECT cpu_ms FROM events.query_store_group_delta WHERE run_id=@run);
            """))
        {
            evidence.Parameters.AddWithValue("target", target);
            evidence.Parameters.AddWithValue("run", runId.Value);
            await using NpgsqlDataReader reader = await evidence.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal("baseline_unavailable", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));
        }

        PostgresException divergent = await Assert.ThrowsAsync<PostgresException>(() =>
            runtime.CommitRunAsync(Success(work, runId, lease, incarnation, 101),
                CancellationToken.None).AsTask());
        Assert.Equal("40001", divergent.SqlState);
        Assert.Equal(1L, await CountDeltasAsync(database, runId));

        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
        await MarkDueAsync(database, target);
        CollectorDueWorkItem partialWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "queries.performance-groups");
        WorkerLeaseIdentity partialLease = await AcquireAsync(leases, partialWork);
        var partialRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                partialWork, partialRun, partialLease, Timeout), CancellationToken.None)).Status);
        CollectorRunCommitResult partial = await runtime.CommitRunAsync(
            PartialIncomplete(partialWork, partialRun, partialLease), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, partial.Status);
        Assert.Equal(0, partial.PersistedBytes);
        Assert.Equal(0L, await CountDeltasAsync(database, partialRun));
        await using (var unchanged = database.DataSource.CreateCommand("""
            SELECT count(*),max(cpu_ms) FROM events.query_store_group_watermark
            WHERE instance_id=@target;
            """))
        {
            unchanged.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader reader = await unchanged.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(100L, reader.GetInt64(1));
        }

        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(partialLease, Timeout), CancellationToken.None);
        await MarkDueAsync(database, target);
        CollectorDueWorkItem lostWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "queries.performance-groups");
        WorkerLeaseIdentity lostLease = await AcquireAsync(leases, lostWork);
        var lostRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(lostWork, lostRun, lostLease, Timeout),
                CancellationToken.None)).Status);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lostLease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost,
            (await runtime.CommitRunAsync(Success(lostWork, lostRun, lostLease, incarnation, 110),
                CancellationToken.None)).Status);
        Assert.Equal(0L, await CountDeltasAsync(database, lostRun));
        await using var outcome = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;");
        outcome.Parameters.AddWithValue("run", lostRun.Value);
        Assert.Equal(0L, await outcome.ExecuteScalarAsync());
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(
        PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/queries.performance-groups/{work.TargetId.Value:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquisition.Status);
        return Assert.IsType<WorkerLease>(acquisition.Lease).Identity;
    }

    private static CommitCollectorRunRequest Success(CollectorDueWorkItem work,
        CollectorRunId runId, WorkerLeaseIdentity lease, Guid incarnation, long cpu)
    {
        DateTimeOffset now = work.RepositoryTimeUtc;
        var query = new QueryOpaqueIdentity(6, new string('a', 64));
        var plan = new PlanOpaqueIdentity(query, new string('b', 64));
        var key = new QueryStoreRuntimeWatermarkKey(work.TargetId, work.TargetRevision,
            incarnation, query, plan, now.AddHours(-2), 1,
            now.AddHours(-1), now.AddHours(1), 0);
        var runtime = new QueryStoreRuntimeWatermark(key,
            new QueryStoreRuntimeCounters(cpu, cpu * 2, cpu, cpu, cpu, cpu),
            now.AddMinutes(-10), now.AddMinutes(-4), now.AddSeconds(-1));
        var waits = new QueryStoreWaitWatermark(key,
            new Dictionary<int, long> { [3] = cpu }, now.AddSeconds(-1));
        var groups = new QueryStoreGroupOutputBatch(work.TargetId, work.TargetRevision,
            now.AddMinutes(-5), now, true, QueryStoreGroupWaitCoverage.Complete,
            [new QueryStoreGroupObservation(runtime, waits)]);
        var payload = new CollectorPayload(queryStoreGroups: groups);
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(2, payload.ItemCount,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static CommitCollectorRunRequest PartialIncomplete(CollectorDueWorkItem work,
        CollectorRunId runId, WorkerLeaseIdentity lease)
    {
        DateTimeOffset now = work.RepositoryTimeUtc;
        var groups = new QueryStoreGroupOutputBatch(work.TargetId, work.TargetRevision,
            now.AddMinutes(-5), now, false, QueryStoreGroupWaitCoverage.Unavailable, []);
        var payload = new CollectorPayload(queryStoreGroups: groups);
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(4_259, 0,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false));
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task MarkDueAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var due = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET next_due_at=clock_timestamp()
            WHERE instance_id=@target AND collector_id='queries.performance-groups';
            """);
        due.Parameters.AddWithValue("target", target);
        await due.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountDeltasAsync(RepositoryTestDatabase database, CollectorRunId runId)
    {
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM events.query_store_group_delta WHERE run_id=@run;");
        count.Parameters.AddWithValue("run", runId.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
