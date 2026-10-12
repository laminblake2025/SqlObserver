using System.Reflection;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    [Fact]
    public async Task BlockingCommitTransitionsMatchProductionAtDurationBoundariesAcrossStatesGapsAndMaintenance()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); var at=new DateTimeOffset(2026,9,30,12,0,0,TimeSpan.Zero);
        int combinations=0;
        foreach(AlertState state in Enum.GetValues<AlertState>())
        foreach(long? milliseconds in new long?[]{null,0,49999,50000,50001,60000,65001})
        foreach(AlertComparison comparison in new[]{AlertComparison.GreaterThan,AlertComparison.GreaterThanOrEqual})
        foreach(int hysteresis in new[]{0,10})
        foreach(int clears in new[]{1,2})
        foreach(bool gap in new[]{false,true})
        foreach(bool suppressed in new[]{false,true})
        {
            var rule=new AlertRuleDefinition(Guid.NewGuid(),"blocking.duration",AlertRuleKind.BlockingDuration,null,
                comparison,60,hysteresis,2,TimeSpan.FromMinutes(5),TimeSpan.FromSeconds(5),clearConfirmationCount:clears);
            bool active=state is AlertState.Firing or AlertState.Acknowledged,episode=active||state==AlertState.Resolved;
            var prior=new AlertRuleState(rule.RuleId,target,state,state==AlertState.Pending?1:active?2:0,
                firstMatchUtc:active||state==AlertState.Pending?at:null,lastObservedUtc:at,firedUtc:episode?at:null,
                acknowledgedUtc:state==AlertState.Acknowledged?at:null,alertId:episode?Guid.NewGuid():null,
                episodeId:episode?Guid.NewGuid():null,resolvedUtc:state==AlertState.Resolved?at:null,
                episodeStartedUtc:episode?at:null,acknowledgedBy:state==AlertState.Acknowledged?"operator":null,
                revision:7,lastValue:65,consecutiveClears:active?1:0);
            DateTimeOffset now=gap?at.AddMinutes(6):at.AddSeconds(5);
            var evidence=new BlockingAlertEvidence(target,1,now,milliseconds is null?null:now.AddMinutes(1),1,Guid.NewGuid(),
                now.AddSeconds(-1),milliseconds is >0?now.AddSeconds(-1):null,milliseconds is null?null:milliseconds>0?1:0,
                milliseconds,milliseconds is null?BlockingAlertEvidenceReason.SourceIncomplete:BlockingAlertEvidenceReason.Current,
                new string('b',64),new string('a',64));
            AlertObservation observation=BlockingAlertObservationFactory.Create(rule,evidence);
            MaintenanceWindow? maintenance=suppressed?new(Guid.NewGuid(),target,at.AddMinutes(-1),now.AddMinutes(1),"planned"):null;
            JsonObject expected=BlockingDecisionWire(observation,AlertEvaluator.Evaluate(rule,prior,observation,maintenance));
            JsonObject before=BlockingDecisionWire(observation,new(prior,null,false,"prior"));
            await using var command=database.DataSource.CreateCommand("""
                SELECT alerting.blocking_alert_transition(@comparison,60,@hysteresis,2,@clears,interval '5 minutes',
                  @prior,@observation,@suppressed)=alerting.canonical_decision_snapshot(@expected);
                """);
            command.Parameters.AddWithValue("comparison",(short)comparison); command.Parameters.AddWithValue("hysteresis",(double)hysteresis);
            command.Parameters.AddWithValue("clears",clears); command.Parameters.AddWithValue("suppressed",suppressed);
            command.Parameters.AddWithValue("prior",NpgsqlDbType.Jsonb,before["State"]!.ToJsonString());
            command.Parameters.AddWithValue("observation",NpgsqlDbType.Jsonb,expected["Observation"]!.ToJsonString());
            command.Parameters.AddWithValue("expected",NpgsqlDbType.Jsonb,expected.ToJsonString());
            Assert.Equal(true,await command.ExecuteScalarAsync()); combinations++;
        }
        Assert.Equal(1120,combinations);
    }

    [Fact]
    public async Task BlockingCommitPersistsUnknownAcknowledgementRecoveryRefireAndImmutableReplayAtomically()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); await InsertActiveTargetAsync(database,target);
        Guid ruleId=await SeedBlockingQueueRuleAsync(database,target);
        await ExecuteAsync(database,"UPDATE alerting.rule SET confirmation_count=1 WHERE rule_id=@rule;",("rule",ruleId));
        await SeedBlockingCommitDestinationAsync(database,target);
        await ExecuteAsync(database,"""
            INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason)
              VALUES(gen_random_uuid(),@target,clock_timestamp()-interval '1 minute',clock_timestamp()+interval '10 minutes','planned');
            """,("target",target.Value));
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector); var repository=new PostgreSqlAlertRepositoryPort(collector);
        AlertRuleDefinition rule=Assert.Single(await repository.ListRulesAsync(target,Timeout,CancellationToken.None));
        AlertRuleState prior=new(rule.RuleId,target); JsonObject? first=null; Guid? originalAlert=null; Guid? originalEpisode=null;
        async Task<AlertEvaluationResult> Next(long? milliseconds)
        {
            if(prior.LastObservedUtc is { } previous)
            {
                TimeSpan remaining=previous.AddSeconds(5.05)-(await ReadRepositoryClockAsync(database));
                if(remaining>TimeSpan.Zero) await Task.Delay(remaining);
            }
            DateTimeOffset now=await ReadRepositoryClockAsync(database); Guid run=Guid.NewGuid();
            await SeedTargetAndRunAsync(database,target,run,"blocking.current",now,milliseconds is >0?1:0,
                outcome:milliseconds is null?"permission_denied":"succeeded");
            await EnsureBlockingPartitionAsync(database,now);
            if(milliseconds is >0) await InsertBlockingAlertEdgeAsync(database,target,run,now,71,milliseconds.Value);
            Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
            JsonObject queued=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
            AlertObservation observation=BlockingObservationFromWire(queued);
            var maintenance=new MaintenanceWindow(Guid.NewGuid(),target,now.AddMinutes(-1),now.AddMinutes(1),"planned");
            AlertEvaluationResult result=AlertEvaluator.Evaluate(rule,prior,observation,maintenance);
            JsonObject decision=BlockingDecisionWire(observation,result); Assert.Equal(1,await CommitBlockingAsync(database,target,decision,lease));
            first??=(JsonObject)decision.DeepClone();
            prior=Assert.IsType<AlertRuleState>(await repository.GetStateAsync(target,ruleId,Timeout,CancellationToken.None));
            Assert.Equal(result.State.State,prior.State); Assert.Equal(result.State.AlertId,prior.AlertId);
            Assert.Equal(result.State.ConsecutiveClears,prior.ConsecutiveClears); Assert.True(prior.DeliverySuppressed);
            return result;
        }
        AlertEvaluationResult fired=await Next(65001); Assert.Equal(AlertEventKind.Fired,fired.Event);
        originalAlert=prior.AlertId; originalEpisode=prior.EpisodeId;
        await using (NpgsqlConnection connection=await collector.OpenConnectionAsync())
        {
            // The production acknowledgement function is Server-only.
            await using var denied=new NpgsqlCommand("SELECT * FROM alerting.acknowledge(NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);",connection);
            Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(async()=>await denied.ExecuteNonQueryAsync())).SqlState);
        }
        await using (NpgsqlDataSource server=database.CreateServerDataSource())
        await using (NpgsqlConnection connection=await server.OpenConnectionAsync())
        {
            await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
            await using(var scope=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction))
            {scope.Parameters.AddWithValue("target",target.Value.ToString("D")); await scope.ExecuteNonQueryAsync();}
            await using var acknowledge=new NpgsqlCommand("SELECT * FROM alerting.acknowledge(@alert,@target,@operation,'S-1-5-21-1-2-3-1234',@correlation,@revision,@episode,NULL);",connection,transaction);
            acknowledge.Parameters.AddWithValue("alert",prior.AlertId!.Value); acknowledge.Parameters.AddWithValue("target",target.Value);
            acknowledge.Parameters.AddWithValue("operation",Guid.NewGuid().ToString("D")); acknowledge.Parameters.AddWithValue("correlation",Guid.NewGuid());
            acknowledge.Parameters.AddWithValue("revision",prior.Revision); acknowledge.Parameters.AddWithValue("episode",prior.EpisodeId!.Value);
            await acknowledge.ExecuteNonQueryAsync(); await transaction.CommitAsync();
        }
        prior=Assert.IsType<AlertRuleState>(await repository.GetStateAsync(target,ruleId,Timeout,CancellationToken.None));
        Assert.Equal(AlertState.Acknowledged,prior.State);
        Assert.Null((await Next(null)).Event); Assert.Equal(AlertState.Acknowledged,prior.State);
        Assert.Equal(originalAlert,prior.AlertId); Assert.Equal(originalEpisode,prior.EpisodeId); Assert.NotNull(prior.AcknowledgedUtc);
        Assert.Null((await Next(0)).Event); Assert.Equal(1,prior.ConsecutiveClears);
        Assert.Null((await Next(null)).Event); Assert.Equal(0,prior.ConsecutiveClears);
        Assert.Null((await Next(0)).Event); Assert.Equal(1,prior.ConsecutiveClears);
        Assert.Equal(AlertEventKind.Resolved,(await Next(0)).Event); Assert.Equal(AlertState.Resolved,prior.State);
        Assert.Equal(AlertEventKind.Fired,(await Next(65001)).Event); Assert.NotEqual(originalAlert,prior.AlertId);
        Assert.NotEqual(originalEpisode,prior.EpisodeId); Assert.Null(prior.AcknowledgedUtc);
        string snapshot=await BlockingCommitSnapshotAsync(database,target);
        Assert.Equal(0,await CommitBlockingAsync(database,target,first!,lease));
        Assert.Equal(snapshot,await BlockingCommitSnapshotAsync(database,target));
        JsonObject forged=(JsonObject)first!.DeepClone(); forged["Reason"]="forged replay";
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(()=>CommitBlockingAsync(database,target,forged,lease))).SqlState);
        Assert.Equal(snapshot,await BlockingCommitSnapshotAsync(database,target));
        await using(var counts=database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM alerting.evaluation_replay WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.state_history WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target AND due_at>clock_timestamp()),
              (SELECT count(*) FROM alerting.blocking_evaluation_capture WHERE instance_id=@target),
              (SELECT count(*) FROM audit.activity WHERE safe_details->>'source_kind'='blocking_duration_v1');
            """))
        {
            counts.Parameters.AddWithValue("target",target.Value); await using NpgsqlDataReader row=await counts.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(7L,row.GetInt64(0)); Assert.Equal(8L,row.GetInt64(1)); Assert.Equal(3L,row.GetInt64(2));
            // Blocking fire/resolve intents wait for maintenance to end. The
            // existing acknowledgement function omits its notification during
            // maintenance; it still persists the acknowledgement/history/audit.
            Assert.Equal(3L,row.GetInt64(3)); Assert.Equal(0L,row.GetInt64(4)); Assert.Equal(7L,row.GetInt64(5));
        }
        WorkerLeaseIdentity delivery=Assert.IsType<WorkerLease>((await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new(new("alerts/blocking/commit-delivery"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(1)),Timeout),CancellationToken.None)).Lease).Identity;
        Assert.Empty(await repository.ClaimDueDeliveriesAsync(delivery,100,Timeout,CancellationToken.None));
        await ExecuteAsync(database,"UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;",("target",target.Value));
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(()=>CommitBlockingAsync(database,target,first!,lease))).SqlState);
    }

    [Fact]
    public async Task BlockingCommitRejectsForgedDecisionsAndRollsBackStateOutboxHistoryReplayAndAuditTogether()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); Guid run=Guid.NewGuid(); DateTimeOffset at=await ReadRepositoryClockAsync(database);
        await SeedTargetAndRunAsync(database,target,run,"blocking.current",at,1);
        await EnsureBlockingPartitionAsync(database,at); await InsertBlockingAlertEdgeAsync(database,target,run,at,71,65001);
        Guid ruleId=await SeedBlockingQueueRuleAsync(database,target);
        await ExecuteAsync(database,"UPDATE alerting.rule SET confirmation_count=1 WHERE rule_id=@rule;",("rule",ruleId));
        await SeedBlockingCommitDestinationAsync(database,target);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector); var repository=new PostgreSqlAlertRepositoryPort(collector);
        AlertRuleDefinition rule=Assert.Single(await repository.ListRulesAsync(target,Timeout,CancellationToken.None));
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        AlertObservation observation=BlockingObservationFromWire(Assert.Single(await ClaimBlockingQueueAsync(database,target,lease)));
        JsonObject decision=BlockingDecisionWire(observation,AlertEvaluator.Evaluate(rule,new(rule.RuleId,target),observation));
        string before=await BlockingCommitSnapshotAsync(database,target);
        foreach(string field in new[]{"State","ConsecutiveMatches","AlertId","LastValue","ResolvedUtc"})
        {
            JsonObject forged=(JsonObject)decision.DeepClone();
            forged["State"]![field]=field switch
            {
                "State"=>1,"ConsecutiveMatches"=>99,"AlertId"=>JsonValue.Create(Guid.NewGuid()),
                "LastValue"=>0d,_=>JsonValue.Create(at),
            };
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>CommitBlockingAsync(database,target,forged,lease))).SqlState);
            Assert.Equal(before,await BlockingCommitSnapshotAsync(database,target));
        }
        foreach(string field in new[]{"Event","DeliverySuppressed","Reason"})
        {
            JsonObject forged=(JsonObject)decision.DeepClone();
            forged[field]=field switch {"Event"=>2,"DeliverySuppressed"=>true,_=>"forged"};
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>CommitBlockingAsync(database,target,forged,lease))).SqlState);
            Assert.Equal(before,await BlockingCommitSnapshotAsync(database,target));
        }
        // Inject a transaction-owned audit failure after state/outbox/replay
        // writes. The queue claim and every durable effect must remain intact.
        await ExecuteAsync(database,"""
            CREATE FUNCTION public.reject_blocking_commit_audit() RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN IF NEW.safe_details->>'source_kind'='blocking_duration_v1' THEN RAISE EXCEPTION 'injected audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_blocking_commit_audit BEFORE INSERT ON audit.activity
              FOR EACH ROW EXECUTE FUNCTION public.reject_blocking_commit_audit();
            """);
        Assert.Equal("P0001",(await Assert.ThrowsAsync<PostgresException>(()=>CommitBlockingAsync(database,target,decision,lease))).SqlState);
        Assert.Equal(before,await BlockingCommitSnapshotAsync(database,target));
        await ExecuteAsync(database,"DROP TRIGGER reject_blocking_commit_audit ON audit.activity; DROP FUNCTION public.reject_blocking_commit_audit();");
        Assert.Equal(1,await CommitBlockingAsync(database,target,decision,lease));
        Assert.Equal(0,await CommitBlockingAsync(database,target,decision,lease));
        await using var privileges=database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner),has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='alerting.commit_blocking_alert_evaluation(uuid,jsonb,jsonb,text,uuid,bigint)'::regprocedure;
            """);
        await using NpgsqlDataReader row=await privileges.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal("sqlobserver_migrator",row.GetString(0));
        for(int column=1;column<=4;column++) Assert.False(row.GetBoolean(column));
    }

    private static AlertObservation BlockingObservationFromWire(JsonObject observation)=>new(
        new(observation["TargetId"]!.GetValue<Guid>()),observation["RuleId"]!.GetValue<Guid>(),observation["ObservedAtUtc"]!.GetValue<DateTimeOffset>(),
        observation["Value"]?.GetValue<double>(),observation["CollectorHealthy"]?.GetValue<bool>(),observation["Reason"]?.GetValue<string>(),
        observation["OperationId"]!.GetValue<Guid>(),observation["EvidenceDigest"]!.GetValue<string>(),observation["SampleId"]?.GetValue<string>(),
        observation["RunId"]?.GetValue<Guid>(),observation["SourceKind"]?.GetValue<string>(),observation["MetricId"]?.GetValue<string>(),
        observation["SourceCollector"]?.GetValue<string>(),observation["SourceVersion"]?.GetValue<string>(),
        observation["SourceSchemaVersion"]?.GetValue<int>(),observation["SourceDigest"]?.GetValue<string>());
    private static JsonObject BlockingDecisionWire(AlertObservation observation,AlertEvaluationResult result)
    {
        var decision=new AlertEvaluationDecision(observation,result.State,result.Event,result.DeliverySuppressed,result.Reason);
        MethodInfo serialize=typeof(PostgreSqlAlertRepositoryPort).GetMethod("SerializeDecisions",BindingFlags.NonPublic|BindingFlags.Static)!;
        return JsonNode.Parse((string)serialize.Invoke(null,[new[]{decision}])!)![0]!.AsObject();
    }
    private static async Task<int> CommitBlockingAsync(RepositoryTestDatabase database,MonitoredInstanceId target,JsonObject decision,WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection=await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction=await ConfigureBlockingQueueTransactionAsync(connection,target);
        await using var command=new NpgsqlCommand("SELECT evaluated_count FROM alerting.commit_blocking_alert_evaluation(@target,@observation,@decision,@key,@owner,@fence);",connection,transaction);
        AddBlockingQueueParameters(command,target,lease);
        command.Parameters.AddWithValue("observation",NpgsqlDbType.Jsonb,decision["Observation"]!.ToJsonString());
        command.Parameters.AddWithValue("decision",NpgsqlDbType.Jsonb,decision.ToJsonString());
        int result=(int)(await command.ExecuteScalarAsync()??throw new InvalidDataException()); await transaction.CommitAsync(); return result;
    }
    private static Task SeedBlockingCommitDestinationAsync(RepositoryTestDatabase database,MonitoredInstanceId target)=>ExecuteAsync(database,"""
        INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,revision,approved,
          approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope,configuration_digest)
        VALUES(gen_random_uuid(),@target,'https-webhook','integration',true,1,true,'https-webhook','integration',1,
          repeat('c',64),@target,repeat('d',64));
        """,("target",target.Value));
    private static async Task<string> BlockingCommitSnapshotAsync(RepositoryTestDatabase database,MonitoredInstanceId target)
    {
        await using var command=database.DataSource.CreateCommand("""
            SELECT jsonb_build_object('state',(SELECT jsonb_agg(to_jsonb(s)) FROM alerting.rule_state s WHERE s.instance_id=@target),
              'history',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.observed_at,s.operation_id) FROM alerting.state_history s WHERE s.instance_id=@target),
              'replay',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.operation_id) FROM alerting.evaluation_replay s WHERE s.instance_id=@target),
              'outbox',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.delivery_id) FROM alerting.delivery_outbox s WHERE s.instance_id=@target),
              'queue',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.operation_id) FROM alerting.evaluation_queue s WHERE s.instance_id=@target),
              'captures',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.operation_id) FROM alerting.blocking_evaluation_capture s WHERE s.instance_id=@target),
              'audit',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.activity_id) FROM audit.activity s WHERE s.safe_details->>'source_kind'='blocking_duration_v1'))::text;
            """);
        command.Parameters.AddWithValue("target",target.Value); return (string)(await command.ExecuteScalarAsync()??throw new InvalidDataException());
    }
}
