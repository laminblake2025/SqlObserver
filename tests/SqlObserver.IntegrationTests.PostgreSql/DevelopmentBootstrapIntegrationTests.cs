using System.Security.Cryptography;
using Npgsql;
using SqlObserver.Infrastructure.PostgreSql;
using Testcontainers.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Trait("Category", "RequiresPostgreSql")]
public sealed class DevelopmentBootstrapIntegrationTests
{
    [Fact]
    public async Task DedicatedBootstrapIsIdempotentAndApplicationLoginCannotMigrateOrReadTables()
    {
        // A private cluster is required because the bootstrap deliberately refuses arbitrary database names.
        await using PostgreSqlContainer container = new PostgreSqlBuilder(PostgreSql18Fixture.Image)
            .WithDatabase(PostgreSqlDevelopmentBootstrap.DatabaseName).WithUsername("postgres")
            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).WithCleanUp(true).Build();
        await container.StartAsync();
        var bootstrap = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Host = "127.0.0.1", Pooling = false };
        // Quotes and SQL punctuation remain password data across parameter binding and format('%L').
        string applicationPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + "'\\;--";
        DevelopmentBootstrapResult first = await PostgreSqlDevelopmentBootstrap.ApplyAsync(
            "Development", bootstrap.ConnectionString, applicationPassword, CancellationToken.None);
        Assert.Equal(1, first.SchemaVersion);
        Assert.Equal(2, first.TargetCount);
        Assert.Equal(TimeSpan.FromHours(1), first.SeedToUtc - first.SeedFromUtc);
        Assert.InRange(first.SeedToUtc, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow);

        await using var admin = new NpgsqlConnection(bootstrap.ConnectionString);
        await admin.OpenAsync();
        long[] firstCounts = await ReadCountsAsync(admin);
        Assert.Equal(new long[] { 2, 130, 130, 234, 52, 26, 52, 13 }, firstCounts);
        DevelopmentBootstrapResult replay = await PostgreSqlDevelopmentBootstrap.ApplyAsync(
            "Development", bootstrap.ConnectionString, applicationPassword, CancellationToken.None);
        Assert.Equal(first, replay);
        Assert.Equal(firstCounts, await ReadCountsAsync(admin));

        var appConfiguration = new NpgsqlConnectionStringBuilder(bootstrap.ConnectionString)
        {
            Username = PostgreSqlDevelopmentBootstrap.ApplicationLogin,
            Password = applicationPassword,
        };
        await using var app = new NpgsqlConnection(appConfiguration.ConnectionString);
        await app.OpenAsync();
        await using (var privileges = new NpgsqlCommand(
            """
            SELECT current_user,pg_has_role(current_user,'sqlobserver_server','MEMBER'),
                pg_has_role(current_user,'sqlobserver_migrator','MEMBER'),pg_has_role(current_user,'sqlobserver_collector','MEMBER'),
                rolsuper,rolcreatedb,rolcreaterole,rolreplication,rolbypassrls
            FROM pg_roles WHERE rolname=current_user;
            """, app))
        await using (NpgsqlDataReader roles = await privileges.ExecuteReaderAsync())
        {
            Assert.True(await roles.ReadAsync());
            Assert.Equal(PostgreSqlDevelopmentBootstrap.ApplicationLogin, roles.GetString(0));
            Assert.True(roles.GetBoolean(1));
            for (int column = 2; column < 9; column++) Assert.False(roles.GetBoolean(column));
        }
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope','00000000-0000-4000-8000-000000000001',false);", app))
            await scope.ExecuteNonQueryAsync();
        await using (var projection = new NpgsqlCommand("SELECT count(*) FROM reporting.get_instance_health('00000000-0000-4000-8000-000000000001') WHERE metric_key IS NOT NULL;", app))
            Assert.Equal(9L, await projection.ExecuteScalarAsync());
        await AssertDeniedAsync(app, "SELECT * FROM telemetry.raw_metric_sample LIMIT 1;");
        await AssertDeniedAsync(app, "SET ROLE sqlobserver_migrator;");
        await AssertDeniedAsync(app, "CREATE TABLE control.development_escape(id integer);");

        // A foreign target blocks the whole bootstrap before either credentials or seed data change.
        await using (var foreign = new NpgsqlCommand(
            """
            INSERT INTO control.observation_target
                (instance_id,instance_key,display_name,created_at,updated_at,discovery_requested_at)
            VALUES ('00000000-0000-4000-8000-000000000999','foreign.target','Not a sample',
                statement_timestamp(),statement_timestamp(),statement_timestamp());
            """, admin))
            await foreign.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlDevelopmentBootstrap.ApplyAsync(
            "Development", bootstrap.ConnectionString, new string('x', 64), CancellationToken.None));
        long[] rejectedCounts = await ReadCountsAsync(admin);
        Assert.Equal(3L, rejectedCounts[0]);
        Assert.Equal(firstCounts.Skip(1), rejectedCounts.Skip(1));
        await using var reconnect = new NpgsqlConnection(appConfiguration.ConnectionString);
        await reconnect.OpenAsync();

        await using (var unexpectedGrant = new NpgsqlCommand(
            "GRANT sqlobserver_collector TO sqlobserver_dev_app;", admin))
            await unexpectedGrant.ExecuteNonQueryAsync();
        InvalidOperationException roleFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlDevelopmentBootstrap.ApplyAsync(
            "Development", bootstrap.ConnectionString, applicationPassword, CancellationToken.None));
        Assert.Contains("unexpected privileges", roleFailure.Message, StringComparison.Ordinal);
        Assert.Equal(rejectedCounts, await ReadCountsAsync(admin));
    }

    private static async Task<long[]> ReadCountsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM control.observation_target),(SELECT count(*) FROM telemetry.collection_run),
                (SELECT count(*) FROM telemetry.collection_run_outcome),(SELECT count(*) FROM telemetry.raw_metric_sample),
                (SELECT count(*) FROM telemetry.activity_session_snapshot),(SELECT count(*) FROM telemetry.activity_request_snapshot),
                (SELECT count(*) FROM telemetry.server_wait_snapshot),(SELECT count(*) FROM events.blocking_edge);
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, 8).Select(reader.GetInt64).ToArray();
    }

    private static async Task AssertDeniedAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }
}
