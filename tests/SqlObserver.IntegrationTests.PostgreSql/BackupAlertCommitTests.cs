using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class BackupEvidenceReadTests
{
    [Fact]
    public async Task BackupCommitSqlTransitionsMatchProductionAcrossStatesClearCountsGapsAndMaintenance()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026,9,30,12,0,0,TimeSpan.Zero);
        int combinations = 0;
        foreach (AlertRuleKind kind in BindingKinds)
        foreach (AlertState state in Enum.GetValues<AlertState>())
        foreach (int value in new[] { 0,2 })
        foreach (int clear in new[] { 1,2 })
        foreach (bool gap in new[] { false,true })
        foreach (bool suppressed in new[] { false,true })
        {
            if (kind==AlertRuleKind.BackupObservedFaults && value==0) continue;
            var rule = new AlertRuleDefinition(Guid.NewGuid(),"backup.commit",kind,null,AlertComparison.GreaterThan,
                0,0,2,TimeSpan.FromMinutes(5),TimeSpan.FromSeconds(15),clearConfirmationCount:clear);
            AlertRuleState prior = BackupPrior(rule.RuleId,target,state,at);
            DateTimeOffset now = gap ? at.AddMinutes(6) : at.AddSeconds(15);
            BackupFleetSummary summary = BindingSummary(target,now,
                kind==AlertRuleKind.BackupFullPolicy && value>0 ? BindingCounts(violated:2) : BindingCounts(within:2),
                kind==AlertRuleKind.BackupLogPolicy && value>0 ? BindingCounts(violated:2) : BindingCounts(within:2),
                faults:kind==AlertRuleKind.BackupObservedFaults ? value : 0);
            AlertObservation observation = BackupAlertObservationFactory.Create(rule,new(target,1,now,summary,new string('a',64)));
            MaintenanceWindow? maintenance = suppressed ? new(Guid.NewGuid(),target,at.AddMinutes(-1),now.AddMinutes(1),"planned") : null;
            AlertEvaluationResult result = AlertEvaluator.Evaluate(rule,prior,observation,maintenance);
            var expected = BackupDecisionWire(observation,result);
            var before = BackupDecisionWire(observation,new(prior,null,false,"prior"));
            await using var command = database.DataSource.CreateCommand("""
                SELECT alerting.backup_alert_transition(@kind,2,@clear,interval '5 minutes',@prior,@observation,@suppressed)
                    = alerting.canonical_decision_snapshot(@expected);
                """);
            command.Parameters.AddWithValue("kind",(short)kind); command.Parameters.AddWithValue("clear",clear);
            command.Parameters.AddWithValue("prior",NpgsqlDbType.Jsonb,before["State"]!.ToJsonString());
            command.Parameters.AddWithValue("observation",NpgsqlDbType.Jsonb,expected["Observation"]!.ToJsonString());
            command.Parameters.AddWithValue("expected",NpgsqlDbType.Jsonb,expected.ToJsonString());
            command.Parameters.AddWithValue("suppressed",suppressed);
            Assert.Equal(true,await command.ExecuteScalarAsync()); combinations++;
        }
        Assert.Equal(200,combinations);
    }

    [Fact]
    public async Task BackupCommitPersistsFiringResolutionUnknownAndOutboxWithImmutableReplayAfterSourceChanges()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target);
        await using (var configure = database.DataSource.CreateCommand("""
            UPDATE alerting.rule SET confirmation_count=1,clear_confirmation_count=1,evaluation_interval=interval '15 seconds'
                WHERE instance_id=@target;
            """)) { configure.Parameters.AddWithValue("target",target); await configure.ExecuteNonQueryAsync(); }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertRuleDefinition> rules = await repository.ListRulesAsync(new(target),Timeout,CancellationToken.None);
        AlertRuleDefinition log = rules.Single(rule => rule.Kind==AlertRuleKind.BackupLogPolicy);
        await SeedBackupCommitActiveLogAsync(database,target,log.RuleId);
        await SeedBackupCommitDestinationAsync(database,target);
        await CollectAsync(database,target,databaseCount:1,withLog:true);
        await SetPolicyAsync(server,target);
        await using (var policy = server.CreateCommand("""
            SELECT result_status FROM control.set_backup_freshness_policy(@target,1,decode(@database,'hex'),1,gen_random_uuid(),
                true,30,900,300,1,false,'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """))
        { policy.Parameters.AddWithValue("target",target); policy.Parameters.AddWithValue("database",Fingerprint); Assert.Equal("applied",await policy.ExecuteScalarAsync()); }
        WorkerLeaseIdentity overview = await OverviewLeaseAsync(collector);
        BackupFleetSummary summary = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(new(new(target),1),overview,Timeout,CancellationToken.None);
        Assert.True(await new PostgreSqlBackupFleetCachePort(collector).PublishAsync(summary,overview,Timeout,CancellationToken.None));
        IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        AlertEvaluationBatch batch = await BackupCommitBatchAsync(repository,lease,work);
        AlertEvaluationOutcome committed = await repository.EvaluateAndPersistAsync(batch,CancellationToken.None);
        Assert.Equal(3,committed.Evaluated); Assert.Equal(2,committed.Changed);
        AlertRuleDefinition full = rules.Single(rule => rule.Kind==AlertRuleKind.BackupFullPolicy);
        AlertRuleState fired = Assert.IsType<AlertRuleState>(await repository.GetStateAsync(new(target),full.RuleId,Timeout,CancellationToken.None));
        AlertRuleState resolved = Assert.IsType<AlertRuleState>(await repository.GetStateAsync(new(target),log.RuleId,Timeout,CancellationToken.None));
        Assert.Equal(AlertState.Firing,fired.State); Assert.Equal(AlertState.Resolved,resolved.State);
        Assert.NotNull(fired.AlertId); Assert.NotNull(resolved.ResolvedUtc);
        await AssertBackupCommitCountsAsync(database,target,3,3,2,0);
        string before = await BackupCommitSnapshotAsync(database,target);
        await SetPolicyAsync(server,target,enabled:false,expected:2);
        Assert.Equal(0,(await repository.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        Assert.Equal(before,await BackupCommitSnapshotAsync(database,target));
        AlertEvaluationDecision[] divergent = batch.Decisions!.ToArray();
        divergent[0] = divergent[0] with { Reason="forged replay" };
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch with { Decisions=divergent },CancellationToken.None))).SqlState);
        DateTimeOffset due = batch.Observations.Max(observation => observation.ObservedAtUtc).AddSeconds(15);
        TimeSpan remaining = due-await ClockAsync(database);
        if (remaining>TimeSpan.Zero) await Task.Delay(remaining+TimeSpan.FromMilliseconds(10));
        IReadOnlyList<AlertEvaluationWork> unknown = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(3,unknown.Count);
        Assert.All(unknown.SelectMany(item => item.Observations),observation => Assert.Null(observation.Value));
        Assert.Equal(3,(await repository.EvaluateAndPersistAsync(await BackupCommitBatchAsync(repository,lease,unknown),CancellationToken.None)).Evaluated);
        AlertRuleState held = Assert.IsType<AlertRuleState>(await repository.GetStateAsync(new(target),full.RuleId,Timeout,CancellationToken.None));
        Assert.Equal(AlertState.Firing,held.State); Assert.Equal(fired.AlertId,held.AlertId); Assert.Equal(fired.EpisodeId,held.EpisodeId);
        Assert.Equal(fired.FiredUtc,held.FiredUtc); Assert.Null(held.LastValue);
        Assert.Equal(resolved.ResolvedUtc,(await repository.GetStateAsync(new(target),log.RuleId,Timeout,CancellationToken.None))!.ResolvedUtc);
        await AssertBackupCommitCountsAsync(database,target,6,6,2,0);
        var deliveries = new PostgreSqlWorkerLeasePort(collector);
        WorkerLeaseIdentity deliveryLease = Assert.IsType<WorkerLease>((await deliveries.AcquireAsync(
            new(new("alerts/backup/commit-delivery"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(1)),Timeout),CancellationToken.None)).Lease).Identity;
        Assert.Equal(2,(await repository.ClaimDueDeliveriesAsync(deliveryLease,100,Timeout,CancellationToken.None)).Count);
    }

    [Fact]
    public async Task BackupCommitRollsBackAnEntireBatchForDivergentDecisionsAndSourcesChangedAfterClaim()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        AlertEvaluationBatch batch = await BackupCommitBatchAsync(repository,lease,work);
        string unchanged = await BackupCommitSnapshotAsync(database,target);
        AlertEvaluationDecision[] bad = batch.Decisions!.ToArray(); bad[^1] = bad[^1] with { Reason="forged result" };
        Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch with { Decisions=bad },CancellationToken.None))).SqlState);
        Assert.Equal(unchanged,await BackupCommitSnapshotAsync(database,target));
        await CollectAsync(database,target,databaseCount:1,withLog:true); await SetPolicyAsync(server,target);
        WorkerLeaseIdentity overview = await OverviewLeaseAsync(collector);
        BackupFleetSummary summary = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(new(new(target),1),overview,Timeout,CancellationToken.None);
        Assert.True(await new PostgreSqlBackupFleetCachePort(collector).PublishAsync(summary,overview,Timeout,CancellationToken.None));
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch,CancellationToken.None))).SqlState);
        Assert.Equal(unchanged,await BackupCommitSnapshotAsync(database,target));
        await ExpireBackupQueueClaimsAsync(database,target);
        IReadOnlyList<AlertEvaluationWork> fresh = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(3,(await repository.EvaluateAndPersistAsync(await BackupCommitBatchAsync(repository,lease,fresh),CancellationToken.None)).Evaluated);
        await AssertBackupCommitCountsAsync(database,target,3,3,0,3);
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        await AssertBackupCommitCountsAsync(database,target,3,3,0,0);
    }

    [Fact]
    public async Task BackupCommitMixedMetricBatchVerifiesAndRollsBackBothPathsTogether()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target); Guid metricRule = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            SELECT control.ensure_daily_metric_partition(current_date-1);
            SELECT control.ensure_daily_metric_partition(current_date);
            INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,
                confirmation_count,clear_confirmation_count,confirmation_window,evaluation_interval,enabled,updated_at)
            VALUES(@rule,@target,'backup.commit.metric',1,'engine.user_connections',1,1,0,1,1,
                interval '5 minutes',interval '15 seconds',true,clock_timestamp()-interval '1 hour');
            """))
        { seed.Parameters.AddWithValue("rule",metricRule); seed.Parameters.AddWithValue("target",target); await seed.ExecuteNonQueryAsync(); }
        Guid run = Guid.NewGuid(); Guid sample = Guid.NewGuid(); DateTimeOffset at = (await ClockAsync(database)).AddMinutes(-1);
        var metric = new AlertObservation(new(target),metricRule,at,2,null,$"observed|sample={sample:D}|run={run:D}",
            sampleId:sample.ToString("D"),runId:run,sourceKind:"metric_threshold",metricId:"engine.user_connections",
            sourceCollector:"engine.core",sourceVersion:"1",sourceSchemaVersion:1,sourceDigest:new string('a',64));
        await M8AlertingPostgreSqlIntegrationTests.SeedEvaluationQueueAsync(database,metric);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(4,work.Count); AlertEvaluationBatch batch = await BackupCommitBatchAsync(repository,lease,work);
        string untouched = await BackupCommitSnapshotAsync(database,target);
        AlertEvaluationDecision[] bad = batch.Decisions!.Select(decision => decision.Observation.RuleId==metricRule
            ? decision with { Reason="forged metric result" } : decision).ToArray();
        Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch with { Decisions=bad },CancellationToken.None))).SqlState);
        Assert.Equal(untouched,await BackupCommitSnapshotAsync(database,target));
        Assert.Equal(4,(await repository.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        Assert.Equal(AlertState.Firing,(await repository.GetStateAsync(new(target),metricRule,Timeout,CancellationToken.None))!.State);
        await AssertBackupCommitCountsAsync(database,target,4,4,0,0);
        Assert.Equal(0,(await repository.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        await AssertBackupCommitCountsAsync(database,target,4,4,0,0);
    }

    [Fact]
    public async Task BackupCommitConcurrentRetriesPersistOneResultAndCannotExecutePrivateMutationHelpers()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        AlertEvaluationBatch batch = await BackupCommitBatchAsync(repository,lease,
            await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        AlertEvaluationOutcome[] results = await Task.WhenAll(
            repository.EvaluateAndPersistAsync(batch,CancellationToken.None).AsTask(),
            new PostgreSqlAlertRepositoryPort(collector).EvaluateAndPersistAsync(batch,CancellationToken.None).AsTask());
        Assert.Equal(3,results.Sum(result => result.Evaluated));
        await AssertBackupCommitCountsAsync(database,target,3,3,0,0);
        await using var privileges = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner),p.prosecdef,
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='alerting'
              AND p.proname IN ('backup_alert_transition','backup_alert_state_snapshot','commit_backup_alert_evaluation');
            """);
        await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync(); int found=0;
        while (await row.ReadAsync())
        { found++; Assert.Equal("sqlobserver_migrator",row.GetString(0)); Assert.True(row.GetBoolean(1));
          Assert.False(row.GetBoolean(2)); Assert.False(row.GetBoolean(3)); Assert.False(row.GetBoolean(4)); Assert.False(row.GetBoolean(5)); }
        Assert.Equal(3,found);
    }

    private static async Task<AlertEvaluationBatch> BackupCommitBatchAsync(PostgreSqlAlertRepositoryPort repository,
        WorkerLeaseIdentity lease,IReadOnlyList<AlertEvaluationWork> work)
    {
        Assert.NotEmpty(work); AlertObservation[] observations = work.SelectMany(item => item.Observations).ToArray();
        IReadOnlyList<AlertRuleDefinition> rules = await repository.ListRulesAsync(observations[0].TargetId,Timeout,CancellationToken.None);
        var decisions = new List<AlertEvaluationDecision>();
        foreach (AlertObservation observation in observations)
        {
            AlertRuleDefinition rule = rules.Single(candidate => candidate.RuleId==observation.RuleId);
            AlertRuleState prior = await repository.GetStateAsync(observation.TargetId,rule.RuleId,Timeout,CancellationToken.None) ?? new(rule.RuleId,observation.TargetId);
            MaintenanceWindow? maintenance = await repository.GetMaintenanceAsync(observation.TargetId,observation.ObservedAtUtc,Timeout,CancellationToken.None);
            AlertEvaluationResult result = AlertEvaluator.Evaluate(rule,prior,observation,maintenance);
            decisions.Add(new(observation,result.State,result.Event,result.DeliverySuppressed,result.Reason));
        }
        return new(observations,lease,Timeout,decisions,null,work.Min(item => item.DueAtUtc),work);
    }
    private static async Task<string> BackupCommitSnapshotAsync(RepositoryTestDatabase database,Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
              'state',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.rule_id) FROM alerting.rule_state s WHERE s.instance_id=@target),
              'history',(SELECT count(*) FROM alerting.state_history h WHERE h.instance_id=@target),
              'replay',(SELECT count(*) FROM alerting.evaluation_replay r WHERE r.instance_id=@target),
              'outbox',(SELECT count(*) FROM alerting.delivery_outbox d WHERE d.instance_id=@target),
              'queue',(SELECT jsonb_agg(to_jsonb(q) ORDER BY q.operation_id) FROM alerting.evaluation_queue q WHERE q.instance_id=@target),
              'capture',(SELECT count(*) FROM alerting.backup_evaluation_capture c WHERE c.instance_id=@target),
              'audit',(SELECT count(*) FROM audit.activity a WHERE a.action_name='alerting.evaluate'))::text;
            """);
        command.Parameters.AddWithValue("target",target); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    private static async Task AssertBackupCommitCountsAsync(RepositoryTestDatabase database,Guid target,long history,long replay,long outbox,long captures)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM alerting.state_history WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.evaluation_replay WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.backup_evaluation_capture WHERE instance_id=@target);
            """);
        command.Parameters.AddWithValue("target",target); await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync()); Assert.Equal(history,row.GetInt64(0)); Assert.Equal(replay,row.GetInt64(1));
        Assert.Equal(outbox,row.GetInt64(2)); Assert.Equal(captures,row.GetInt64(3));
    }
    private static async Task SeedBackupCommitDestinationAsync(RepositoryTestDatabase database,Guid target)
    {
        Guid destination = Guid.NewGuid();
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,revision,approved,
              approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope,configuration_digest)
            VALUES(@destination,@target,'https-webhook','integration',true,1,true,'https-webhook','integration',1,
              encode(sha256(convert_to(@binding,'UTF8')),'hex'),@target,@config);
            """);
        command.Parameters.AddWithValue("destination",destination); command.Parameters.AddWithValue("target",target);
        command.Parameters.AddWithValue("binding",$"{target:D}|{destination:D}|https-webhook|integration|1");
        command.Parameters.AddWithValue("config",new string('d',64)); await command.ExecuteNonQueryAsync();
    }
    private static async Task SeedBackupCommitActiveLogAsync(RepositoryTestDatabase database,Guid target,Guid rule)
    {
        Guid episode = Guid.NewGuid();
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO alerting.rule_state(instance_id,rule_id,alert_id,alert_episode_id,state,consecutive_matches,first_match_at,
                last_observed_at,fired_at,episode_started_at,last_value,reason,last_reason,revision)
            VALUES(@target,@rule,alerting.sha_uuid('alert|'||@target::text||'|'||@rule::text||'|'||@episode::text),@episode,3,1,
              statement_timestamp()-interval '1 minute',statement_timestamp()-interval '1 minute',
              statement_timestamp()-interval '1 minute',statement_timestamp()-interval '1 minute',1,'seeded-active','seeded-active',2);
            """);
        command.Parameters.AddWithValue("target",target); command.Parameters.AddWithValue("rule",rule);
        command.Parameters.AddWithValue("episode",episode); await command.ExecuteNonQueryAsync();
    }
}
