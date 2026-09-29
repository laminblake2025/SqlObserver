using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class DatabaseLogReuseMigrationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ExistingInventoryRemainsUnknownAfterForwardMigration()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrationPort = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(90));
        MigrationBatchResult before = await migrationPort.ApplyPendingAsync(
            new MigrationApplyRequest(257, timeout), CancellationToken.None);
        Assert.False(before.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.observation_target
                 (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                  authentication_mode,transport_security_mode,lifecycle_state,revision,
                  created_at,updated_at,discovery_requested_at)
                VALUES (@target,@key,'Log reuse migration target','sql01',1433,
                  interval '5 seconds','windows_integrated_service_identity',
                  'mandatory_validated','active',1,statement_timestamp(),
                  statement_timestamp(),statement_timestamp());
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                INSERT INTO telemetry.collection_run
                 (run_id,instance_id,collector_id,collector_version,
                  output_schema_version,target_revision,schedule_revision,work_key,
                  owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
                VALUES (@run,@target,'database.inventory',1,1,1,1,
                  'collector/run/database.inventory/' || @target::text,
                  @owner,1,decode(repeat('aa',32),'hex'),
                  statement_timestamp(),statement_timestamp());
                INSERT INTO telemetry.database_inventory_snapshot
                 (observed_at,snapshot_id,collection_run_id,instance_id,target_revision,
                  database_id,database_name,state_code,is_read_only,recovery_model,
                  user_access,compatibility_level,collected_at)
                VALUES (statement_timestamp(),gen_random_uuid(),@run,@target,1,
                  5,'Ledger','online',false,'full','multi_user',160,
                  statement_timestamp());
                """, connection, transaction);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"log.reuse.migration.{target:N}");
            seed.Parameters.AddWithValue("scope", target.ToString());
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("owner", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        MigrationBatchResult after = await migrationPort.ApplyPendingAsync(
            new MigrationApplyRequest(258, timeout), CancellationToken.None);
        Assert.False(after.HasFailures);
        Assert.Contains(after.Results, result => result.Migration.Number.Value == 258 &&
            result.Outcome == MigrationOutcome.Applied);

        await using NpgsqlConnection read = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SET ROLE sqlobserver_migrator;
            SELECT set_config('sqlobserver.target_scope',@scope,false);
            SELECT database_name,log_reuse_wait_desc
            FROM telemetry.database_inventory_snapshot
            WHERE collection_run_id=@run;
            """, read);
        command.Parameters.AddWithValue("scope", target.ToString());
        command.Parameters.AddWithValue("run", run);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        do { } while (reader.FieldCount != 2 && await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Ledger", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());
    }
}
