using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunFleetFenceTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task FleetCheckRequiresCoverageAndHoldsWriterFenceThroughCallerTransaction()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), emptyTarget = Guid.NewGuid(), run = Guid.NewGuid();
        Guid sentinel = Guid.NewGuid();
        DateTimeOffset start = new(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Fleet fence target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now()),
              (@empty,@empty::text,'Fleet fence empty','sql02',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,
               work_key,owner_execution_id,fencing_token,request_digest,
               scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',1,1,1,1,
              'test/fleet-fence',gen_random_uuid(),1,
              decode(repeat('00',32),'hex'),@start,@start);
            """, admin))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("empty", emptyTarget);
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("start", start);
            await seed.ExecuteNonQueryAsync();
        }

        async Task SetMigratorScopeAsync(NpgsqlTransaction transaction, Guid scope)
        {
            await using var command = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope::text,true);
                """, admin, transaction);
            command.Parameters.AddWithValue("scope", scope);
            await command.ExecuteNonQueryAsync();
        }

        async Task<(long Targets, long Collectors, long Pairs)> VerifyAsync(
            NpgsqlTransaction transaction, int maxPairs = 10000)
        {
            await using var command = new NpgsqlCommand(
                "SELECT * FROM control.verify_collection_run_v2_fleet_fenced(@max);",
                admin, transaction);
            command.Parameters.AddWithValue("max", maxPairs);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        await using (NpgsqlTransaction incomplete = await admin.BeginTransactionAsync())
        {
            await SetMigratorScopeAsync(incomplete, sentinel);
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(
                () => VerifyAsync(incomplete));
            Assert.Equal("55000", error.SqlState);
            await incomplete.RollbackAsync();
        }

        foreach (Guid scope in new[] { target, emptyTarget })
        {
            await using NpgsqlTransaction backfill = await admin.BeginTransactionAsync();
            await SetMigratorScopeAsync(backfill, scope);
            await using var copy = new NpgsqlCommand(
                "SELECT complete FROM control.backfill_collection_run_v2(@target,10);",
                admin, backfill);
            copy.Parameters.AddWithValue("target", scope);
            Assert.True((bool)(await copy.ExecuteScalarAsync())!);
            await backfill.CommitAsync();
        }
        await using (NpgsqlTransaction record = await admin.BeginTransactionAsync())
        {
            await SetMigratorScopeAsync(record, target);
            await using var certificate = new NpgsqlCommand("""
                SELECT parity FROM control.record_collection_run_v2_parity_window(
                  @target,'queries.performance',@start,@end,10);
                """, admin, record);
            certificate.Parameters.AddWithValue("target", target);
            certificate.Parameters.AddWithValue("start", start);
            certificate.Parameters.AddWithValue("end", start.AddDays(1));
            Assert.True((bool)(await certificate.ExecuteScalarAsync())!);
            await record.CommitAsync();
        }

        await using (NpgsqlTransaction fenced = await admin.BeginTransactionAsync())
        {
            await SetMigratorScopeAsync(fenced, sentinel);
            var verified = await VerifyAsync(fenced);
            Assert.Equal(2L, verified.Targets);
            Assert.True(verified.Collectors > 1);
            Assert.Equal(verified.Targets * verified.Collectors, verified.Pairs);
            await using (var scope = new NpgsqlCommand(
                "SELECT current_setting('sqlobserver.target_scope',true);",
                admin, fenced))
                Assert.Equal(sentinel.ToString(), await scope.ExecuteScalarAsync());

            // The table lock must outlive the function call until this
            // transaction commits, so a new target cannot enter the proof.
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
            await using (NpgsqlTransaction blockedRun =
                await contender.BeginTransactionAsync())
            {
                await using var write = new NpgsqlCommand("""
                    SET LOCAL lock_timeout='150ms';
                    INSERT INTO telemetry.collection_run
                      (run_id,instance_id,collector_id,collector_version,
                       output_schema_version,target_revision,schedule_revision,
                       work_key,owner_execution_id,fencing_token,
                       request_digest,scheduled_for,started_at)
                    VALUES(gen_random_uuid(),@target,'queries.performance',
                      1,1,1,1,'test/blocked-run',gen_random_uuid(),1,
                      decode(repeat('00',32),'hex'),@start,@start);
                    """, contender, blockedRun);
                write.Parameters.AddWithValue("target", target);
                write.Parameters.AddWithValue("start", start);
                PostgresException writerLock =
                    await Assert.ThrowsAsync<PostgresException>(
                        () => write.ExecuteNonQueryAsync());
                Assert.Equal("55P03", writerLock.SqlState);
                await blockedRun.RollbackAsync();
            }
            await fenced.CommitAsync();
        }

        await using (NpgsqlTransaction small = await admin.BeginTransactionAsync())
        {
            await SetMigratorScopeAsync(small, sentinel);
            PostgresException bound = await Assert.ThrowsAsync<PostgresException>(
                () => VerifyAsync(small, 1));
            Assert.Equal("54000", bound.SqlState);
            await small.RollbackAsync();
        }
        await using (NpgsqlTransaction disabled = await admin.BeginTransactionAsync())
        {
            await using (var alter = new NpgsqlCommand(
                "ALTER TABLE telemetry.collection_run DISABLE TRIGGER collection_run_v2_mirror;",
                admin, disabled))
                await alter.ExecuteNonQueryAsync();
            await SetMigratorScopeAsync(disabled, sentinel);
            PostgresException gate = await Assert.ThrowsAsync<PostgresException>(
                () => VerifyAsync(disabled));
            Assert.Equal("55000", gate.SqlState);
            await disabled.RollbackAsync();
        }
        await using var privilege = new NpgsqlCommand("""
            SELECT has_function_privilege('sqlobserver_server',
                     'control.verify_collection_run_v2_fleet_fenced(integer)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                     'control.verify_collection_run_v2_fleet_fenced(integer)',
                     'EXECUTE');
            """, admin);
        await using NpgsqlDataReader privileges = await privilege.ExecuteReaderAsync();
        Assert.True(await privileges.ReadAsync());
        Assert.False(privileges.GetBoolean(0));
        Assert.False(privileges.GetBoolean(1));
    }
}
