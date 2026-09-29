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
public sealed class CollectionRunWriterCutoverTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task WriterRouteRequiresFkCutoverAndMirrorsBegunRunForRollback()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        async Task SetMigratorAsync(NpgsqlTransaction transaction)
        {
            await using var role = new NpgsqlCommand(
                "SET LOCAL ROLE sqlobserver_migrator;", admin, transaction);
            await role.ExecuteNonQueryAsync();
        }
        await using (NpgsqlTransaction denied = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(denied);
            await using var activate = new NpgsqlCommand(
                "SELECT control.set_collection_run_v2_writer_route(true,10000);",
                admin, denied);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => activate.ExecuteScalarAsync())).SqlState);
            await denied.RollbackAsync();
        }

        var children = new List<string>();
        await using (var inventory = new NpgsqlCommand(
            "SELECT child_relation::text FROM control.collection_run_identity_fk_targets() ORDER BY child_relation::oid;",
            admin))
        await using (NpgsqlDataReader rows = await inventory.ExecuteReaderAsync())
            while (await rows.ReadAsync()) children.Add(rows.GetString(0));
        Assert.Equal(20, children.Count);
        await using (NpgsqlTransaction stage = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(stage);
            foreach (string child in children)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT control.stage_collection_run_identity_fk(@child::regclass,10000);",
                    admin, stage);
                command.Parameters.AddWithValue("child", child);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            await stage.CommitAsync();
        }
        await using (NpgsqlTransaction validate = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(validate);
            foreach (string child in children)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT control.validate_collection_run_identity_fk(@child::regclass);",
                    admin, validate);
                command.Parameters.AddWithValue("child", child);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            await validate.CommitAsync();
        }
        await using (NpgsqlTransaction cutover = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(cutover);
            await using (var childrenCutover = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);", admin, cutover))
                Assert.Equal(20, await childrenCutover.ExecuteScalarAsync());
            await using (var readRoute = new NpgsqlCommand(
                "SELECT control.set_collection_run_v2_scheduler_read_route(true,10000);",
                admin, cutover))
                Assert.Equal(1L, await readRoute.ExecuteScalarAsync());
            await using (var writerRoute = new NpgsqlCommand(
                "SELECT control.set_collection_run_v2_writer_route(true,10000);",
                admin, cutover))
                Assert.Equal(1L, await writerRoute.ExecuteScalarAsync());
            await cutover.CommitAsync();
        }

        Guid target = Guid.NewGuid(), run = Guid.NewGuid(), owner = Guid.NewGuid();
        string workKey = $"collector/run/engine.core/{target:N}";
        await using (var insertTarget = new NpgsqlCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,
              connect_timeout,authentication_mode,transport_security_mode,
              lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'V2 writer target','sql01',1433,
             interval '5 seconds','windows_integrated_service_identity',
             'mandatory_validated','active',1,now(),now(),now());
            UPDATE control.collector_schedule
            SET next_due_at=statement_timestamp()-interval '1 second'
            WHERE instance_id=@target AND collector_id='engine.core';
            """, admin))
        {
            insertTarget.Parameters.AddWithValue("target", target);
            await insertTarget.ExecuteNonQueryAsync();
        }
        Guid unmirrored = Guid.NewGuid();
        await using (var originalOnly = new NpgsqlCommand("""
            INSERT INTO telemetry.collection_run
             (run_id,instance_id,collector_id,collector_version,
              output_schema_version,target_revision,schedule_revision,
              work_key,owner_execution_id,fencing_token,request_digest,
              scheduled_for,started_at)
            VALUES(@run,@target,'engine.core',1,1,1,1,'test/original-only',
             gen_random_uuid(),1,decode(repeat('cc',32),'hex'),
             statement_timestamp(),statement_timestamp());
            """, admin))
        {
            originalOnly.Parameters.AddWithValue("run", unmirrored);
            originalOnly.Parameters.AddWithValue("target", target);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => originalOnly.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target::text,false);", admin))
        {
            scope.Parameters.AddWithValue("target", target);
            await scope.ExecuteScalarAsync();
        }
        long scheduleRevision;
        DateTimeOffset due;
        await using (var schedule = new NpgsqlCommand("""
            SELECT schedule_revision,next_due_at
            FROM control.collector_schedule
            WHERE instance_id=@target AND collector_id='engine.core';
            """, admin))
        {
            schedule.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await schedule.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            scheduleRevision = row.GetInt64(0);
            due = row.GetFieldValue<DateTimeOffset>(1);
        }
        long fence;
        await using (var leaseCommand = new NpgsqlCommand("""
            SELECT acquired,fencing_token FROM control.acquire_worker_lease(
             @work,@owner,interval '5 minutes');
            """, admin))
        {
            leaseCommand.Parameters.AddWithValue("work", workKey);
            leaseCommand.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader row = await leaseCommand.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            fence = row.GetInt64(1);
        }
        await using (var begin = new NpgsqlCommand("""
            SELECT result_status FROM control.begin_collection_run(
             @run,@target,1::bigint,'engine.core',1,1,@revision,@due,
             @work,@owner,@fence,decode(repeat('aa',32),'hex'));
            """, admin))
        {
            begin.Parameters.AddWithValue("run", run);
            begin.Parameters.AddWithValue("target", target);
            begin.Parameters.AddWithValue("revision", scheduleRevision);
            begin.Parameters.AddWithValue("due", due);
            begin.Parameters.AddWithValue("work", workKey);
            begin.Parameters.AddWithValue("owner", owner);
            begin.Parameters.AddWithValue("fence", fence);
            Assert.Equal("started", await begin.ExecuteScalarAsync());
        }
        await using (var mirrored = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run);
            """, admin))
        {
            mirrored.Parameters.AddWithValue("run", run);
            await using NpgsqlDataReader row = await mirrored.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(1L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
        }

        Guid committedTarget = Guid.NewGuid();
        await using (var insertTarget = new NpgsqlCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,
              connect_timeout,authentication_mode,transport_security_mode,
              lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'V2 commit target','sql02',1433,
             interval '5 seconds','windows_integrated_service_identity',
             'mandatory_validated','active',1,now(),now(),now());
            UPDATE control.collector_schedule
            SET next_due_at=statement_timestamp()-interval '1 second'
            WHERE instance_id=@target AND collector_id='engine.core';
            """, admin))
        {
            insertTarget.Parameters.AddWithValue("target", committedTarget);
            await insertTarget.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None)).Items, item =>
                item.TargetId.Value == committedTarget &&
                item.CollectorId.Value == "engine.core");
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/engine.core/{committedTarget:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        var committedRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                work, committedRun, lease,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
                CancellationToken.None)).Status);
        var summary = new CollectorRunSummary(
            committedRun, work.TargetId, work.TargetRevision, work.CollectorId,
            work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.TransientFailure,
            CollectorRunReason.TransientTargetFailure,
            TimeSpan.FromMilliseconds(1), 1,
            new CollectorRunAccounting(0, 0, 0, 0),
            CollectorLossEvidence.None);
        var commit = new CommitCollectorRunRequest(
            work, summary, CollectorPayload.Empty,
            new CollectorCircuitSnapshot(CollectorCircuitState.Closed, 1,
                work.RepositoryTimeUtc), lease,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(10)));
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        await using (var completed = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run);
            """, admin))
        {
            completed.Parameters.AddWithValue("run", committedRun.Value);
            await using NpgsqlDataReader row = await completed.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            for (int i = 0; i < 4; i++) Assert.Equal(1L, row.GetInt64(i));
        }

        await using (NpgsqlTransaction parity = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(parity);
            foreach ((Guid parityTarget, Guid parityRun) in new[]
                { (target, run), (committedTarget, committedRun.Value) })
            {
                await using (var scope = new NpgsqlCommand(
                    "SELECT set_config('sqlobserver.target_scope',@target::text,true);",
                    admin, parity))
                {
                    scope.Parameters.AddWithValue("target", parityTarget);
                    await scope.ExecuteScalarAsync();
                }
                foreach (string function in new[]
                    { "backfill_collection_run_v2", "backfill_collection_run_identity" })
                {
                    await using var copy = new NpgsqlCommand(
                        $"SELECT complete FROM control.{function}(@target,10);", admin, parity);
                    copy.Parameters.AddWithValue("target", parityTarget);
                    Assert.True((bool)(await copy.ExecuteScalarAsync())!);
                }
                await using var certificate = new NpgsqlCommand("""
                    SELECT parity FROM control.record_collection_run_v2_parity_window(
                     @target,'engine.core',
                     date_trunc('day',(SELECT started_at FROM telemetry.collection_run WHERE run_id=@run)),
                     date_trunc('day',(SELECT started_at FROM telemetry.collection_run WHERE run_id=@run))
                       + interval '1 day',10);
                    """, admin, parity);
                certificate.Parameters.AddWithValue("target", parityTarget);
                certificate.Parameters.AddWithValue("run", parityRun);
                Assert.True((bool)(await certificate.ExecuteScalarAsync())!);
            }
            await using var rollback = new NpgsqlCommand(
                "SELECT control.set_collection_run_v2_writer_route(false,10000);",
                admin, parity);
            Assert.Equal(2L, await rollback.ExecuteScalarAsync());
            await parity.CommitAsync();
        }
        Guid afterRollback = Guid.NewGuid();
        await using (NpgsqlTransaction originalWriter = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(originalWriter);
            await using var insert = new NpgsqlCommand("""
                SELECT telemetry.insert_collection_run_runtime(
                 @run,@target,'engine.core',1,1,1,1,'test/after-writer-rollback',
                 gen_random_uuid(),1,decode(repeat('bb',32),'hex'),
                 statement_timestamp(),statement_timestamp());
                """, admin, originalWriter);
            insert.Parameters.AddWithValue("run", afterRollback);
            insert.Parameters.AddWithValue("target", target);
            await insert.ExecuteNonQueryAsync();
            await originalWriter.CommitAsync();
        }
        await using (var mirroredAgain = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run);
            """, admin))
        {
            mirroredAgain.Parameters.AddWithValue("run", afterRollback);
            await using NpgsqlDataReader row = await mirroredAgain.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(1L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
        }
    }
}
