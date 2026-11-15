using Npgsql;
using NpgsqlTypes;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    [Fact]
    public async Task InactiveHealthReadRouteDoesNotExecuteItsShadowBranch()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId target = new(Guid.NewGuid());
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "engine.core",
            (await ReadRepositoryClockAsync(database)).AddMinutes(-1), outputItemCount: 0);
        await ExecuteAsync(database, """
            SET ROLE sqlobserver_migrator;
            CREATE FUNCTION control.test_inactive_health_branch() RETURNS boolean
            LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $test$
            BEGIN RAISE EXCEPTION 'inactive health branch executed' USING ERRCODE='P0001'; END
            $test$;
            CREATE OR REPLACE VIEW reporting.collector_health_projection_v2_shadow
            WITH (security_barrier=true) AS
            SELECT source.* FROM reporting.collector_health_projection_legacy AS source
            WHERE control.test_inactive_health_branch();
            """);
        await using var command = database.DataSource.CreateCommand("""
            SELECT count(*) FROM reporting.collector_health_projection
            WHERE instance_id=@target AND collector_id='engine.core';
            """);
        command.Parameters.AddWithValue("target", target.Value);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = """
            SELECT count(*) FROM reporting.collector_health_projection_v2_shadow
            WHERE instance_id=@target AND collector_id='engine.core';
            """;
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteScalarAsync());
        Assert.Equal("P0001", failure.SqlState);
        command.CommandText = "SELECT use_v2 FROM system.collection_run_v2_health_read_route WHERE singleton;";
        command.Parameters.Clear();
        Assert.Equal(false, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    public async Task CompletionLookupPreservesRecentAndHistoricalEvidenceOrdering(int ageHours)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddHours(-ageHours).AddMinutes(-3);
        MonitoredInstanceId target = new(Guid.NewGuid()), other = new(Guid.NewGuid());
        Guid first = Guid.NewGuid(), low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid winner = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff0"), failed = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, first, "activity.requests", at.AddMinutes(-1), outputItemCount: 0);
        await SeedTargetAndRunAsync(database, target, low, "activity.requests", at, outputItemCount: 0);
        await SeedTargetAndRunAsync(database, target, winner, "activity.requests", at,
            outputItemCount: 0, startedAt: at.AddMinutes(-30), outcome: "partial", loss: true);
        await SeedTargetAndRunAsync(database, target, failed, "activity.requests", at.AddSeconds(1),
            outputItemCount: 0, outcome: "permission_denied");
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "activity.sessions", at.AddSeconds(2), outputItemCount: 0);
        Guid foreign = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, other, foreign, "activity.requests", at.AddSeconds(3), outputItemCount: 0);

        async Task<Guid?> Find(bool qualified = true, Guid? exact = null,
            DateTimeOffset? before = null, Guid? beforeRun = null, long revision = 1, long? schedule = null)
        {
            await using var command = database.DataSource.CreateCommand("""
                SELECT reporting.find_completed_run(@target,'activity.requests',@revision,
                    @schedule,@qualified,@exact,@before,@before_run);
                """);
            command.Parameters.AddWithValue("target", target.Value);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("qualified", qualified);
            command.Parameters.AddWithValue("schedule", NpgsqlDbType.Bigint, (object?)schedule ?? DBNull.Value);
            command.Parameters.AddWithValue("exact", NpgsqlDbType.Uuid, (object?)exact ?? DBNull.Value);
            command.Parameters.AddWithValue("before", NpgsqlDbType.TimestampTz, (object?)before ?? DBNull.Value);
            command.Parameters.AddWithValue("before_run", NpgsqlDbType.Uuid, (object?)beforeRun ?? DBNull.Value);
            object? value = await command.ExecuteScalarAsync();
            return value is Guid id ? id : null;
        }

        Assert.Equal(winner, await Find());
        Assert.Equal(failed, await Find(qualified: false));
        Assert.Equal(low, await Find(before: at, beforeRun: winner));
        Assert.Equal(first, await Find(before: at, beforeRun: low));
        Assert.Equal(first, await Find(exact: first));
        Assert.Null(await Find(exact: foreign));
        Assert.Null(await Find(exact: failed));
        Assert.Equal(failed, await Find(qualified: false, exact: failed));
        Assert.Null(await Find(revision: 2));
        Assert.Null(await Find(schedule: 2));

        await using var privileges = database.DataSource.CreateCommand("""
            SELECT NOT has_function_privilege('sqlobserver_server',
                'reporting.find_completed_run(uuid,text,bigint,bigint,boolean,uuid,timestamptz,uuid)','EXECUTE')
               AND NOT has_function_privilege('sqlobserver_collector',
                'reporting.find_completed_run(uuid,text,bigint,bigint,boolean,uuid,timestamptz,uuid)','EXECUTE');
            """);
        Assert.Equal(true, await privileges.ExecuteScalarAsync());
    }
}
