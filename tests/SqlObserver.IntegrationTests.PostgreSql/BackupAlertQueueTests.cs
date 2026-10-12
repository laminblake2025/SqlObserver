using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class BackupEvidenceReadTests
{
    [Fact]
    public async Task BackupQueueCapturesUnknownOnceAndReclaimsTheSameDurableOperationsAfterClaimExpiry()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork> first = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(3,first.Count);
        AlertObservation[] observations = first.SelectMany(work => work.Observations).ToArray();
        Assert.Single(observations.Select(observation => observation.ObservedAtUtc).Distinct());
        Assert.All(observations,observation =>
        {
            Assert.Null(observation.Value); Assert.Null(observation.RunId); Assert.Null(observation.SourceDigest);
            Assert.Equal(observation.OperationId.ToString("D"),observation.SampleId);
        });
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        foreach (AlertObservation observation in observations) await AssertBackupQueueClaimAsync(database,target,observation,lease);
        await ExpireBackupQueueClaimsAsync(database,target);
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBackupQueueClaimAsync(database,target,observations[0],lease))).SqlState);
        IReadOnlyList<AlertEvaluationWork> reclaimed = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(first.Select(work => work.OperationId).Order(),reclaimed.Select(work => work.OperationId).Order());
        Assert.Equal(observations.Select(observation => observation.EvidenceDigest).Order(),
            reclaimed.SelectMany(work => work.Observations).Select(observation => observation.EvidenceDigest).Order());
        foreach (AlertObservation observation in reclaimed.SelectMany(work => work.Observations))
            await AssertBackupQueueClaimAsync(database,target,observation,lease);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.backup_evaluation_capture WHERE instance_id=@target;");
        count.Parameters.AddWithValue("target",target); Assert.Equal(3L,await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BackupQueueSupersedesStaleSourcesAndPolicyRevisionsWithoutCancellingLiveClaims()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork> missing = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(3,missing.Count);
        await CollectAsync(database,target,databaseCount:1,withLog:true);
        await SetPolicyAsync(server,target);
        WorkerLeaseIdentity overview = await OverviewLeaseAsync(collector);
        BackupFleetSummary summary = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(new(new(target),1),overview,Timeout,CancellationToken.None);
        Assert.True(await new PostgreSqlBackupFleetCachePort(collector).PublishAsync(summary,overview,Timeout,CancellationToken.None));
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBackupQueueClaimAsync(database,target,missing[0].Observations[0],lease))).SqlState);
        await ExpireBackupQueueClaimsAsync(database,target);
        IReadOnlyList<AlertEvaluationWork> current = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(3,current.Count);
        Assert.Empty(missing.Select(work => work.OperationId).Intersect(current.Select(work => work.OperationId)));
        IReadOnlyList<AlertRuleDefinition> rules = await repository.ListRulesAsync(new(target),Timeout,CancellationToken.None);
        foreach (AlertObservation observation in current.SelectMany(work => work.Observations))
        {
            AlertRuleDefinition rule = rules.Single(candidate => candidate.RuleId==observation.RuleId);
            AlertObservation expected = BackupAlertObservationFactory.Create(rule,new(new(target),1,observation.ObservedAtUtc,summary,observation.SourceDigest));
            Assert.Equal(expected,observation);
            await AssertBackupQueueClaimAsync(database,target,observation,lease);
        }
        Assert.Equal(2,current.SelectMany(work => work.Observations).Count(observation => observation.Value==0));
        Assert.Single(current.SelectMany(work => work.Observations),observation => observation.Value is null);
        await AssertBackupQueueCancellationAsync(database,target,"backup_source_superseded",3);
        await SetPolicyAsync(server,target,enabled:false,expected:1);
        await ExpireBackupQueueClaimsAsync(database,target);
        IReadOnlyList<AlertEvaluationWork> invalidated = await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None);
        Assert.Equal(3,invalidated.Count);
        Assert.All(invalidated.SelectMany(work => work.Observations),observation => Assert.Null(observation.Value));
        await AssertBackupQueueCancellationAsync(database,target,"backup_source_superseded",6);
        await using (var revise = database.DataSource.CreateCommand("UPDATE alerting.rule SET revision=revision+1,updated_at=clock_timestamp() WHERE instance_id=@target;"))
        { revise.Parameters.AddWithValue("target",target); await revise.ExecuteNonQueryAsync(); }
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBackupQueueClaimAsync(database,target,invalidated[0].Observations[0],lease))).SqlState);
        await ExpireBackupQueueClaimsAsync(database,target);
        Assert.Equal(3,(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None)).Count);
        await AssertBackupQueueCancellationAsync(database,target,"rule_revision_stale",3);
        await ExpireBackupQueueClaimsAsync(database,target);
        await AdminTargetAsync(database,target,"UPDATE control.observation_target SET lifecycle_state='disabled',revision=revision+1 WHERE instance_id=@target;");
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        await AssertBackupQueueCancellationAsync(database,target,"target_not_active",3);
    }

    [Fact]
    public async Task BackupQueueHonorsGlobalClaimBudgetCadenceAndPrivateSourcePrivileges()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        var work = new List<AlertEvaluationWork>();
        for (int i=0;i<3;i++) work.Add(Assert.Single(await repository.ClaimDueEvaluationsAsync(lease,1,Timeout,CancellationToken.None)));
        Assert.Equal(3,work.Select(item => item.OperationId).Distinct().Count());
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,1,Timeout,CancellationToken.None));
        AlertObservation original = work[0].Observations[0];
        var forged = new AlertObservation(original.TargetId,original.RuleId,original.ObservedAtUtc,1,null,original.Reason,
            original.OperationId,sampleId:original.SampleId,sourceKind:original.SourceKind,sourceCollector:original.SourceCollector,
            sourceVersion:original.SourceVersion,sourceSchemaVersion:original.SourceSchemaVersion);
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBackupQueueClaimAsync(database,target,forged,lease))).SqlState);
        await using (var complete = database.DataSource.CreateCommand("""
            UPDATE alerting.evaluation_queue SET completed_at=clock_timestamp(),claimed_until=NULL,
                claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL WHERE instance_id=@target;
            """)) { complete.Parameters.AddWithValue("target",target); await complete.ExecuteNonQueryAsync(); }
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        await using (var remaining = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.backup_evaluation_capture WHERE instance_id=@target;"))
        { remaining.Parameters.AddWithValue("target",target); Assert.Equal(0L,await remaining.ExecuteScalarAsync()); }
        await using var privileges = database.DataSource.CreateCommand("""
            SELECT c.relrowsecurity,c.relforcerowsecurity,
              has_table_privilege('sqlobserver_collector',c.oid,'SELECT,INSERT,UPDATE,DELETE'),
              has_table_privilege('sqlobserver_server',c.oid,'SELECT,INSERT,UPDATE,DELETE'),
              has_function_privilege('sqlobserver_collector','alerting.assert_backup_evaluation_claim(uuid,jsonb,text,uuid,bigint)','EXECUTE'),
              has_function_privilege('sqlobserver_collector','alerting.list_legacy_targets_with_due_alert_work(text,text,uuid,bigint,integer)','EXECUTE')
            FROM pg_class c WHERE c.oid='alerting.backup_evaluation_capture'::regclass;
            """);
        await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.True(row.GetBoolean(0)); Assert.True(row.GetBoolean(1));
        Assert.False(row.GetBoolean(2)); Assert.False(row.GetBoolean(3)); Assert.False(row.GetBoolean(4)); Assert.False(row.GetBoolean(5));
        Assert.All(await repository.ListRulesAsync(new(target),Timeout,CancellationToken.None),rule => Assert.True(AlertCatalog.IsApproved(rule)));
    }

    [Fact]
    public async Task BackupQueueConcurrentProducersCreateOnePendingCapturePerRuleAndRejectOtherCoordinatorClaims()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await SeedBackupQueueRulesAsync(database,target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var first = new PostgreSqlAlertRepositoryPort(collector);
        var second = new PostgreSqlAlertRepositoryPort(collector);
        IReadOnlyList<AlertEvaluationWork>[] results = await Task.WhenAll(
            first.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None).AsTask(),
            second.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None).AsTask());
        Assert.Equal(3,results.Sum(result => result.Count));
        Assert.Equal(3,results.SelectMany(result => result).Select(work => work.Observations[0].RuleId).Distinct().Count());
        await ExpireBackupQueueClaimsAsync(database,target);
        WorkerLeaseIdentity overview = await OverviewLeaseAsync(collector);
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction))
        { scope.Parameters.AddWithValue("target",target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        await using var wrong = new NpgsqlCommand("SELECT * FROM alerting.claim_due_evaluations(@target,@key,@owner,@fence,100);",connection,transaction);
        wrong.Parameters.AddWithValue("target",target); wrong.Parameters.AddWithValue("key",overview.Key.Value);
        wrong.Parameters.AddWithValue("owner",overview.Owner.Value); wrong.Parameters.AddWithValue("fence",overview.FencingToken.Value);
        Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(async () => await wrong.ExecuteNonQueryAsync())).SqlState);
    }

    private static async Task SeedBackupQueueRulesAsync(RepositoryTestDatabase database,Guid target)
    {
        foreach (AlertRuleKind kind in BindingKinds)
        {
            await using var command = database.DataSource.CreateCommand("""
                INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,
                    confirmation_count,clear_confirmation_count,confirmation_window,evaluation_interval,enabled,updated_at)
                VALUES(gen_random_uuid(),@target,@name,@kind,NULL,1,0,0,2,2,interval '5 minutes',interval '30 seconds',true,
                    clock_timestamp()-interval '1 minute');
                """);
            command.Parameters.AddWithValue("target",target); command.Parameters.AddWithValue("name","backup.queue."+(int)kind);
            command.Parameters.AddWithValue("kind",(short)kind); await command.ExecuteNonQueryAsync();
        }
    }
    private static async Task ExpireBackupQueueClaimsAsync(RepositoryTestDatabase database,Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            UPDATE alerting.evaluation_queue SET claimed_until=clock_timestamp()-interval '1 second'
              WHERE instance_id=@target AND completed_at IS NULL;
            """);
        command.Parameters.AddWithValue("target",target); await command.ExecuteNonQueryAsync();
    }
    private static async Task AssertBackupQueueCancellationAsync(RepositoryTestDatabase database,Guid target,string reason,long expected)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND cancel_reason=@reason
              AND completed_at IS NOT NULL AND claimed_until IS NULL AND claim_work_key IS NULL
              AND claim_owner_execution_id IS NULL AND claim_fencing IS NULL;
            """);
        command.Parameters.AddWithValue("target",target); command.Parameters.AddWithValue("reason",reason);
        Assert.Equal(expected,await command.ExecuteScalarAsync());
    }
    private static async Task AssertBackupQueueClaimAsync(RepositoryTestDatabase database,Guid target,AlertObservation observation,WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction))
        { scope.Parameters.AddWithValue("target",target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("SELECT alerting.assert_backup_evaluation_claim(@target,@observation,@key,@owner,@fence);",connection,transaction);
        command.Parameters.AddWithValue("target",target);
        command.Parameters.AddWithValue("observation",NpgsqlDbType.Jsonb,
            BackupDecisionWire(observation,new(new(observation.RuleId,observation.TargetId),null,false,"test"))["Observation"]!.ToJsonString());
        command.Parameters.AddWithValue("key",lease.Key.Value); command.Parameters.AddWithValue("owner",lease.Owner.Value);
        command.Parameters.AddWithValue("fence",lease.FencingToken.Value);
        await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
    }
}
