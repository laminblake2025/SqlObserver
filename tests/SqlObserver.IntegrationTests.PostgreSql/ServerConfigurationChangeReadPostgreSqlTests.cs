using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerConfigurationChangeReadPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task PagesTiedChangesAndRejectsStaleGenerationOrMissingScope()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result =>
                result.Outcome == MigrationOutcome.Failed).Select(static result =>
                $"{result.Migration.Number.Value}:{result.FailureCode}")));

        Guid target = Guid.NewGuid();
        Guid emptyTarget = Guid.NewGuid();
        Guid run = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.observation_target
                 (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                  authentication_mode,transport_security_mode,lifecycle_state,revision,
                  created_at,updated_at,discovery_requested_at)
                VALUES (@target,@key,'Configuration target','sql01',1433,interval '5 seconds',
                  'windows_integrated_service_identity','mandatory_validated','active',1,
                  statement_timestamp(),statement_timestamp(),statement_timestamp()),
                 (@empty,@empty_key,'Empty configuration target','sql02',1433,interval '5 seconds',
                  'windows_integrated_service_identity','mandatory_validated','active',1,
                  statement_timestamp(),statement_timestamp(),statement_timestamp());
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                INSERT INTO telemetry.collection_run
                 (run_id,instance_id,collector_id,collector_version,output_schema_version,
                  target_revision,schedule_revision,work_key,owner_execution_id,
                  fencing_token,request_digest,scheduled_for,started_at)
                VALUES (@run,@target,'database.inventory',1,1,1,1,
                  'collector/run/database.inventory/' || @target::text,
                  @owner,1,decode(repeat('aa',32),'hex'),@now-interval '20 minutes',
                  @now-interval '20 minutes');
                INSERT INTO telemetry.server_configuration_snapshot
                 (instance_id,run_id,configuration_id,target_revision,server_major_version,
                  configuration_name,source_type,configured_value,in_use_value,
                  is_dynamic,is_advanced,observed_at)
                SELECT @target,@run,id,1,16,'Option ' || id,'int',id::text,'1',
                       true,false,@now-interval '20 minutes'
                FROM generate_series(1,3) AS id;
                INSERT INTO events.server_configuration_change
                 (instance_id,run_id,configuration_id,target_revision,server_major_version,
                  configuration_name,source_type,previous_configured_value,
                  configured_value,previous_in_use_value,in_use_value,
                  configured_changed,in_use_changed,previous_observed_at,
                  observed_at,detected_at)
                SELECT @target,@run,id,1,16,'Option ' || id,'int','0',id::text,
                       '1','1',true,false,@now-interval '25 minutes',
                       @now-interval '20 minutes',@now-interval '10 minutes'
                FROM generate_series(1,3) AS id;
                """, connection, transaction);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"config.history.{target:N}");
            seed.Parameters.AddWithValue("empty", emptyTarget);
            seed.Parameters.AddWithValue("empty_key", $"config.history.{emptyTarget:N}");
            seed.Parameters.AddWithValue("scope", target.ToString());
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("owner", owner);
            seed.Parameters.AddWithValue("now", now);
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using (NpgsqlConnection fresh = await server.OpenConnectionAsync())
        {
            await using var unscoped = new NpgsqlCommand("""
                SELECT count(*) FROM reporting.list_server_configuration_changes(
                    @target,@from_utc,@to_utc,2);
                """, fresh);
            AddWindow(unscoped, target, now);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                () => unscoped.ExecuteScalarAsync())).SqlState);
        }
        var port = new PostgreSqlServerConfigurationChangePort(server);
        ServerConfigurationChangePage emptyPage = await port.ReadAsync(
            new ServerConfigurationChangeRequest(new MonitoredInstanceId(emptyTarget),
                now.AddHours(-1), now, 2, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),
            CancellationToken.None);
        Assert.Empty(emptyPage.Items);
        Assert.Equal(0, emptyPage.PublicationGeneration);
        Assert.Null(emptyPage.NextCursor);
        var request = new ServerConfigurationChangeRequest(new MonitoredInstanceId(target),
            now.AddHours(-1), now, 2, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        ServerConfigurationChangePage firstPage = await port.ReadAsync(request, CancellationToken.None);
        Assert.Equal([1, 2], firstPage.Items.Select(static item => item.ConfigurationId));
        Assert.NotNull(firstPage.NextCursor);
        ServerConfigurationChangePage secondPage = await port.ReadAsync(
            new ServerConfigurationChangeRequest(request.TargetId, request.FromUtc,
                request.ToUtc, request.Limit, request.Timeout, firstPage.NextCursor),
            CancellationToken.None);
        Assert.Equal(3, Assert.Single(secondPage.Items).ConfigurationId);
        Assert.Null(secondPage.NextCursor);
        await using NpgsqlConnection scoped = await server.OpenConnectionAsync();
        DateTimeOffset cursorTime = default;
        await using (NpgsqlTransaction transaction = await scoped.BeginTransactionAsync())
        {
            await SetScopeAsync(scoped, transaction, target);
            await using var first = new NpgsqlCommand("""
                SELECT target_revision,publication_generation,has_more,
                       configuration_id,detected_at,run_id
                FROM reporting.list_server_configuration_changes(
                    @target,@from_utc,@to_utc,2);
                """, scoped, transaction);
            AddWindow(first, target, now);
            var ids = new List<int>();
            await using (NpgsqlDataReader rows = await first.ExecuteReaderAsync())
            {
                while (await rows.ReadAsync())
                {
                    Assert.Equal(1, rows.GetInt64(0));
                    Assert.Equal(1, rows.GetInt64(1));
                    Assert.True(rows.GetBoolean(2));
                    ids.Add(rows.GetInt32(3));
                    cursorTime = rows.GetFieldValue<DateTimeOffset>(4);
                    Assert.Equal(run, rows.GetGuid(5));
                }
            }
            Assert.Equal([1, 2], ids);
            Assert.NotEqual(default, cursorTime);
            await using var next = new NpgsqlCommand("""
                SELECT publication_generation,has_more,configuration_id
                FROM reporting.list_server_configuration_changes(
                    @target,@from_utc,@to_utc,2,@after_at,@after_run,@after_id,1,1);
                """, scoped, transaction);
            AddWindow(next, target, now);
            next.Parameters.AddWithValue("after_at", cursorTime);
            next.Parameters.AddWithValue("after_run", run);
            next.Parameters.AddWithValue("after_id", 2);
            await using (NpgsqlDataReader rows = await next.ExecuteReaderAsync())
            {
                Assert.True(await rows.ReadAsync());
                Assert.Equal(1, rows.GetInt64(0));
                Assert.False(rows.GetBoolean(1));
                Assert.Equal(3, rows.GetInt32(2));
                Assert.False(await rows.ReadAsync());
            }
            await transaction.CommitAsync();
        }

        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await admin.BeginTransactionAsync())
        {
            await using var publish = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                INSERT INTO telemetry.server_configuration_snapshot
                 (instance_id,run_id,configuration_id,target_revision,server_major_version,
                  configuration_name,source_type,configured_value,in_use_value,
                  is_dynamic,is_advanced,observed_at)
                VALUES (@target,@run,4,1,16,'Option 4','int','4','1',
                        true,false,@now-interval '5 minutes');
                INSERT INTO events.server_configuration_change
                 (instance_id,run_id,configuration_id,target_revision,server_major_version,
                  configuration_name,source_type,previous_configured_value,
                  configured_value,previous_in_use_value,in_use_value,
                  configured_changed,in_use_changed,previous_observed_at,
                  observed_at,detected_at)
                VALUES (@target,@run,4,1,16,'Option 4','int','0','4',
                        '1','1',true,false,@now-interval '10 minutes',
                        @now-interval '5 minutes',@now-interval '2 minutes');
                """, admin, transaction);
            publish.Parameters.AddWithValue("target", target);
            publish.Parameters.AddWithValue("scope", target.ToString());
            publish.Parameters.AddWithValue("run", run);
            publish.Parameters.AddWithValue("now", now);
            await publish.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await Assert.ThrowsAsync<ServerConfigurationCursorStaleException>(async () =>
        {
            await port.ReadAsync(new ServerConfigurationChangeRequest(
                request.TargetId, request.FromUtc, request.ToUtc,
                request.Limit, request.Timeout, firstPage.NextCursor), CancellationToken.None);
        });
        await using (NpgsqlTransaction transaction = await scoped.BeginTransactionAsync())
        {
            await SetScopeAsync(scoped, transaction, target);
            await using var stale = new NpgsqlCommand("""
                SELECT count(*) FROM reporting.list_server_configuration_changes(
                    @target,@from_utc,@to_utc,2,@after_at,@after_run,@after_id,1,1);
                """, scoped, transaction);
            AddWindow(stale, target, now);
            stale.Parameters.AddWithValue("after_at", cursorTime);
            stale.Parameters.AddWithValue("after_run", run);
            stale.Parameters.AddWithValue("after_id", 2);
            Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(
                () => stale.ExecuteScalarAsync())).SqlState);
        }
        await using var denied = new NpgsqlCommand("""
            SELECT count(*) FROM reporting.list_server_configuration_changes(
                @target,@from_utc,@to_utc,2);
            """, scoped);
        AddWindow(denied, target, now);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
            () => denied.ExecuteScalarAsync())).SqlState);
    }

    private static void AddWindow(NpgsqlCommand command, Guid target, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("from_utc", now.AddHours(-1));
        command.Parameters.AddWithValue("to_utc", now);
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid target)
    {
        await using var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction);
        scope.Parameters.AddWithValue("scope", target.ToString());
        await scope.ExecuteNonQueryAsync();
    }
}
