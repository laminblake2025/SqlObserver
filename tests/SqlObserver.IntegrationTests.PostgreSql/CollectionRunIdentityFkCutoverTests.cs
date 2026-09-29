using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunIdentityFkCutoverTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task CutoverRequiresAllValidatedChildrenAndCommitsTheWholeGraph()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        var children = new List<string>();
        await using (var inventory = new NpgsqlCommand("""
            SELECT child_relation::text
            FROM control.collection_run_identity_fk_targets()
            ORDER BY child_relation::oid;
            """, admin))
        await using (NpgsqlDataReader rows = await inventory.ExecuteReaderAsync())
            while (await rows.ReadAsync()) children.Add(rows.GetString(0));
        Assert.Equal(20, children.Count);

        async Task SetMigratorAsync(NpgsqlTransaction transaction)
        {
            await using var scope = new NpgsqlCommand(
                "SET LOCAL ROLE sqlobserver_migrator;", admin, transaction);
            await scope.ExecuteNonQueryAsync();
        }
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
        await using (NpgsqlTransaction premature = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(premature);
            await using var command = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);",
                admin, premature);
            PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync());
            Assert.Equal("55000", denied.SqlState);
            await premature.RollbackAsync();
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
        await using (NpgsqlTransaction partialGraph = await admin.BeginTransactionAsync())
        {
            await using (var drop = new NpgsqlCommand("""
                ALTER TABLE control.collector_schedule
                  DROP CONSTRAINT fk_collector_schedule_active_run;
                """, admin, partialGraph))
                await drop.ExecuteNonQueryAsync();
            await SetMigratorAsync(partialGraph);
            await using var command = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);",
                admin, partialGraph);
            PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync());
            Assert.Equal("55000", denied.SqlState);
            await partialGraph.RollbackAsync();
        }

        Guid proofTarget = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Fleet proof target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            """, admin))
        {
            seed.Parameters.AddWithValue("target", proofTarget);
            await seed.ExecuteNonQueryAsync();
        }
        await using (NpgsqlTransaction incompleteFleet = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(incompleteFleet);
            await using var command = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);",
                admin, incompleteFleet);
            PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync());
            Assert.Equal("55000", denied.SqlState);
            await incompleteFleet.RollbackAsync();
        }
        await using (NpgsqlTransaction completeFleet = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(completeFleet);
            await using var scope = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, completeFleet);
            scope.Parameters.AddWithValue("target", proofTarget);
            await scope.ExecuteNonQueryAsync();
            await using var source = new NpgsqlCommand(
                "SELECT complete FROM control.backfill_collection_run_v2(@target,10);",
                admin, completeFleet);
            source.Parameters.AddWithValue("target", proofTarget);
            Assert.True((bool)(await source.ExecuteScalarAsync())!);
            await using var identity = new NpgsqlCommand(
                "SELECT complete FROM control.backfill_collection_run_identity(@target,10);",
                admin, completeFleet);
            identity.Parameters.AddWithValue("target", proofTarget);
            Assert.True((bool)(await identity.ExecuteScalarAsync())!);
            await completeFleet.CommitAsync();
        }

        await using (NpgsqlTransaction cutover = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(cutover);
            await using var command = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);",
                admin, cutover);
            Assert.Equal(20, (int)(await command.ExecuteScalarAsync())!);
            await using NpgsqlConnection contender =
                await database.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction blocked =
                await contender.BeginTransactionAsync();
            await using var insert = new NpgsqlCommand("""
                SET LOCAL lock_timeout='150ms';
                INSERT INTO control.observation_target
                  (instance_id,instance_key,display_name,host_name,tcp_port,
                   connect_timeout,authentication_mode,transport_security_mode,
                   lifecycle_state,revision,created_at,updated_at,
                   discovery_requested_at)
                VALUES(gen_random_uuid(),gen_random_uuid()::text,'Blocked target',
                  'sql03',1433,interval '5 seconds',
                  'windows_integrated_service_identity','mandatory_validated',
                  'active',1,now(),now(),now());
                """, contender, blocked);
            PostgresException locked = await Assert.ThrowsAsync<PostgresException>(
                () => insert.ExecuteNonQueryAsync());
            Assert.Equal("55P03", locked.SqlState);
            await blocked.RollbackAsync();
            await cutover.CommitAsync();
        }

        await using (var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM pg_constraint
                    WHERE contype='f'
                      AND confrelid='telemetry.collection_run'::regclass),
                   (SELECT count(*) FROM pg_constraint
                    WHERE contype='f' AND conparentid=0
                      AND conname='fk_collection_run_identity'
                      AND confrelid='telemetry.collection_run_identity'::regclass
                      AND convalidated),
                   (SELECT removed_constraints
                    FROM system.collection_run_identity_fk_cutover),
                   has_function_privilege('sqlobserver_server',
                     'control.cutover_collection_run_identity_fks(integer)',
                     'EXECUTE');
            """, admin))
        await using (NpgsqlDataReader result = await inspect.ExecuteReaderAsync())
        {
            Assert.True(await result.ReadAsync());
            Assert.Equal(0L, result.GetInt64(0));
            Assert.Equal(20L, result.GetInt64(1));
            Assert.Equal(20, result.GetInt32(2));
            Assert.False(result.GetBoolean(3));
        }
        await using (NpgsqlTransaction repeat = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(repeat);
            await using var command = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);",
                admin, repeat);
            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
            await repeat.CommitAsync();
        }

        Guid target = Guid.NewGuid(), run = Guid.NewGuid(), absent = Guid.NewGuid();
        DateTimeOffset started = new(DateTime.UtcNow.Date, TimeSpan.Zero);
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Post-cutover target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,
               work_key,owner_execution_id,fencing_token,request_digest,
               scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',1,1,1,1,
              'test/after-fk-cutover',gen_random_uuid(),1,
              decode(repeat('00',32),'hex'),@started,@started);
            UPDATE control.collector_schedule SET active_run_id=@run
            WHERE instance_id=@target AND collector_id='queries.performance';
            """, admin))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("started", started);
            await seed.ExecuteNonQueryAsync();
        }
        await using (var mirror = new NpgsqlCommand("""
            SELECT count(*) FROM telemetry.collection_run_identity
            WHERE run_id=@run AND started_at=@started;
            """, admin))
        {
            mirror.Parameters.AddWithValue("run", run);
            mirror.Parameters.AddWithValue("started", started);
            Assert.Equal(1L, (long)(await mirror.ExecuteScalarAsync())!);
        }
        await using (NpgsqlTransaction invalid = await admin.BeginTransactionAsync())
        {
            await using var update = new NpgsqlCommand("""
                UPDATE control.collector_schedule SET active_run_id=@absent
                WHERE instance_id=@target AND collector_id='queries.performance';
                """, admin, invalid);
            update.Parameters.AddWithValue("absent", absent);
            update.Parameters.AddWithValue("target", target);
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
                () => update.ExecuteNonQueryAsync());
            Assert.Equal("23503", rejected.SqlState);
            Assert.Equal("fk_collection_run_identity", rejected.ConstraintName);
            await invalid.RollbackAsync();
        }
    }
}
