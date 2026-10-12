using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class DatabaseOptionChangePostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ChangeHistoryRequiresAdjacentCompleteRunsAndTargetScope()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(249,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result =>
                result.Outcome == MigrationOutcome.Failed).Select(static result =>
                $"{result.Migration.Number.Value}:{result.FailureCode}")));

        Guid target = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.observation_target
                 (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                  authentication_mode,transport_security_mode,lifecycle_state,revision,
                  created_at,updated_at,discovery_requested_at)
                VALUES (@target,@key,'Option change target','sql01',1433,interval '5 seconds',
                  'windows_integrated_service_identity','mandatory_validated','active',1,
                  statement_timestamp(),statement_timestamp(),statement_timestamp());
                CREATE TEMP TABLE option_change_fixture
                 (sequence integer,run_id uuid,at_utc timestamptz,outcome text,
                  database_name text,recovery_model text,user_access text,
                  is_read_only boolean,compatibility_level integer) ON COMMIT DROP;
                INSERT INTO option_change_fixture
                SELECT sample.sequence,gen_random_uuid(),
                       @now-sample.minutes_ago*interval '1 minute',sample.outcome,
                       sample.database_name,sample.recovery_model,sample.user_access,
                       sample.is_read_only,sample.compatibility_level
                FROM (VALUES
                    (0,1800,'succeeded','Ledger','simple','multi_user',false,160),
                    (1,90,'succeeded','Ledger','full','multi_user',false,160),
                    (2,80,'succeeded','Ledger','simple','single_user',true,150),
                    (3,70,'permission_denied','Ledger','simple','single_user',true,150),
                    (4,60,'succeeded','Ledger','full','multi_user',false,160),
                    (5,50,'succeeded','Ledger','bulk_logged','multi_user',false,160),
                    (6,40,'succeeded','Ledger New','full','multi_user',false,150),
                    (7,30,'succeeded','Ledger New','simple','multi_user',false,150)
                ) AS sample(sequence,minutes_ago,outcome,database_name,
                            recovery_model,user_access,is_read_only,compatibility_level);
                GRANT SELECT ON option_change_fixture TO sqlobserver_migrator;
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                INSERT INTO telemetry.collection_run
                 (run_id,instance_id,collector_id,collector_version,output_schema_version,
                  target_revision,schedule_revision,work_key,owner_execution_id,
                  fencing_token,request_digest,scheduled_for,started_at)
                SELECT fixture.run_id,@target,'database.inventory',1,1,1,1,
                       'collector/run/database.inventory/' || @target::text,
                       @owner,1,decode(repeat('aa',32),'hex'),fixture.at_utc,fixture.at_utc
                FROM option_change_fixture AS fixture ORDER BY fixture.sequence;
                INSERT INTO telemetry.collection_run_outcome
                 (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                  source_row_count,output_item_count,inserted_item_count,
                  duplicate_item_count,rejected_item_count,response_bytes,
                  output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                  loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
                  completed_at)
                SELECT fixture.run_id,fixture.outcome,
                       CASE WHEN fixture.outcome='succeeded' THEN 'completed'
                            ELSE 'required_permission_missing' END,
                       1,0,10,
                       CASE WHEN fixture.outcome='succeeded' THEN 1 ELSE 0 END,
                       CASE WHEN fixture.outcome='succeeded' THEN 1 ELSE 0 END,
                       CASE WHEN fixture.outcome='succeeded' THEN 1 ELSE 0 END,
                       0,0,
                       CASE WHEN fixture.outcome='succeeded' THEN 128 ELSE 0 END,
                       CASE WHEN fixture.outcome='succeeded' THEN 128 ELSE 0 END,
                       CASE WHEN fixture.outcome='succeeded' THEN 128 ELSE 0 END,
                       false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),
                       fixture.at_utc+interval '1 second'
                FROM option_change_fixture AS fixture ORDER BY fixture.sequence;
                INSERT INTO telemetry.database_inventory_snapshot
                 (observed_at,snapshot_id,collection_run_id,instance_id,target_revision,
                  database_id,database_name,state_code,is_read_only,recovery_model,
                  user_access,compatibility_level,collected_at)
                SELECT fixture.at_utc,gen_random_uuid(),fixture.run_id,@target,1,
                       5,fixture.database_name,'online',fixture.is_read_only,
                       fixture.recovery_model,fixture.user_access,
                       fixture.compatibility_level,fixture.at_utc
                FROM option_change_fixture AS fixture
                WHERE fixture.outcome='succeeded';
                """, connection, transaction);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"option.changes.{target:N}");
            seed.Parameters.AddWithValue("scope", target.ToString());
            seed.Parameters.AddWithValue("owner", Guid.NewGuid());
            seed.Parameters.AddWithValue("now", now);
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var request = new DatabaseOptionChangeRequest(new MonitoredInstanceId(target),
            now.AddHours(-2), now, 100, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var port = new PostgreSqlDatabaseOptionChangePort(server);
        Assert.Equal(6, (await port.ReadAsync(request, CancellationToken.None)).Items.Count);
        MigrationBatchResult upgraded = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(upgraded.HasFailures);
        Assert.Equal(250, Assert.Single(upgraded.Results).Migration.Number.Value);
        DatabaseOptionChangeResult portResult = await port
            .ReadAsync(request, CancellationToken.None);
        Assert.Equal(7, portResult.Items.Count);
        Assert.All(portResult.Items, item => Assert.Equal(5, item.DatabaseId));
        Assert.Contains(portResult.Items, item => item.OptionKey == "recovery_model" &&
            item.PreviousValue == "simple" && item.CurrentValue == "full" &&
            item.DetectedAtUtc >= now.AddMinutes(-90) &&
            item.DetectedAtUtc < now.AddMinutes(-89));
        await using NpgsqlConnection scoped = await server.OpenConnectionAsync();
        await using (NpgsqlTransaction transaction = await scoped.BeginTransactionAsync())
        {
            await using (var claim = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target,true);", scoped, transaction))
            {
                claim.Parameters.AddWithValue("target", target.ToString());
                await claim.ExecuteNonQueryAsync();
            }
            await using var read = new NpgsqlCommand("""
                SELECT option_key,previous_value,current_value,detected_at
                FROM reporting.list_database_option_changes(@target,@from_utc,@to_utc,100);
                """, scoped, transaction);
            read.Parameters.AddWithValue("target", target);
            read.Parameters.AddWithValue("from_utc", now.AddHours(-2));
            read.Parameters.AddWithValue("to_utc", now);
            var changes = new List<(string Key, string Previous, string Current, DateTimeOffset Detected)>();
            await using (NpgsqlDataReader rows = await read.ExecuteReaderAsync())
            {
                while (await rows.ReadAsync())
                    changes.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2),
                        rows.GetFieldValue<DateTimeOffset>(3)));
            }
            Assert.Equal(7, changes.Count);
            Assert.Contains(changes, item => item.Key == "recovery_model" &&
                item.Previous == "full" && item.Current == "simple" &&
                item.Detected >= now.AddMinutes(-80));
            Assert.Contains(changes, item => item.Key == "recovery_model" &&
                item.Previous == "full" && item.Current == "bulk_logged" &&
                item.Detected >= now.AddMinutes(-50));
            Assert.DoesNotContain(changes, item => item.Detected < now.AddMinutes(-90));
            await transaction.CommitAsync();
        }

        await using var denied = new NpgsqlCommand("""
            SELECT count(*) FROM reporting.list_database_option_changes(
                @target,@from_utc,@to_utc,100);
            """, scoped);
        denied.Parameters.AddWithValue("target", target);
        denied.Parameters.AddWithValue("from_utc", now.AddHours(-2));
        denied.Parameters.AddWithValue("to_utc", now);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => denied.ExecuteScalarAsync());
        Assert.Equal("42501", exception.SqlState);
    }
}
