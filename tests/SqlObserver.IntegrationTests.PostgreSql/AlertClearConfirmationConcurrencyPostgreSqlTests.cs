using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    [Fact]
    public async Task ClearConfirmationConcurrentRuleUpdateFencesPreviouslyClaimedEvaluation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        Guid ruleId = Guid.NewGuid();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/clear-concurrent-update"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        var rule = new AlertRuleDefinition(ruleId, "connections.clear.concurrent", AlertRuleKind.MetricThreshold,
            new MetricId("engine.user_connections"), AlertComparison.GreaterThan, 20, 2, 1,
            TimeSpan.Zero, TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        await new PostgreSqlAlertRepositoryPort(serverSource).UpsertRuleAsync(
            new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"), Audit(target, AdministrativeAuditAction.CreateAlertRule), Timeout()),
            CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target.Value, Guid.NewGuid());
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorSource);
        DateTimeOffset start = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-5));
        var (_, firing) = await PersistClearConfirmationObservationAsync(database, repository, lease, rule, target, start, 25);
        Assert.Equal(AlertState.Firing, firing.State);
        Assert.Equal(0, firing.ConsecutiveClears);
        AlertEvaluationBatch claimed = await ClaimClearConfirmationObservationAsync(database, repository, lease, rule, target, start.AddSeconds(30), 10);
        Assert.Equal(1, Assert.Single(claimed.Decisions!).State.ConsecutiveClears);
        ReplaySnapshot beforeUpdate = await SnapshotReplayRowsAsync(database, target.Value, ruleId);

        await using NpgsqlConnection adminConnection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction adminTransaction = await adminConnection.BeginTransactionAsync();
        await using var backend = new NpgsqlCommand("SELECT pg_backend_pid()", adminConnection, adminTransaction);
        int adminPid = (int)(await backend.ExecuteScalarAsync())!;
        // Hold the same rule revision/settings row write made by administrative
        // upsert open. This fixture mutation isolates its row lock from the
        // administrative audit and outbox side effects.
        await using (var update = new NpgsqlCommand("""
            UPDATE alerting.rule
            SET revision=revision+1,clear_confirmation_count=1,updated_at=clock_timestamp()
            WHERE instance_id=@target AND rule_id=@rule AND revision=1
            """, adminConnection, adminTransaction))
        {
            update.Parameters.AddWithValue("target", target.Value);
            update.Parameters.AddWithValue("rule", ruleId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        Task<AlertEvaluationOutcome> evaluation = repository.EvaluateAndPersistAsync(claimed, CancellationToken.None).AsTask();
        using (var waitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await using NpgsqlConnection observer = await database.DataSource.OpenConnectionAsync(waitDeadline.Token);
            await using var blocked = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_stat_activity activity
                    WHERE activity.datname=current_database()
                      AND activity.state='active' AND activity.wait_event_type='Lock'
                      AND activity.query LIKE '%alerting.evaluate_and_enqueue(%'
                      AND @admin_pid=ANY(pg_catalog.pg_blocking_pids(activity.pid))
                )
                """, observer);
            blocked.Parameters.AddWithValue("admin_pid", adminPid);
            while (await blocked.ExecuteScalarAsync(waitDeadline.Token) is not true)
            {
                // Poll an observed database lock dependency; elapsed time alone
                // never establishes that the evaluation reached the lock.
                await Task.Delay(TimeSpan.FromMilliseconds(25), waitDeadline.Token);
            }
        }
        Assert.False(evaluation.IsCompleted);
        await adminTransaction.CommitAsync();
        PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(() => evaluation);
        Assert.Equal("40001", conflict.SqlState);

        Assert.Equal(beforeUpdate, await SnapshotReplayRowsAsync(database, target.Value, ruleId));
        AlertRuleState unchanged = (await repository.GetStateAsync(target, ruleId, Timeout(), CancellationToken.None))!;
        Assert.Equal(AlertState.Firing, unchanged.State);
        Assert.Equal(0, unchanged.ConsecutiveClears);
        Assert.Equal(firing.AlertId, unchanged.AlertId);
        Assert.Equal(firing.EpisodeId, unchanged.EpisodeId);
        Assert.Equal(firing.Revision, unchanged.Revision);
        Assert.Equal(firing.LastOperationId, unchanged.LastOperationId);
        await using var verify = database.DataSource.CreateCommand("""
            SELECT revision,clear_confirmation_count,
                (SELECT count(*) FROM alerting.state_history WHERE instance_id=@target AND rule_id=@rule)
            FROM alerting.rule WHERE instance_id=@target AND rule_id=@rule
            """);
        verify.Parameters.AddWithValue("target", target.Value);
        verify.Parameters.AddWithValue("rule", ruleId);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }
}
