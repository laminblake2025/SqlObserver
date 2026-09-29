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
public sealed class CollectionRunRollbackSealTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task SealRequiresAllRoutesAndStopsOriginalMirroringPermanently()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();

        await using (NpgsqlTransaction denied = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, denied);
            await using var command = new NpgsqlCommand(
                "SELECT control.seal_collection_run_original_rollback(10000);", admin, denied);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteScalarAsync())).SqlState);
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
            await SetMigratorAsync(admin, stage);
            foreach (string child in children)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT control.stage_collection_run_identity_fk(@child::regclass,10000);", admin, stage);
                command.Parameters.AddWithValue("child", child);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            await stage.CommitAsync();
        }
        await using (NpgsqlTransaction validate = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, validate);
            foreach (string child in children)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT control.validate_collection_run_identity_fk(@child::regclass);", admin, validate);
                command.Parameters.AddWithValue("child", child);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            await validate.CommitAsync();
        }

        DateTimeOffset sealedAt;
        await using (NpgsqlTransaction activate = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, activate);
            await using (var cutover = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);", admin, activate))
                Assert.Equal(20, await cutover.ExecuteScalarAsync());
            foreach (string route in new[]
            {
                "activity", "health", "m9", "m10", "overview", "ple",
                "query_catalog", "scheduler", "sql_volume", "wait", "writer"
            })
            {
                // This fixed test inventory is deliberately not supplied by a
                // caller; the production seal verifies its own route list.
                await using var command = new NpgsqlCommand(
                    $"SELECT control.set_collection_run_v2_{route}_read_route(true,10000);",
                    admin, activate);
                if (route == "writer") command.CommandText =
                    "SELECT control.set_collection_run_v2_writer_route(true,10000);";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
            await using var seal = new NpgsqlCommand(
                "SELECT control.seal_collection_run_original_rollback(10000);", admin, activate);
            sealedAt = new DateTimeOffset((DateTime)(await seal.ExecuteScalarAsync())!);
            await activate.CommitAsync();
        }

        Guid target = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,
              connect_timeout,authentication_mode,transport_security_mode,
              lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'sealed-run target','sql01',1433,
             interval '5 seconds','windows_integrated_service_identity',
             'mandatory_validated','active',1,now(),now(),now());
            UPDATE control.collector_schedule
            SET next_due_at=statement_timestamp()-interval '1 second'
            WHERE instance_id=@target AND collector_id='engine.core';
            """, admin))
        {
            insert.Parameters.AddWithValue("target", target);
            await insert.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None)).Items, item =>
                item.TargetId.Value == target && item.CollectorId.Value == "engine.core");
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/engine.core/{target:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                work, run, lease,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
                CancellationToken.None)).Status);
        var summary = new CollectorRunSummary(
            run, work.TargetId, work.TargetRevision, work.CollectorId,
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
        await using (var counts = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run);
            """, admin))
        {
            counts.Parameters.AddWithValue("run", run.Value);
            await using NpgsqlDataReader row = await counts.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(0L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
        }
        await using (var outcome = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", admin))
        {
            outcome.Parameters.AddWithValue("run", run.Value);
            Assert.Equal(1L, await outcome.ExecuteScalarAsync());
        }

        await using (var due = new NpgsqlCommand("""
            UPDATE control.collector_schedule
            SET next_due_at=statement_timestamp()-interval '1 second'
            WHERE instance_id=@target AND collector_id='engine.core';
            """, admin))
        {
            due.Parameters.AddWithValue("target", target);
            await due.ExecuteNonQueryAsync();
        }
        CollectorDueWorkItem pending = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None)).Items, item =>
                item.TargetId.Value == target && item.CollectorId.Value == "engine.core");
        var invalidatedRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                pending, invalidatedRun, lease,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
                CancellationToken.None)).Status);
        await using (var revise = new NpgsqlCommand("""
            UPDATE control.observation_target
            SET revision=2,host_name='sql02',updated_at=statement_timestamp()
            WHERE instance_id=@target;
            """, admin))
        {
            revise.Parameters.AddWithValue("target", target);
            await revise.ExecuteNonQueryAsync();
        }
        await using (var invalidated = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_outcome
                    WHERE run_id=@run AND outcome='permanent_failure'
                      AND reason_code='target_revision_changed'),
                   (SELECT count(*) FROM telemetry.visibility_gap
                    WHERE run_id=@run AND reason_code='target_revision_changed'),
                   (SELECT active_run_id FROM control.collector_schedule
                    WHERE instance_id=@target AND collector_id='engine.core');
            """, admin))
        {
            invalidated.Parameters.AddWithValue("run", invalidatedRun.Value);
            invalidated.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await invalidated.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(0L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
            Assert.Equal(1L, row.GetInt64(3));
            Assert.True(row.IsDBNull(4));
        }

        await using (NpgsqlTransaction rollback = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, rollback);
            await using var route = new NpgsqlCommand(
                "UPDATE system.collection_run_v2_activity_read_route SET use_v2=false;", admin, rollback);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => route.ExecuteNonQueryAsync())).SqlState);
            await rollback.RollbackAsync();
        }
        await using (NpgsqlTransaction deleteRoute = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, deleteRoute);
            await using var command = new NpgsqlCommand(
                "DELETE FROM system.collection_run_v2_activity_read_route;", admin, deleteRoute);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync())).SqlState);
            await deleteRoute.RollbackAsync();
        }
        await using (NpgsqlTransaction unseal = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, unseal);
            await using var command = new NpgsqlCommand(
                "UPDATE system.collection_run_original_rollback_seal SET sealed_at=NULL,sealed_by=NULL;",
                admin, unseal);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync())).SqlState);
            await unseal.RollbackAsync();
        }
        await using (NpgsqlTransaction repeat = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, repeat);
            await using var command = new NpgsqlCommand(
                "SELECT control.seal_collection_run_original_rollback(10000);", admin, repeat);
            Assert.Equal(sealedAt, new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!));
            await using var lookup = new NpgsqlCommand(
                "SELECT count(*) FROM telemetry.lookup_collection_run_runtime(@run);", admin, repeat);
            lookup.Parameters.AddWithValue("run", run.Value);
            Assert.Equal(1L, await lookup.ExecuteScalarAsync());
            await repeat.CommitAsync();
        }
    }

    private static async Task SetMigratorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var role = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;", connection, transaction);
        await role.ExecuteNonQueryAsync();
    }
}
