using Npgsql;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Collector;
using Microsoft.Extensions.Logging.Abstractions;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    [Fact]
    public async Task BlockingRuntimeBackgroundWorkerCommitsUsingProductionSourceAndLeaseServices()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); Guid run=Guid.NewGuid(); DateTimeOffset at=await ReadRepositoryClockAsync(database);
        await SeedTargetAndRunAsync(database,target,run,"blocking.current",at,1);
        await EnsureBlockingPartitionAsync(database,at); await InsertBlockingAlertEdgeAsync(database,target,run,at,71,65001);
        Guid rule=await SeedBlockingQueueRuleAsync(database,target);
        await ExecuteAsync(database,"UPDATE alerting.rule SET confirmation_count=1 WHERE rule_id=@rule;",("rule",rule));
        await SeedBlockingCommitDestinationAsync(database,target);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource(); var repository=new PostgreSqlAlertRepositoryPort(collector);
        using var worker=new AlertEvaluationWorker(new PostgreSqlAlertEvaluationSource(repository),repository,
            new PostgreSqlWorkerLeasePort(collector),new(Guid.NewGuid()),NullLogger<AlertEvaluationWorker>.Instance);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            AlertRuleState? state;
            do
            {
                await Task.Delay(50,deadline.Token);
                state=await repository.GetStateAsync(target,rule,Timeout,deadline.Token);
            } while(state?.State!=AlertState.Firing);
            Assert.NotNull(state.AlertId); Assert.Equal(65.001,state.LastValue);
        }
        finally {await worker.StopAsync(CancellationToken.None);}
        Assert.Equal(1L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target;"));
    }
    [Fact]
    public async Task BlockingRuntimeDiscoversClaimsAndAtomicallyDispatchesMixedEngineBackupAndBlockingWork()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); DateTimeOffset at=await ReadRepositoryClockAsync(database);
        Guid run=Guid.NewGuid(),core=Guid.NewGuid(),metric=Guid.NewGuid();
        await SeedTargetAndRunAsync(database,target,run,"blocking.current",at,1);
        await SeedTargetAndRunAsync(database,target,core,"engine.core",at,1);
        await EnsureBlockingPartitionAsync(database,at); await InsertBlockingAlertEdgeAsync(database,target,run,at,71,65001);
        Guid blocking=await SeedBlockingQueueRuleAsync(database,target);
        await ExecuteAsync(database,"""
            UPDATE alerting.rule SET confirmation_count=1 WHERE rule_id=@blocking;
            SELECT control.ensure_daily_metric_partition(@at::date);
            INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,
              confirmation_count,clear_confirmation_count,confirmation_window,evaluation_interval,updated_at)
            VALUES(@metric,@target,'connections.high',1,'engine.user_connections',1,80,5,1,1,interval '0 seconds',interval '15 seconds',@at-interval '1 minute'),
              (gen_random_uuid(),@target,'backup.full',3,NULL,1,0,0,2,2,interval '5 minutes',interval '30 seconds',@at-interval '1 minute');
            INSERT INTO telemetry.raw_metric_sample(observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
              VALUES(@at,gen_random_uuid(),@target,'engine.user_connections',90,'{}',@at,@core);
            """,("at",at),("target",target.Value),("blocking",blocking),("metric",metric),("core",core));
        await SeedBlockingCommitDestinationAsync(database,target);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector); var repository=new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork> work=await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(3,work.Count); Assert.Equal(3,work.SelectMany(w=>w.Observations).Select(o=>o.SourceKind).Distinct().Count());
        AlertEvaluationBatch batch=await BlockingRuntimeBatchAsync(repository,lease,work);
        var decisions=batch.Decisions!.ToArray(); int position=Array.FindIndex(decisions,d=>d.Observation.RuleId==blocking);
        decisions[position]=decisions[position] with{Reason="forged blocking decision"};
        string before=await BlockingCommitSnapshotAsync(database,target);
        Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(async()=>
            await repository.EvaluateAndPersistAsync(batch with{Decisions=decisions},CancellationToken.None))).SqlState);
        Assert.Equal(before,await BlockingCommitSnapshotAsync(database,target));
        AlertEvaluationOutcome committed=await repository.EvaluateAndPersistAsync(batch,CancellationToken.None);
        Assert.Equal(3,committed.Evaluated); Assert.Equal(2,committed.Changed);
        Assert.Equal(AlertState.Firing,(await repository.GetStateAsync(target,blocking,Timeout,CancellationToken.None))!.State);
        Assert.Equal(AlertState.Firing,(await repository.GetStateAsync(target,metric,Timeout,CancellationToken.None))!.State);
        before=await BlockingCommitSnapshotAsync(database,target);
        Assert.Equal(0,(await repository.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        Assert.Equal(before,await BlockingCommitSnapshotAsync(database,target));
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
    }

    [Fact]
    public async Task BlockingRuntimePreservesLiveRevisedClaimsAndSharesTheFleetClaimBudget()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var first=new MonitoredInstanceId(Guid.NewGuid()); var second=new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database,first); await InsertActiveTargetAsync(database,second);
        await SeedBlockingQueueRuleAsync(database,first); await SeedBlockingQueueRuleAsync(database,first);
        await SeedBlockingQueueRuleAsync(database,second);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector); var repository=new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork> work=await repository.ClaimDueEvaluationsAsync(lease,2,Timeout,CancellationToken.None);
        Assert.InRange(work.Count,1,2);
        IReadOnlyList<AlertEvaluationWork> rest=await repository.ClaimDueEvaluationsAsync(lease,2,Timeout,CancellationToken.None);
        AlertEvaluationWork[] all=work.Concat(rest).ToArray(); Assert.Equal(3,all.Length);
        Assert.Equal(3,all.Select(w=>w.OperationId).Distinct().Count());
        AlertObservation claimed=all[0].Observations[0]; var target=claimed.TargetId;
        await ExecuteAsync(database,"UPDATE alerting.rule SET revision=revision+1 WHERE rule_id=@rule;",("rule",claimed.RuleId));
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        await using(var live=database.DataSource.CreateCommand("SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND rule_id=@rule AND completed_at IS NULL AND claimed_until>clock_timestamp();"))
        {
            live.Parameters.AddWithValue("target",target.Value); live.Parameters.AddWithValue("rule",claimed.RuleId);
            Assert.Equal(1L,await live.ExecuteScalarAsync());
        }
        await ExpireBlockingQueueClaimsAsync(database,target);
        IReadOnlyList<AlertEvaluationWork> reclaimed=await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Contains(reclaimed,w=>w.Observations[0].RuleId==claimed.RuleId&&w.OperationId!=claimed.OperationId);
        var wrong=new WorkerLeaseIdentity(lease.Key,new(Guid.NewGuid()),lease.FencingToken);
        Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(async()=>
            await repository.ClaimDueEvaluationsAsync(wrong,100,Timeout,CancellationToken.None))).SqlState);
    }

    [Fact]
    public async Task BlockingRuntimeChecksEveryTargetPastTheFirstHundredWithoutRepeatingConsumedSnapshots()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        for(int index=0;index<129;index++)
        {
            var target=new MonitoredInstanceId(Guid.NewGuid()); await InsertActiveTargetAsync(database,target);
            await SeedBlockingQueueRuleAsync(database,target);
        }
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector); var repository=new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork> first=await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(100,first.Count);
        IReadOnlyList<AlertEvaluationWork> second=await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(29,second.Count);
        AlertEvaluationWork[] all=first.Concat(second).ToArray(); Assert.Equal(129,all.Select(w=>w.Observations[0].TargetId.Value).Distinct().Count());
        foreach(AlertEvaluationWork item in all)
            Assert.Equal(1,(await repository.EvaluateAndPersistAsync(await BlockingRuntimeBatchAsync(repository,lease,[item]),CancellationToken.None)).Evaluated);
        await Task.Delay(TimeSpan.FromSeconds(5.1)); DateTimeOffset marker=await ReadRepositoryClockAsync(database);
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        await using var checkedTargets=database.DataSource.CreateCommand("SELECT count(*) FROM alerting.blocking_reconciliation_cursor WHERE last_checked_at>=@marker;");
        checkedTargets.Parameters.AddWithValue("marker",marker); Assert.Equal(100L,await checkedTargets.ExecuteScalarAsync());
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        Assert.Equal(129L,await checkedTargets.ExecuteScalarAsync());
    }

    private static async Task<AlertEvaluationBatch> BlockingRuntimeBatchAsync(PostgreSqlAlertRepositoryPort repository,
        WorkerLeaseIdentity lease,IReadOnlyList<AlertEvaluationWork> work)
    {
        AlertObservation[] observations=work.SelectMany(w=>w.Observations).ToArray(); MonitoredInstanceId target=observations[0].TargetId;
        IReadOnlyList<AlertRuleDefinition> rules=await repository.ListRulesAsync(target,Timeout,CancellationToken.None);
        var decisions=new List<AlertEvaluationDecision>();
        foreach(AlertObservation observation in observations.OrderBy(o=>o.ObservedAtUtc).ThenBy(o=>o.RuleId))
        {
            AlertRuleDefinition rule=rules.Single(r=>r.RuleId==observation.RuleId);
            AlertRuleState prior=await repository.GetStateAsync(target,rule.RuleId,Timeout,CancellationToken.None)??new(rule.RuleId,target);
            MaintenanceWindow? maintenance=await repository.GetMaintenanceAsync(target,observation.ObservedAtUtc,Timeout,CancellationToken.None);
            AlertEvaluationResult result=AlertEvaluator.Evaluate(rule,prior,observation,maintenance);
            decisions.Add(new(observation,result.State,result.Event,result.DeliverySuppressed,result.Reason));
        }
        return new(observations,lease,Timeout,decisions,ClaimedWork:work);
    }
}
