using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryStorePlanRegressionPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ChangedDominantPlanRequiresComparableSlowdownAndTargetScope()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, timeout),
                CancellationToken.None);
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
                VALUES (@target,@key,'Plan regression target','sql01',1433,interval '5 seconds',
                  'windows_integrated_service_identity','mandatory_validated','active',1,
                  statement_timestamp(),statement_timestamp(),statement_timestamp());
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                INSERT INTO events.query_store_group_delta
                 (observed_at,run_id,instance_id,target_revision,database_id,
                  database_incarnation,query_fingerprint,plan_fingerprint,
                  plan_initial_compile_at,source_interval_id,interval_start,
                  interval_end,execution_type,source_window_start,source_window_end,
                  runtime_transition,cpu_ms,duration_ms,execution_count,
                  logical_reads,writes,rows_processed,wait_transition)
                SELECT @now-(sample.minutes_ago*interval '1 minute'),gen_random_uuid(),
                       @target,1,6,@incarnation,
                       decode(repeat(sample.query_digit,64),'hex'),
                       decode(repeat(sample.plan_digit,64),'hex'),
                       @now-interval '1 day',sample.interval_id,
                       @now-(sample.minutes_ago*interval '1 minute')-interval '5 minutes',
                       @now-(sample.minutes_ago*interval '1 minute'),0,
                       @now-(sample.minutes_ago*interval '1 minute')-interval '5 minutes',
                       @now-(sample.minutes_ago*interval '1 minute'),
                       sample.transition,
                       CASE WHEN sample.transition='comparable' THEN 10 END,
                       sample.duration_ms,sample.executions,
                       CASE WHEN sample.transition='comparable' THEN 10 END,
                       CASE WHEN sample.transition='comparable' THEN 0 END,
                       CASE WHEN sample.transition='comparable' THEN 0 END,
                       'unavailable'
                FROM (VALUES
                    (100,1,'a','b','comparable',200,10),
                    (90,2,'a','b','comparable',200,10),
                    (40,3,'a','c','comparable',1200,10),
                    (30,4,'a','c','comparable',1200,10),
                    (20,5,'a','c','reset',NULL::integer,NULL::integer),
                    (100,6,'d','e','comparable',200,10),
                    (90,7,'d','e','comparable',200,10),
                    (40,8,'d','e','comparable',1200,10),
                    (30,9,'d','e','comparable',1200,10)
                ) AS sample(minutes_ago,interval_id,query_digit,plan_digit,
                            transition,duration_ms,executions);
                """, connection, transaction);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"group.plan.regression.{target:N}");
            seed.Parameters.AddWithValue("scope", target.ToString());
            seed.Parameters.AddWithValue("incarnation", Guid.NewGuid());
            seed.Parameters.AddWithValue("now", now);
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var request = new QueryStorePlanRegressionRequest(new MonitoredInstanceId(target),
            now.AddHours(-1), now, 25, timeout);
        QueryStorePlanRegressionResult result = await new PostgreSqlQueryStorePlanRegressionPort(server)
            .ReadAsync(request, CancellationToken.None);
        QueryStorePlanRegression item = Assert.Single(result.Items);
        Assert.Equal(new string('a', 64), item.Query.QueryFingerprint);
        Assert.Equal(new string('b', 64), item.PreviousPlanFingerprint);
        Assert.Equal(new string('c', 64), item.CurrentPlanFingerprint);
        Assert.Equal("20", item.PreviousAverageMilliseconds);
        Assert.Equal("120", item.CurrentAverageMilliseconds);
        Assert.Equal("6", item.SlowdownRatio);
        Assert.Equal(2, item.CurrentComparableRows);
        Assert.Equal(3, item.CurrentSourceRows);

        await using NpgsqlConnection scoped = await server.OpenConnectionAsync();
        await using (var claim = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target,false);", scoped))
        {
            claim.Parameters.AddWithValue("target", target.ToString());
            await claim.ExecuteNonQueryAsync();
        }
        await using var denied = new NpgsqlCommand("""
            SELECT count(*) FROM reporting.list_query_store_plan_regressions(
                @other,@from_utc,@to_utc,25);
            """, scoped);
        denied.Parameters.AddWithValue("other", Guid.NewGuid());
        denied.Parameters.AddWithValue("from_utc", now.AddHours(-1));
        denied.Parameters.AddWithValue("to_utc", now);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => denied.ExecuteScalarAsync());
        Assert.Equal("42501", exception.SqlState);
    }
}
