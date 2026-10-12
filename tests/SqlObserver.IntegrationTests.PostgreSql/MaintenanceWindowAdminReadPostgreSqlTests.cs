using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class MaintenanceWindowAdminReadPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task AdminReadPagesCancelledWindowsWithinOneTargetAndDatabaseScope()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        var target = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        Guid[] windowIds =
        [
            Guid.Parse("00000000-0000-4000-8000-000000000001"),
            Guid.Parse("00000000-0000-4000-8000-000000000002"),
            Guid.Parse("00000000-0000-4000-8000-000000000003"),
        ];
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            foreach (Guid id in new[] { target.Value, other.Value })
            {
                await using var seedTarget = new NpgsqlCommand("""
                    INSERT INTO control.observation_target
                        (instance_id, instance_key, display_name, host_name, tcp_port, connect_timeout,
                         authentication_mode, transport_security_mode, lifecycle_state, revision,
                         created_at, updated_at, discovery_requested_at)
                    VALUES (@id, @key, 'Maintenance admin test', 'test.invalid', 1433, interval '5 seconds',
                            'windows_integrated_service_identity', 'mandatory_validated', 'active', 1,
                            statement_timestamp(), statement_timestamp(), statement_timestamp());
                    """, admin);
                seedTarget.Parameters.AddWithValue("id", id);
                seedTarget.Parameters.AddWithValue("key", $"maintenance-read-{id:N}");
                await seedTarget.ExecuteNonQueryAsync();
            }
            foreach (var (id, windowTarget, day, cancelled) in new[]
            {
                (windowIds[0], target.Value, 3, false),
                (windowIds[1], target.Value, 2, true),
                (windowIds[2], target.Value, 1, false),
                (Guid.NewGuid(), other.Value, 4, false),
            })
            {
                await using var seedWindow = new NpgsqlCommand("""
                    INSERT INTO alerting.maintenance_window
                        (window_id, instance_id, starts_at, ends_at, reason, revision,
                         created_at, cancelled_at)
                    VALUES (@id, @target, @starts, @starts + interval '1 hour',
                            'Planned maintenance', 4, statement_timestamp() - interval '1 minute',
                            CASE WHEN @cancelled THEN statement_timestamp() ELSE NULL END);
                    """, admin);
                seedWindow.Parameters.AddWithValue("id", id);
                seedWindow.Parameters.AddWithValue("target", windowTarget);
                seedWindow.Parameters.AddWithValue("starts", new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero));
                seedWindow.Parameters.AddWithValue("cancelled", cancelled);
                await seedWindow.ExecuteNonQueryAsync();
            }
        }

        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(serverSource);
        RepositoryCallTimeout timeout = new(TimeSpan.FromSeconds(5));
        var seen = new List<MaintenanceWindowListItem>();
        MaintenanceWindowListCursor? cursor = null;
        DateTimeOffset? listingStarted = null;
        for (int page = 0; page < 3; page++)
        {
            MaintenanceWindowListPage result = await repository.ListMaintenanceForAdminAsync(
                target, 1, cursor, timeout, CancellationToken.None);
            MaintenanceWindowListItem item = Assert.Single(result.Items);
            seen.Add(item);
            listingStarted ??= result.ListingStartedUtc;
            Assert.Equal(listingStarted, result.ListingStartedUtc);
            cursor = result.NextCursor;
            Assert.Equal(page < 2, cursor is not null);
        }
        Assert.Equal(windowIds, seen.Select(static row => row.WindowId));
        Assert.NotNull(seen[1].CancelledAtUtc);
        Assert.All(seen, static row => Assert.Equal(4, row.Revision));
        Assert.Single((await repository.ListMaintenanceForAdminAsync(other, 100, null, timeout,
            CancellationToken.None)).Items);
        Assert.False(await FunctionPrivilegeAsync(database, "sqlobserver_collector"));
        Assert.True(await FunctionPrivilegeAsync(database, "sqlobserver_server"));

        await using NpgsqlConnection server = await serverSource.OpenConnectionAsync();
        await using (var unbounded = new NpgsqlCommand(
            "SELECT * FROM alerting.list_maintenance_for_admin(@target,statement_timestamp(),NULL,NULL,NULL::integer);",
            server))
        {
            unbounded.Parameters.AddWithValue("target", target.Value);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(
                () => unbounded.ExecuteNonQueryAsync())).SqlState);
        }
        await using NpgsqlTransaction transaction = await server.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);",
            server, transaction))
        {
            scope.Parameters.AddWithValue("scope", other.Value.ToString("D"));
            await scope.ExecuteScalarAsync();
        }
        await using var crossTarget = new NpgsqlCommand(
            "SELECT * FROM alerting.list_maintenance_for_admin(@target,statement_timestamp(),NULL,NULL,1);",
            server, transaction);
        crossTarget.Parameters.AddWithValue("target", target.Value);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
            () => crossTarget.ExecuteNonQueryAsync())).SqlState);
    }

    private static async Task<bool> FunctionPrivilegeAsync(RepositoryTestDatabase database, string role)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT has_function_privilege(@role,
              'alerting.list_maintenance_for_admin(uuid,timestamptz,timestamptz,uuid,integer)',
              'EXECUTE');
            """);
        command.Parameters.AddWithValue("role", role);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
