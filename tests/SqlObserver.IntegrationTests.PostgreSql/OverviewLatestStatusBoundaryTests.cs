using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class OverviewLatestStatusBoundaryTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task PublishesOnlyNewerFencedTargetStatusAndReadsOnlyItsCurrentRevision()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        Guid target = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            foreach (Guid id in new[] { target, other })
            {
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO control.observation_target
                        (instance_id, instance_key, display_name, host_name, tcp_port,
                         connect_timeout, authentication_mode, transport_security_mode,
                         lifecycle_state, revision, created_at, updated_at, discovery_requested_at)
                    VALUES (@id, @key, 'Overview boundary test', 'test.invalid', 1433,
                            interval '5 seconds', 'windows_integrated_service_identity',
                            'mandatory_validated', 'active', 1,
                            statement_timestamp(), statement_timestamp(), statement_timestamp());
                    """, admin);
                seed.Parameters.AddWithValue("id", id);
                seed.Parameters.AddWithValue("key", $"overview-latest-{id:N}");
                await seed.ExecuteNonQueryAsync();
            }
        }

        await using var privilege = database.DataSource.CreateCommand("""
            SELECT has_table_privilege('sqlobserver_server',
                       'reporting.overview_latest_status','SELECT'),
                   has_function_privilege('sqlobserver_server',
                       'reporting.get_overview_latest_status(uuid,bigint)','EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                       'reporting.publish_overview_latest_status(uuid,bigint,bigint,text,timestamptz,integer,text,integer,text,jsonb,jsonb,jsonb,jsonb,uuid,bigint)','EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                       'reporting.publish_overview_latest_status(uuid,bigint,bigint,text,timestamptz,integer,text,integer,text,jsonb,jsonb,jsonb,jsonb,uuid,bigint)','EXECUTE');
            """);
        await using (NpgsqlDataReader permissions = await privilege.ExecuteReaderAsync())
        {
            Assert.True(await permissions.ReadAsync());
            Assert.False(permissions.GetBoolean(0));
            Assert.True(permissions.GetBoolean(1));
            Assert.False(permissions.GetBoolean(2));
            Assert.True(permissions.GetBoolean(3));
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        long token;
        await using (var lease = collector.CreateCommand("""
            SELECT acquired,fencing_token
            FROM control.acquire_worker_lease('overview/latest-status',@owner,interval '5 minutes');
            """))
        {
            lease.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader row = await lease.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            token = row.GetInt64(1);
        }
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => PublishAsync(collector, target, target, Guid.NewGuid(), token, 1))).SqlState);
        Assert.True(await PublishAsync(collector, target, target, owner, token, 1));
        Assert.False(await PublishAsync(collector, target, target, owner, token, 1));
        Assert.True(await PublishAsync(collector, target, target, owner, token, 2));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(
            () => PublishAsync(collector, target, other, owner, token, 3))).SqlState);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Assert.Equal(2, await ReadGenerationAsync(server, target, target, 1));
        Assert.Null(await ReadGenerationAsync(server, target, target, 2));
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
            () => ReadGenerationAsync(server, target, other, 1))).SqlState);

        var latest = new PostgreSqlOverviewLatestStatusPort(server);
        OverviewLatestStatus status = Assert.IsType<OverviewLatestStatus>(
            await latest.ReadAsync(new MonitoredInstanceId(target), 1, CancellationToken.None));
        Assert.Equal(target, status.TargetId);
        Assert.Equal(2, status.Generation);
        Assert.Equal(3, status.ActiveAlerts.Value);
        Assert.Equal(2, status.BlockedSessions.Value);
        Assert.Equal("current", status.Sources["alerts"].State);
        Assert.Null(await latest.ReadAsync(new MonitoredInstanceId(target), 2, CancellationToken.None));

        await using (var corrupt = database.DataSource.CreateCommand("""
            UPDATE reporting.overview_latest_status
            SET issues = jsonb_build_array(jsonb_build_object(
                'targetId', @other, 'server', 'Foreign server', 'title', 'Foreign issue',
                'detail', 'Outside target', 'destination', 'health',
                'priority', 1, 'observedAtUtc', NULL))
            WHERE instance_id = @target;
            """))
        {
            corrupt.Parameters.AddWithValue("target", target);
            corrupt.Parameters.AddWithValue("other", other);
            await corrupt.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            latest.ReadAsync(new MonitoredInstanceId(target), 1, CancellationToken.None));
    }

    private static async Task<bool> PublishAsync(
        NpgsqlDataSource source, Guid target, Guid scope, Guid owner, long token, long generation)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand("""
            SELECT reporting.publish_overview_latest_status(
                @target,1,@generation,'current',NULL::timestamptz,
                3,'current',2,'current',
                '{"schemaVersion":1,"health":{"state":"current"},"alerts":{"state":"current"},"blocking":{"state":"current"}}'::jsonb,
                '[]'::jsonb,'[]'::jsonb,'[]'::jsonb,@owner,@token);
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        bool published = (bool)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return published;
    }

    private static async Task<long?> ReadGenerationAsync(
        NpgsqlDataSource source, Guid target, Guid scope, long revision)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand("""
            SELECT generation
            FROM reporting.get_overview_latest_status(@target,@revision);
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("revision", revision);
        object? result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result is null or DBNull ? null : (long)result;
    }

    private static async Task SetScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid scope)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction);
        command.Parameters.AddWithValue("scope", scope.ToString("D"));
        await command.ExecuteScalarAsync();
    }
}
