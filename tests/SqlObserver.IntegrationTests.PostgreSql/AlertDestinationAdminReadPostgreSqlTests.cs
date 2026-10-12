using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AlertDestinationAdminReadPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task AdminReadPagesOneTargetWithoutExposingReferences()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        var target = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        Guid[] destinationIds =
        [
            Guid.Parse("00000000-0000-4000-8000-000000000010"),
            Guid.Parse("00000000-0000-4000-8000-000000000020"),
            Guid.Parse("00000000-0000-4000-8000-000000000030"),
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
                    VALUES (@id, @key, 'Destination admin test', 'test.invalid', 1433, interval '5 seconds',
                            'windows_integrated_service_identity', 'mandatory_validated', 'active', 1,
                            statement_timestamp(), statement_timestamp(), statement_timestamp());
                    """, admin);
                seedTarget.Parameters.AddWithValue("id", id);
                seedTarget.Parameters.AddWithValue("key", $"destination-read-{id:N}");
                await seedTarget.ExecuteNonQueryAsync();
            }
            foreach (var (id, destinationTarget, enabled, approved) in new[]
            {
                (destinationIds[0], target.Value, true, true),
                (destinationIds[1], target.Value, false, false),
                (destinationIds[2], target.Value, true, false),
                (Guid.NewGuid(), other.Value, true, true),
            })
            {
                await using var seedDestination = new NpgsqlCommand("""
                    INSERT INTO alerting.destination
                        (destination_id, instance_id, kind, configuration_reference,
                         enabled, approved, revision)
                    VALUES (@id, @target, 'https-webhook', 'provisioned/secret-reference',
                            @enabled, @approved, 4);
                    """, admin);
                seedDestination.Parameters.AddWithValue("id", id);
                seedDestination.Parameters.AddWithValue("target", destinationTarget);
                seedDestination.Parameters.AddWithValue("enabled", enabled);
                seedDestination.Parameters.AddWithValue("approved", approved);
                await seedDestination.ExecuteNonQueryAsync();
            }
        }

        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(serverSource);
        RepositoryCallTimeout timeout = new(TimeSpan.FromSeconds(5));
        var seen = new List<AlertDestinationListItem>();
        AlertDestinationListCursor? cursor = null;
        DateTimeOffset? listingStarted = null;
        for (int page = 0; page < 3; page++)
        {
            AlertDestinationListPage result = await repository.ListDestinationsForAdminAsync(
                target, 1, cursor, timeout, CancellationToken.None);
            AlertDestinationListItem item = Assert.Single(result.Items);
            seen.Add(item);
            listingStarted ??= result.ListingStartedUtc;
            Assert.Equal(listingStarted, result.ListingStartedUtc);
            cursor = result.NextCursor;
            Assert.Equal(page < 2, cursor is not null);
            if (page == 0)
            {
                await using var insertAfterListing = database.DataSource.CreateCommand("""
                    INSERT INTO alerting.destination
                        (destination_id, instance_id, kind, configuration_reference,
                         enabled, approved, revision)
                    VALUES ('00000000-0000-4000-8000-000000000015', @target,
                            'https-webhook', 'provisioned/later', true, false, 1);
                    """);
                insertAfterListing.Parameters.AddWithValue("target", target.Value);
                await insertAfterListing.ExecuteNonQueryAsync();
            }
        }
        Assert.Equal(destinationIds, seen.Select(static row => row.DestinationId));
        Assert.False(seen[1].Enabled);
        Assert.False(seen[1].Approved);
        Assert.All(seen, static row => Assert.Equal(4, row.Revision));
        Assert.Single((await repository.ListDestinationsForAdminAsync(other, 100, null, timeout,
            CancellationToken.None)).Items);
        Assert.False(await FunctionPrivilegeAsync(database, "sqlobserver_collector"));
        Assert.True(await FunctionPrivilegeAsync(database, "sqlobserver_server"));
        Assert.False(await TablePrivilegeAsync(database, "sqlobserver_server"));

        await using NpgsqlConnection server = await serverSource.OpenConnectionAsync();
        await using (var unbounded = new NpgsqlCommand(
            "SELECT * FROM alerting.list_destinations_for_admin(@target,statement_timestamp(),NULL,NULL::integer);",
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
            "SELECT * FROM alerting.list_destinations_for_admin(@target,statement_timestamp(),NULL,1);",
            server, transaction);
        crossTarget.Parameters.AddWithValue("target", target.Value);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
            () => crossTarget.ExecuteNonQueryAsync())).SqlState);
    }

    private static async Task<bool> FunctionPrivilegeAsync(RepositoryTestDatabase database, string role)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT has_function_privilege(@role,
              'alerting.list_destinations_for_admin(uuid,timestamptz,uuid,integer)', 'EXECUTE');
            """);
        command.Parameters.AddWithValue("role", role);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TablePrivilegeAsync(RepositoryTestDatabase database, string role)
    {
        await using var command = database.DataSource.CreateCommand(
            "SELECT has_table_privilege(@role, 'alerting.destination', 'SELECT');");
        command.Parameters.AddWithValue("role", role);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
