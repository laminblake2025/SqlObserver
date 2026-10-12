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
        Assert.Equal(new long[] { 2, 156, 156, 598, 52, 26, 52, 13 }, firstCounts);
        await using (var seeded = new NpgsqlCommand("""
            SELECT count(*)=26 AND bool_and(o.source_row_count=14 AND o.output_item_count=14 AND NOT o.loss_detected)
              AND (SELECT count(*) FROM telemetry.raw_metric_sample m JOIN telemetry.collection_run r ON r.run_id=m.collection_run_id
                WHERE r.work_key LIKE 'development/sample/core-v2/%')=364
            FROM telemetry.collection_run r JOIN telemetry.collection_run_outcome o USING(run_id)
            WHERE r.work_key LIKE 'development/sample/core-v2/%';
            """, admin))
            Assert.Equal(true, await seeded.ExecuteScalarAsync());
        await using (var original = new NpgsqlCommand("""
            SELECT count(*)=26 AND bool_and(o.source_row_count=9 AND o.output_item_count=9)
            FROM telemetry.collection_run r JOIN telemetry.collection_run_outcome o USING(run_id)
            WHERE collector_id='engine.core' AND work_key LIKE 'development/sample/v1/%';
            """, admin))
            Assert.Equal(true, await original.ExecuteScalarAsync());
        // Existing fixture partitions must be reused, even when creation is no
        // longer allowed. Refusing every creation call makes that boundary
        // deterministic without waiting for the saved sample window to age.
        string partitionDefinitions;
        await using (var definitions = new NpgsqlCommand("""
            SELECT pg_get_functiondef('control.ensure_activity_daily_partitions(date)'::regprocedure)
                || E';\n' || pg_get_functiondef('control.ensure_blocking_monthly_partition(date)'::regprocedure) || ';';
            """, admin)) partitionDefinitions = (string)(await definitions.ExecuteScalarAsync())!;
        await using (var refuseCreation = new NpgsqlCommand("""
            CREATE OR REPLACE FUNCTION control.ensure_activity_daily_partitions(p_partition_day date) RETURNS void
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture partition creation is closed'; END $$;
            CREATE OR REPLACE FUNCTION control.ensure_blocking_monthly_partition(p_partition_month date) RETURNS void
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture partition creation is closed'; END $$;
            """, admin)) await refuseCreation.ExecuteNonQueryAsync();
        DevelopmentBootstrapResult replay = await PostgreSqlDevelopmentBootstrap.ApplyAsync(
            "Development", bootstrap.ConnectionString, applicationPassword, CancellationToken.None);
        Assert.Equal(first, replay);
        Assert.Equal(firstCounts, await ReadCountsAsync(admin));
        await using (var restoreCreation = new NpgsqlCommand(partitionDefinitions, admin))
            await restoreCreation.ExecuteNonQueryAsync();
        await using (var historicalCreation = new NpgsqlCommand(
            "SELECT control.ensure_activity_daily_partitions(current_date-30);", admin))
        {
            PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => historicalCreation.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, refused.SqlState);
        }

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

        await using (var source = new NpgsqlCommand("""
            SELECT set_config('sqlobserver.role','Viewer',false),set_config('sqlobserver.target_scope','00000000-0000-4000-8000-000000000001',false);
            """, app)) await source.ExecuteNonQueryAsync();
        await using (var history = new NpgsqlCommand("""
            SELECT count(*) FILTER(WHERE metric_key='engine.sql_scheduler_cpu_percent' AND metric_value=25),
              count(*) FILTER(WHERE metric_key='engine.batch_requests_per_second' AND metric_value=100),
              count(*) FILTER(WHERE metric_value IS NOT NULL AND (metric_key='engine.sql_scheduler_cpu_percent' AND metric_value<>25
                OR metric_key='engine.batch_requests_per_second' AND metric_value<>100)),count(DISTINCT metric_key)
            FROM reporting.overview_workload_history('00000000-0000-4000-8000-000000000001',1,@from,@to,@snapshot);
            """, app))
        {
            history.Parameters.AddWithValue("from", first.SeedFromUtc); history.Parameters.AddWithValue("to", first.SeedToUtc);
            history.Parameters.AddWithValue("snapshot", DateTimeOffset.UtcNow);
            await using NpgsqlDataReader historyRows = await history.ExecuteReaderAsync();
            Assert.True(await historyRows.ReadAsync()); Assert.True(historyRows.GetInt64(0)>0); Assert.True(historyRows.GetInt64(1)>0);
            Assert.Equal(0,historyRows.GetInt64(2)); Assert.Equal(7,historyRows.GetInt64(3));
        }

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
