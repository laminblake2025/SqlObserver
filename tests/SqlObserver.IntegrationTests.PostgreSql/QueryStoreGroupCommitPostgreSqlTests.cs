using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
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
        (DateTimeOffset firstStart, DateTimeOffset firstEnd,
            DateTimeOffset? firstCursor, bool firstGap) = await ReadWindowAsync(collector, target);
        Assert.Null(firstCursor);
        Assert.False(firstGap);
        Assert.Equal(TimeSpan.FromHours(2), firstEnd - firstStart);
        await using (var privilege = collector.CreateCommand("""
            SELECT has_table_privilege(current_user,
              'control.query_store_group_window_cursor'::regclass,'SELECT');
            """))
            Assert.False((bool)(await privilege.ExecuteScalarAsync())!);
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
        Assert.Equal(work.RepositoryTimeUtc,
            (await ReadWindowAsync(collector, target)).CursorEnd);
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
        Assert.Equal(partialWork.RepositoryTimeUtc,
            (await ReadWindowAsync(collector, target)).CursorEnd);
        await using (var cursor = database.DataSource.CreateCommand("""
            SELECT last_runtime_complete FROM control.query_store_group_window_cursor
            WHERE instance_id=@target AND target_revision=1;
            """))
        {
            cursor.Parameters.AddWithValue("target", target);
            Assert.False((bool)(await cursor.ExecuteScalarAsync())!);
        }
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
        CollectorDueWorkItem emptyWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "queries.performance-groups");
        WorkerLeaseIdentity emptyLease = await AcquireAsync(leases, emptyWork);
        var emptyRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                emptyWork, emptyRun, emptyLease, Timeout), CancellationToken.None)).Status);
        CollectorRunCommitResult empty = await runtime.CommitRunAsync(
            EmptySuccess(emptyWork, emptyRun, emptyLease), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, empty.Status);
        Assert.Equal(0, empty.InsertedCount);
        Assert.Equal(0L, await CountDeltasAsync(database, emptyRun));
        Assert.Equal(emptyWork.RepositoryTimeUtc,
            (await ReadWindowAsync(collector, target)).CursorEnd);
        await using (var cursor = database.DataSource.CreateCommand("""
            SELECT last_runtime_complete,last_run_id FROM control.query_store_group_window_cursor
            WHERE instance_id=@target AND target_revision=1;
            """))
        {
            cursor.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader reader = await cursor.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.Equal(emptyRun.Value, reader.GetGuid(1));
        }

        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(emptyLease, Timeout), CancellationToken.None);
        await MarkDueAsync(database, target);
        CollectorDueWorkItem mixedWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "queries.performance-groups");
        WorkerLeaseIdentity mixedLease = await AcquireAsync(leases, mixedWork);
        var mixedRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                mixedWork, mixedRun, mixedLease, Timeout), CancellationToken.None)).Status);
        CommitCollectorRunRequest mixedRequest = MixedCoverage(
            mixedWork, mixedRun, mixedLease, incarnation);
        CollectorRunCommitResult mixed = await runtime.CommitRunAsync(mixedRequest, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, mixed.Status);
        Assert.Equal(2, mixed.InsertedCount);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(mixedRequest, CancellationToken.None)).Status);
        await using (var coverage = database.DataSource.CreateCommand("""
            SELECT database_id,wait_transition FROM events.query_store_group_delta
            WHERE run_id=@run ORDER BY database_id;
            """))
        {
            coverage.Parameters.AddWithValue("run", mixedRun.Value);
            await using NpgsqlDataReader reader = await coverage.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(6, reader.GetInt32(0));
            Assert.Equal("baseline_unavailable", reader.GetString(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(7, reader.GetInt32(0));
            Assert.Equal("unavailable", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        }

        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(mixedLease, Timeout), CancellationToken.None);
        await MarkDueAsync(database, target);
        CollectorDueWorkItem truncatedWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "queries.performance-groups");
        WorkerLeaseIdentity truncatedLease = await AcquireAsync(leases, truncatedWork);
        var truncatedRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                truncatedWork, truncatedRun, truncatedLease, Timeout), CancellationToken.None)).Status);
        CollectorRunCommitResult truncated = await runtime.CommitRunAsync(
            MixedCoverage(truncatedWork, truncatedRun, truncatedLease, incarnation,
                truncatedWaits: true), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, truncated.Status);
        await using (var evidence = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run),
                   (SELECT count(*) FROM events.query_store_group_delta WHERE run_id=@run
                    AND database_id=7 AND wait_transition='unavailable');
            """))
        {
            evidence.Parameters.AddWithValue("run", truncatedRun.Value);
            await using NpgsqlDataReader reader = await evidence.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(truncatedLease, Timeout), CancellationToken.None);
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var expire = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                UPDATE control.query_store_group_window_cursor
                SET last_window_start=@start,last_window_end=@end
                WHERE instance_id=@target AND target_revision=1;
                """, connection, transaction);
            DateTimeOffset expiredEnd = DateTimeOffset.UtcNow.AddDays(-8);
            expire.Parameters.AddWithValue("scope", target.ToString());
            expire.Parameters.AddWithValue("target", target);
            expire.Parameters.AddWithValue("start", expiredEnd.AddMinutes(-5));
            expire.Parameters.AddWithValue("end", expiredEnd);
            await expire.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        (DateTimeOffset gapStart, _, DateTimeOffset? expiredCursor,
            bool gapBeforeWindow) = await ReadWindowAsync(collector, target);
        Assert.True(gapBeforeWindow);
        Assert.True(gapStart > expiredCursor);
        await MarkDueAsync(database, target);
        CollectorDueWorkItem gapWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "queries.performance-groups");
        WorkerLeaseIdentity gapLease = await AcquireAsync(leases, gapWork);
        var gapRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                gapWork, gapRun, gapLease, Timeout), CancellationToken.None)).Status);
        PostgresException invisibleGap = await Assert.ThrowsAsync<PostgresException>(() =>
            runtime.CommitRunAsync(Success(gapWork, gapRun, gapLease, incarnation, 120),
                CancellationToken.None).AsTask());
        Assert.Equal("22023", invisibleGap.SqlState);
        Assert.Equal(0L, await CountDeltasAsync(database, gapRun));
        CollectorRunCommitResult visibleGap = await runtime.CommitRunAsync(
            PartialIncomplete(gapWork, gapRun, gapLease), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, visibleGap.Status);
        Assert.Equal(gapWork.RepositoryTimeUtc,
            (await ReadWindowAsync(collector, target)).CursorEnd);
        await using (var gap = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run;"))
        {
            gap.Parameters.AddWithValue("run", gapRun.Value);
            Assert.Equal(1L, await gap.ExecuteScalarAsync());
        }
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(gapLease, Timeout), CancellationToken.None);
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

    private static CommitCollectorRunRequest EmptySuccess(CollectorDueWorkItem work,
        CollectorRunId runId, WorkerLeaseIdentity lease)
    {
        DateTimeOffset now = work.RepositoryTimeUtc;
        var groups = new QueryStoreGroupOutputBatch(work.TargetId, work.TargetRevision,
            now.AddMinutes(-5), now, true, QueryStoreGroupWaitCoverage.Complete, []);
        var payload = new CollectorPayload(queryStoreGroups: groups);
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(0, 0,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<(DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
        DateTimeOffset? CursorEnd, bool GapBeforeWindow)> ReadWindowAsync(
        NpgsqlDataSource collector, Guid target)
    {
        QueryStoreGroupSourceWindow window = await new PostgreSqlQueryStoreGroupWindowPort(
            collector).ReadNextAsync(new MonitoredInstanceId(target),
            new ObservationTargetRevision(1), Timeout, CancellationToken.None);
        return (window.FromUtc, window.ToUtc, window.CursorEndUtc,
            window.GapBeforeWindow);
    }

    private static CommitCollectorRunRequest MixedCoverage(CollectorDueWorkItem work,
        CollectorRunId runId, WorkerLeaseIdentity lease, Guid incarnation,
        bool truncatedWaits = false)
    {
        DateTimeOffset now = work.RepositoryTimeUtc;
        QueryStoreGroupObservation first = Assert.Single(Success(work, runId, lease,
            incarnation, 110).Payload.QueryStoreGroups!.Items);
        var query = new QueryOpaqueIdentity(7, new string('c', 64));
        var plan = new PlanOpaqueIdentity(query, new string('d', 64));
        var key = new QueryStoreRuntimeWatermarkKey(work.TargetId, work.TargetRevision,
            incarnation, query, plan, now.AddHours(-2), 1,
            now.AddHours(-1), now.AddHours(1), 0);
        var second = new QueryStoreGroupObservation(new QueryStoreRuntimeWatermark(key,
            new QueryStoreRuntimeCounters(20, 40, 20, 20, 20, 20),
            now.AddMinutes(-10), now.AddMinutes(-4), now.AddSeconds(-1)), null);
        var groups = new QueryStoreGroupOutputBatch(work.TargetId, work.TargetRevision,
            now.AddMinutes(-5), now, true, QueryStoreGroupWaitCoverage.Mixed,
            [first, second], new Dictionary<int, QueryStoreGroupWaitCoverage>
            {
                [6] = QueryStoreGroupWaitCoverage.Complete,
                [7] = truncatedWaits
                    ? QueryStoreGroupWaitCoverage.Truncated
                    : QueryStoreGroupWaitCoverage.CaptureOff,
            });
        var payload = new CollectorPayload(queryStoreGroups: groups);
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            truncatedWaits ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
            truncatedWaits ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(4, payload.ItemCount,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            truncatedWaits
                ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false)
                : CollectorLossEvidence.None);
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
