using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryStoreGroupPlanTrendPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task TrendSeparatesComparableDeltasFromBaselinesAndEnforcesTargetScope()
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
        Guid otherTarget = Guid.NewGuid();
        Guid incarnation = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.observation_target
                 (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                  authentication_mode,transport_security_mode,lifecycle_state,revision,
                  created_at,updated_at,discovery_requested_at)
                VALUES (@target,@key,'Trend target','sql01',1433,interval '5 seconds',
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
                  logical_reads,writes,rows_processed,wait_transition,wait_delta,
                  wait_observed_at)
                VALUES
                 (@now-interval '25 minutes',gen_random_uuid(),@target,1,6,
                  @incarnation,decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),
                  @now-interval '1 day',1,@now-interval '1 hour',@now,0,
                  @now-interval '2 hours',@now,'baseline_unavailable',
                  NULL,NULL,NULL,NULL,NULL,NULL,'baseline_unavailable',NULL,NULL),
                 (@now-interval '20 minutes',gen_random_uuid(),@target,1,6,
                  @incarnation,decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),
                  @now-interval '1 day',1,@now-interval '1 hour',@now,0,
                  @now-interval '2 hours',@now,'comparable',
                  10,20,1,30,4,5,'comparable','{"3":5}'::jsonb,@now),
                 (@now-interval '15 minutes',gen_random_uuid(),@target,1,6,
                  @incarnation,decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),
                  @now-interval '1 day',1,@now-interval '1 hour',@now,0,
                  @now-interval '2 hours',@now,'reset',
                  NULL,NULL,NULL,NULL,NULL,NULL,'reset',NULL,NULL);
                """, connection, transaction);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"group.trend.{target:N}");
            seed.Parameters.AddWithValue("scope", target.ToString());
            seed.Parameters.AddWithValue("incarnation", incarnation);
            seed.Parameters.AddWithValue("now", now);
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var plan = new PlanOpaqueIdentity(
            new QueryOpaqueIdentity(6, new string('a', 64)), new string('b', 64));
        var request = new QueryStoreGroupPlanTrendRequest(new MonitoredInstanceId(target),
            plan, now.AddHours(-1), now, timeout);
        QueryStoreGroupPlanTrendResult trend = await new PostgreSqlQueryStoreGroupPlanTrendPort(
            server).ReadAsync(request, CancellationToken.None);
        Assert.InRange(trend.Points.Count, 1, 2);
        Assert.Equal(3, trend.Points.Sum(static point => point.SourceRows));
        Assert.Equal(1, trend.Points.Sum(static point => point.RuntimeComparableRows));
        Assert.Equal(1, trend.Points.Sum(static point => point.RuntimeBaselineRows));
        Assert.Equal(1, trend.Points.Sum(static point => point.RuntimeResetRows));
        QueryStoreGroupPlanTrendPoint point = Assert.Single(trend.Points,
            static item => item.RuntimeComparableRows == 1);
        Assert.Equal("10", point.CpuMilliseconds);
        Assert.Equal("20", point.DurationMilliseconds);
        Assert.Equal("5", point.WaitCategories[3]);
        Assert.Equal(1, point.WaitComparableRows);

        await using NpgsqlConnection scoped = await server.OpenConnectionAsync();
        await using (var claim = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target,false);", scoped))
        {
            claim.Parameters.AddWithValue("target", target.ToString());
            await claim.ExecuteNonQueryAsync();
        }
        await using var denied = new NpgsqlCommand("""
            SELECT count(*) FROM control.get_query_store_group_plan_trend(
              @other,6,decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),@from_utc,@to_utc);
            """, scoped);
        denied.Parameters.AddWithValue("other", otherTarget);
        denied.Parameters.AddWithValue("from_utc", now.AddHours(-1));
        denied.Parameters.AddWithValue("to_utc", now);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => denied.ExecuteScalarAsync());
        Assert.Equal("22023", exception.SqlState);
    }
}
