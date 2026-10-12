using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryStoreWaitRankingPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task RankingUsesComparableWaitDeltasAndEnforcesTargetScope()
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
                VALUES (@target,@key,'Wait ranking target','sql01',1433,interval '5 seconds',
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
                  @now-interval '1 day',2,@now-interval '1 hour',@now,0,
                  @now-interval '2 hours',@now,'comparable',
                  10,20,1,30,4,5,'comparable','{"3":5,"6":7}'::jsonb,@now),
                 (@now-interval '15 minutes',gen_random_uuid(),@target,1,6,
                 @incarnation,decode(repeat('c',64),'hex'),decode(repeat('d',64),'hex'),
                 @now-interval '1 day',3,@now-interval '1 hour',@now,0,
                 @now-interval '2 hours',@now,'comparable',
                  10,20,1,30,4,5,'comparable','{"3":30}'::jsonb,@now),
                 (@now-interval '10 minutes',gen_random_uuid(),@target,1,6,
                  @incarnation,decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),
                  @now-interval '1 day',4,@now-interval '1 hour',@now,0,
                  @now-interval '2 hours',@now,'baseline_unavailable',
                  NULL,NULL,NULL,NULL,NULL,NULL,'unavailable',NULL,NULL);
                """, connection, transaction);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"group.wait.ranking.{target:N}");
            seed.Parameters.AddWithValue("scope", target.ToString());
            seed.Parameters.AddWithValue("incarnation", Guid.NewGuid());
            seed.Parameters.AddWithValue("now", now);
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var request = new QueryStoreWaitRankingRequest(new MonitoredInstanceId(target),
            now.AddHours(-1), now, 25, timeout);
        QueryStoreWaitRankingResult ranking = await new PostgreSqlQueryStoreWaitRankingPort(server)
            .ReadAsync(request, CancellationToken.None);
        Assert.Equal(2, ranking.Items.Count);
        Assert.Equal(new string('c', 64), ranking.Items[0].Plan.Query.QueryFingerprint);
        Assert.Equal("30", ranking.Items[0].WaitMilliseconds);
        Assert.Equal(1, ranking.Items[0].ComparableRows);
        Assert.Equal(1, ranking.Items[0].SourceRows);
        Assert.Equal("12", ranking.Items[1].WaitMilliseconds);
        Assert.Equal(1, ranking.Items[1].ComparableRows);
        Assert.Equal(3, ranking.Items[1].SourceRows);

        var categoryRequest = new QueryStoreWaitRankingRequest(new MonitoredInstanceId(target),
            now.AddHours(-1), now, 25, timeout, 6);
        QueryStoreWaitRankingResult categoryRanking = await new PostgreSqlQueryStoreWaitRankingPort(server)
            .ReadAsync(categoryRequest, CancellationToken.None);
        QueryStoreWaitRankedPlan categoryPlan = Assert.Single(categoryRanking.Items);
        Assert.Equal(6, categoryRanking.Category);
        Assert.Equal(new string('b', 64), categoryPlan.Plan.PlanFingerprint);
        Assert.Equal("7", categoryPlan.WaitMilliseconds);
        Assert.Equal(1, categoryPlan.ComparableRows);
        Assert.Equal(3, categoryPlan.SourceRows);
        Assert.InRange(categoryPlan.LastObservedAtUtc,
            now.AddMinutes(-20).AddTicks(-10), now.AddMinutes(-20).AddTicks(10));

        var emptyCategory = new QueryStoreWaitRankingRequest(new MonitoredInstanceId(target),
            now.AddHours(-1), now, 25, timeout, 2);
        Assert.Empty((await new PostgreSqlQueryStoreWaitRankingPort(server)
            .ReadAsync(emptyCategory, CancellationToken.None)).Items);

        await using NpgsqlConnection scoped = await server.OpenConnectionAsync();
        await using (var claim = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target,false);", scoped))
        {
            claim.Parameters.AddWithValue("target", target.ToString());
            await claim.ExecuteNonQueryAsync();
        }
        await using var denied = new NpgsqlCommand("""
            SELECT count(*) FROM reporting.list_top_query_store_waits_for_category(
                @other,@from_utc,@to_utc,25,6);
            """, scoped);
        denied.Parameters.AddWithValue("other", Guid.NewGuid());
        denied.Parameters.AddWithValue("from_utc", now.AddHours(-1));
        denied.Parameters.AddWithValue("to_utc", now);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => denied.ExecuteScalarAsync());
        Assert.Equal("42501", exception.SqlState);
    }
}
