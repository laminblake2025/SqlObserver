using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunIdentityFkStageTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ReviewedPlainAndPartitionedChildrenStageAfterFence()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        async Task StageAsync(string child)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using var scope = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;",
                admin, transaction);
            await scope.ExecuteNonQueryAsync();
            await using var stage = new NpgsqlCommand(
                "SELECT control.stage_collection_run_identity_fk(@child::regclass,10000);",
                admin, transaction);
            stage.Parameters.AddWithValue("child", child);
            Assert.True((bool)(await stage.ExecuteScalarAsync())!);
            await transaction.CommitAsync();
        }
        await StageAsync("control.collector_schedule");
        await StageAsync("events.blocking_edge");
        await using (NpgsqlTransaction unreviewed = await admin.BeginTransactionAsync())
        {
            await using var scope = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;",
                admin, unreviewed);
            await scope.ExecuteNonQueryAsync();
            await using var stage = new NpgsqlCommand(
                "SELECT control.stage_collection_run_identity_fk('telemetry.collection_run_identity'::regclass,10000);",
                admin, unreviewed);
            PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
                () => stage.ExecuteNonQueryAsync());
            Assert.Equal("22023", denied.SqlState);
            await unreviewed.RollbackAsync();
        }

        await using (var catalog = new NpgsqlCommand("""
            SELECT child.relkind,identity_fk.convalidated,
                   identity_fk.confrelid='telemetry.collection_run_identity'::regclass,
                   count(old_fk.oid)
            FROM pg_class AS child
            JOIN pg_constraint AS identity_fk
              ON identity_fk.conrelid=child.oid
             AND identity_fk.conname='fk_collection_run_identity'
            LEFT JOIN pg_constraint AS old_fk
              ON old_fk.conrelid=child.oid
             AND old_fk.confrelid='telemetry.collection_run'::regclass
             AND old_fk.contype='f' AND old_fk.conparentid=0
            WHERE child.oid IN ('control.collector_schedule'::regclass,
                                'events.blocking_edge'::regclass)
            GROUP BY child.oid,identity_fk.oid
            ORDER BY child.oid;
            """, admin))
        await using (NpgsqlDataReader rows = await catalog.ExecuteReaderAsync())
        {
            int seen = 0;
            while (await rows.ReadAsync())
            {
                Assert.True(rows.GetChar(0) is 'r' or 'p');
                Assert.False(rows.GetBoolean(1));
                Assert.True(rows.GetBoolean(2));
                Assert.Equal(1L, rows.GetInt64(3));
                seen++;
            }
            Assert.Equal(2, seen);
        }

        Guid target = Guid.NewGuid(), run = Guid.NewGuid(), absent = Guid.NewGuid();
        DateTimeOffset started = new(DateTime.UtcNow.Date, TimeSpan.Zero);
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Run identity FK target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,
               work_key,owner_execution_id,fencing_token,request_digest,
               scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',1,1,1,1,
              'test/identity-fk-stage',gen_random_uuid(),1,
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
        await using (NpgsqlTransaction onlyNewFk = await admin.BeginTransactionAsync())
        {
            await using (var dropOld = new NpgsqlCommand("""
                ALTER TABLE control.collector_schedule
                  DROP CONSTRAINT fk_collector_schedule_active_run;
                """, admin, onlyNewFk))
                await dropOld.ExecuteNonQueryAsync();
            await using var reject = new NpgsqlCommand("""
                UPDATE control.collector_schedule SET active_run_id=@absent
                WHERE instance_id=@target AND collector_id='queries.performance';
                """, admin, onlyNewFk);
            reject.Parameters.AddWithValue("absent", absent);
            reject.Parameters.AddWithValue("target", target);
            PostgresException missing = await Assert.ThrowsAsync<PostgresException>(
                () => reject.ExecuteNonQueryAsync());
            Assert.Equal("23503", missing.SqlState);
            Assert.Equal("fk_collection_run_identity", missing.ConstraintName);
            await onlyNewFk.RollbackAsync();
        }

        async Task<bool> ValidateAsync(string child)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using var scope = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;",
                admin, transaction);
            await scope.ExecuteNonQueryAsync();
            await using var validate = new NpgsqlCommand(
                "SELECT control.validate_collection_run_identity_fk(@child::regclass);",
                admin, transaction);
            validate.Parameters.AddWithValue("child", child);
            bool changed = (bool)(await validate.ExecuteScalarAsync())!;
            await transaction.CommitAsync();
            return changed;
        }
        Assert.True(await ValidateAsync("control.collector_schedule"));
        Assert.True(await ValidateAsync("events.blocking_edge"));
        Assert.False(await ValidateAsync("control.collector_schedule"));
        await using (var validated = new NpgsqlCommand("""
            SELECT count(*) FROM pg_constraint
            WHERE conrelid IN ('control.collector_schedule'::regclass,
                               'events.blocking_edge'::regclass)
              AND conname='fk_collection_run_identity' AND convalidated;
            """, admin))
            Assert.Equal(2L, (long)(await validated.ExecuteScalarAsync())!);
        await using (var targets = new NpgsqlCommand("""
            SELECT count(*) FROM control.collection_run_identity_fk_targets();
            """, admin))
            Assert.Equal(20L, (long)(await targets.ExecuteScalarAsync())!);

        await using (NpgsqlTransaction incomplete = await admin.BeginTransactionAsync())
        {
            await using var scope = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;",
                admin, incomplete);
            await scope.ExecuteNonQueryAsync();
            await using var stage = new NpgsqlCommand(
                "SELECT control.stage_collection_run_identity_fk('telemetry.collection_run_outcome'::regclass,10000);",
                admin, incomplete);
            PostgresException premature = await Assert.ThrowsAsync<PostgresException>(
                () => stage.ExecuteNonQueryAsync());
            Assert.Equal("55000", premature.SqlState);
            await incomplete.RollbackAsync();
        }
        await using (NpgsqlTransaction unstaged = await admin.BeginTransactionAsync())
        {
            await using var scope = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;",
                admin, unstaged);
            await scope.ExecuteNonQueryAsync();
            await using var validate = new NpgsqlCommand(
                "SELECT control.validate_collection_run_identity_fk('telemetry.collection_run_outcome'::regclass);",
                admin, unstaged);
            PostgresException missing = await Assert.ThrowsAsync<PostgresException>(
                () => validate.ExecuteNonQueryAsync());
            Assert.Equal("55000", missing.SqlState);
            await unstaged.RollbackAsync();
        }
        await using var privilege = new NpgsqlCommand("""
            SELECT has_function_privilege('sqlobserver_server',
                     'control.stage_collection_run_identity_fk(regclass,integer)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                     'control.stage_collection_run_identity_fk(regclass,integer)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                     'control.validate_collection_run_identity_fk(regclass)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                     'control.validate_collection_run_identity_fk(regclass)',
                     'EXECUTE');
            """, admin);
        await using NpgsqlDataReader grants = await privilege.ExecuteReaderAsync();
        Assert.True(await grants.ReadAsync());
        Assert.False(grants.GetBoolean(0));
        Assert.False(grants.GetBoolean(1));
        Assert.False(grants.GetBoolean(2));
        Assert.False(grants.GetBoolean(3));
    }
}
