using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    [Fact]
    public async Task BlockingQueueKeepsDurableIdentityAndConsumesEachCompleteSnapshotOnce()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); Guid run=Guid.NewGuid();
        DateTimeOffset at=await ReadRepositoryClockAsync(database);
        await SeedTargetAndRunAsync(database,target,run,"blocking.current",at,1);
        await EnsureBlockingPartitionAsync(database,at);
        await InsertBlockingAlertEdgeAsync(database,target,run,at,71,65001);
        Guid rule=await SeedBlockingQueueRuleAsync(database,target);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject observation=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Equal(65.001,observation["Value"]!.GetValue<double>());
        Assert.Equal("blocking_duration_v1",observation["SourceKind"]!.GetValue<string>());
        var typed=new AlertObservation(target,rule,observation["ObservedAtUtc"]!.GetValue<DateTimeOffset>(),
            observation["Value"]!.GetValue<double>(),null,observation["Reason"]!.GetValue<string>(),
            observation["OperationId"]!.GetValue<Guid>(),sampleId:observation["SampleId"]!.GetValue<string>(),runId:run,
            sourceKind:"blocking_duration_v1",sourceCollector:"blocking.current",sourceVersion:"1",sourceSchemaVersion:1,
            sourceDigest:observation["SourceDigest"]!.GetValue<string>());
        Assert.Equal(typed.OperationId,AlertOperationIdentity.Compute(target,rule,typed.ObservedAtUtc,typed.Reason));
        Assert.Equal(typed.EvidenceDigest,observation["EvidenceDigest"]!.GetValue<string>());
        await AssertBlockingQueueClaimAsync(database,target,observation,lease);
        await using (NpgsqlConnection connection=await collector.OpenConnectionAsync())
        {
            await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
            await using(var scope=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction))
            {scope.Parameters.AddWithValue("target",target.Value.ToString("D")); await scope.ExecuteNonQueryAsync();}
            await using var legacy=new NpgsqlCommand("SELECT * FROM alerting.evaluate_and_enqueue(@target,@observations,@decisions,@key,@owner,@fence);",connection,transaction);
            AddBlockingQueueParameters(legacy,target,lease);
            legacy.Parameters.AddWithValue("observations",NpgsqlDbType.Jsonb,new JsonArray(observation.DeepClone()).ToJsonString());
            legacy.Parameters.AddWithValue("decisions",NpgsqlDbType.Jsonb,new JsonArray(new JsonObject{["Observation"]=observation.DeepClone()}).ToJsonString());
            PostgresException rejected=await Assert.ThrowsAsync<PostgresException>(async()=>await legacy.ExecuteNonQueryAsync());
            Assert.Equal("22023",rejected.SqlState);
            Assert.Equal("Blocking alert decision shape is invalid",rejected.MessageText);
        }
        Assert.Empty(await ClaimBlockingQueueAsync(database,target,lease));
        await ExpireBlockingQueueClaimsAsync(database,target);
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(()=>
            AssertBlockingQueueClaimAsync(database,target,observation,lease))).SqlState);
        JsonObject reclaimed=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.True(JsonNode.DeepEquals(observation,reclaimed));
        await AssertBlockingQueueClaimAsync(database,target,reclaimed,lease);

        // Only simulate successful completion in this queue test. State, replay,
        // history and outbox are deliberately not implemented/claimed by 0298.
        await ExecuteAsync(database,"""
            UPDATE alerting.evaluation_queue SET completed_at=clock_timestamp(),claimed_until=NULL,
              claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL
            WHERE instance_id=@target AND rule_id=@rule;
            """,("target",target.Value),("rule",rule));
        // Advance beyond the real five-second cadence; otherwise an empty queue
        // alone would not prove unchanged-source suppression.
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        Assert.Empty(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Equal(0L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.blocking_evaluation_capture WHERE instance_id=@target;"));
        at=await ReadRepositoryClockAsync(database); Guid next=Guid.NewGuid();
        await SeedTargetAndRunAsync(database,target,next,"blocking.current",at,0);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject clear=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Equal(0d,clear["Value"]!.GetValue<double>());
        Assert.NotEqual(observation["SourceDigest"]!.GetValue<string>(),clear["SourceDigest"]!.GetValue<string>());
        Assert.NotEqual(observation["OperationId"]!.GetValue<Guid>(),clear["OperationId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task BlockingQueueSupersedesChangedEvidenceWithoutStealingClaimsAndRechecksRevisions()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); await InsertActiveTargetAsync(database,target);
        Guid rule=await SeedBlockingQueueRuleAsync(database,target);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject missing=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Null(missing["Value"]); Assert.Null(missing["RunId"]);
        DateTimeOffset at=await ReadRepositoryClockAsync(database); Guid run=Guid.NewGuid();
        await SeedTargetAndRunAsync(database,target,run,"blocking.current",at,1);
        await EnsureBlockingPartitionAsync(database,at); await InsertBlockingAlertEdgeAsync(database,target,run,at,71,65000);
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        Assert.Equal(1L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND completed_at IS NULL AND claimed_until>clock_timestamp();"));
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(()=>
            AssertBlockingQueueClaimAsync(database,target,missing,lease))).SqlState);
        await ExpireBlockingQueueClaimsAsync(database,target);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject current=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Equal(65d,current["Value"]!.GetValue<double>());
        Assert.Equal(1L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND cancel_reason='blocking_source_superseded' AND completed_at IS NOT NULL;"));
        foreach (string field in new[]{"Value","SourceDigest","RunId","RuleId","EvidenceDigest"})
        {
            JsonObject forged=(JsonObject)current.DeepClone();
            forged[field]=field switch {"Value"=>66d,"RunId" or "RuleId"=>JsonValue.Create(Guid.NewGuid()),_=>new string('a',64)};
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>
                AssertBlockingQueueClaimAsync(database,target,forged,lease))).SqlState);
        }
        JsonObject extra=(JsonObject)current.DeepClone(); extra["Unexpected"]=true;
        Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>AssertBlockingQueueClaimAsync(database,target,extra,lease))).SqlState);
        await BumpScheduleRevisionAsync(database,target,"blocking.current");
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(()=>AssertBlockingQueueClaimAsync(database,target,current,lease))).SqlState);
        await ExpireBlockingQueueClaimsAsync(database,target);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject reconfigured=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Null(reconfigured["Value"]);
        await ExecuteAsync(database,"UPDATE alerting.rule SET revision=revision+1 WHERE rule_id=@rule;",("rule",rule));
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(()=>AssertBlockingQueueClaimAsync(database,target,reconfigured,lease))).SqlState);
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        await ExpireBlockingQueueClaimsAsync(database,target);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject revised=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        await ExecuteAsync(database,"UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;",("target",target.Value));
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>AssertBlockingQueueClaimAsync(database,target,revised,lease))).SqlState);
        await ExpireBlockingQueueClaimsAsync(database,target);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject targetChanged=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Contains("|target=2|",targetChanged["Reason"]!.GetValue<string>());
        await ExecuteAsync(database,"UPDATE alerting.rule SET enabled=false WHERE rule_id=@rule;",("rule",rule));
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        await ExpireBlockingQueueClaimsAsync(database,target);
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        Assert.Empty(await ClaimBlockingQueueAsync(database,target,lease));
        Assert.Equal(1L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND cancel_reason='rule_disabled';"));
    }

    [Fact]
    public async Task BlockingQueueExpiresUnknownCapturesAndRetiresOnlyAvailableInactiveTargetWork()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); await InsertActiveTargetAsync(database,target);
        await SeedBlockingQueueRuleAsync(database,target);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        // Seed a canonical three-minute-old unknown capture without changing the
        // repository clock. All queue/capture identities and digests are rebuilt.
        await using (NpgsqlConnection connection=await database.DataSource.OpenConnectionAsync())
        {
            await using NpgsqlTransaction transaction=await ConfigureBlockingQueueTransactionAsync(connection,target);
            await using var age=new NpgsqlCommand("""
                RESET ROLE;
                CREATE TEMP TABLE blocking_aged_capture ON COMMIT DROP AS
                  SELECT c.*,c.read_at-interval '3 minutes' AS aged_at,
                    alerting.blocking_alert_observation(c.instance_id,c.target_revision,c.rule_id,
                      c.read_at-interval '3 minutes',c.payload,c.source_digest) AS aged_observation
                  FROM alerting.blocking_evaluation_capture c WHERE c.instance_id=@target;
                DELETE FROM alerting.blocking_evaluation_capture c USING blocking_aged_capture a WHERE c.operation_id=a.operation_id;
                UPDATE alerting.evaluation_queue q SET operation_id=(a.aged_observation->>'OperationId')::uuid,
                  observed_at=a.aged_at,observations=jsonb_build_array(a.aged_observation),due_at=a.aged_at,
                  evidence_digest=decode(a.aged_observation->>'EvidenceDigest','hex'),sample_id=a.aged_observation->>'SampleId'
                FROM blocking_aged_capture a WHERE q.operation_id=a.operation_id;
                INSERT INTO alerting.blocking_evaluation_capture(operation_id,instance_id,rule_id,target_revision,rule_revision,read_at,payload,source_digest)
                  SELECT (aged_observation->>'OperationId')::uuid,instance_id,rule_id,target_revision,rule_revision,aged_at,payload,source_digest
                  FROM blocking_aged_capture;
                """,connection,transaction);
            age.Parameters.AddWithValue("target",target.Value); await age.ExecuteNonQueryAsync(); await transaction.CommitAsync();
        }
        PostgresException expired=await Assert.ThrowsAsync<PostgresException>(()=>ClaimBlockingQueueAsync(database,target,lease));
        Assert.Equal("40001",expired.SqlState); Assert.Equal("Blocking captured source changed or expired",expired.MessageText);
        Assert.Equal(1,await ReconcileBlockingQueueAsync(database,target,lease));
        JsonObject refreshed=Assert.Single(await ClaimBlockingQueueAsync(database,target,lease)); Assert.Null(refreshed["Value"]);
        Assert.Equal(1L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND cancel_reason='blocking_capture_expired';"));
        await ExecuteAsync(database,"UPDATE control.observation_target SET lifecycle_state='disabled',revision=revision+1 WHERE instance_id=@target;",("target",target.Value));
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        Assert.Equal(1L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND completed_at IS NULL;"));
        await ExpireBlockingQueueClaimsAsync(database,target);
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        Assert.Equal(1L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND cancel_reason='target_not_active' AND claimed_until IS NULL AND claim_owner_execution_id IS NULL;"));
        Assert.Equal(0L,await BlockingQueueCountAsync(database,target,"SELECT count(*) FROM alerting.blocking_evaluation_capture WHERE instance_id=@target;"));
        await ExecuteAsync(database,"""
            UPDATE control.worker_lease SET acquired_at=statement_timestamp()-interval '3 minutes',
              renewed_at=statement_timestamp()-interval '2 minutes',expires_at=statement_timestamp()-interval '1 minute'
            WHERE work_key='alerts/evaluation/reconcile';
            """);
        Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(()=>ReconcileBlockingQueueAsync(database,target,lease))).SqlState);
    }

    [Fact]
    public async Task BlockingQueueBoundsConcurrentProducersAndRejectsOutOfContractAdministration()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); await InsertActiveTargetAsync(database,target);
        await SeedBlockingQueueRuleAsync(database,target); await SeedBlockingQueueRuleAsync(database,target);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector);
        int[] produced=await Task.WhenAll(ReconcileBlockingQueueAsync(database,target,lease,1),ReconcileBlockingQueueAsync(database,target,lease,1));
        Assert.All(produced,count=>Assert.Equal(1,count));
        Assert.Equal(0,await ReconcileBlockingQueueAsync(database,target,lease));
        Assert.Single(await ClaimBlockingQueueAsync(database,target,lease,1));
        Assert.Single(await ClaimBlockingQueueAsync(database,target,lease,1));
        Assert.Empty(await ClaimBlockingQueueAsync(database,target,lease));
        var wrong=new WorkerLeaseIdentity(lease.Key,new(Guid.NewGuid()),lease.FencingToken);
        Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(()=>ReconcileBlockingQueueAsync(database,target,wrong))).SqlState);
        foreach (int limit in new[]{0,101})
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>ReconcileBlockingQueueAsync(database,target,lease,limit))).SqlState);
        await using (var inspect=database.DataSource.CreateCommand("""
            SELECT c.relrowsecurity,c.relforcerowsecurity,pg_get_userbyid(c.relowner),
              has_table_privilege('sqlobserver_collector',c.oid,'SELECT'),
              (SELECT contract_version FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts'),
              (SELECT count(*) FROM pg_proc p WHERE p.pronamespace='alerting'::regnamespace
                AND p.proname IN ('blocking_alert_observation','reconcile_blocking_alert_evaluations',
                    'assert_blocking_evaluation_claim','claim_blocking_alert_evaluations')
                AND pg_get_userbyid(p.proowner)='sqlobserver_migrator'
                AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
                AND NOT EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE'))
            FROM pg_class c WHERE c.oid='alerting.blocking_evaluation_capture'::regclass;
            """))
        {
            await using NpgsqlDataReader row=await inspect.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0)); Assert.True(row.GetBoolean(1)); Assert.Equal("sqlobserver_migrator",row.GetString(2));
            Assert.False(row.GetBoolean(3)); Assert.Equal(4,row.GetInt32(4)); Assert.Equal(4L,row.GetInt64(5));
        }
        await using var denied=collector.CreateCommand("SELECT alerting.reconcile_blocking_alert_evaluations(@target,@key,@owner,@fence,1);");
        AddBlockingQueueParameters(denied,target,lease,1);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(async()=>await denied.ExecuteScalarAsync())).SqlState);
        await using (NpgsqlConnection connection=await database.DataSource.OpenConnectionAsync())
        {
            await using NpgsqlTransaction transaction=await ConfigureBlockingQueueTransactionAsync(connection,target);
            await using var catalog=new NpgsqlCommand("""
                SELECT * FROM alerting.upsert_rule(jsonb_build_object('RuleId',gen_random_uuid(),'TargetId',@target,
                  'Action','CreateAlertRule','Enabled',true,'Name','blocking.duration','Kind',6,'MetricId',NULL,
                  'SourceCollector','blocking.current','SourceSchemaVersion',1,'Comparison',1,'Threshold',86401,
                  'Hysteresis',10,'ConfirmationCount',2,'ClearConfirmationCount',2,'ConfirmationWindow','5 minutes',
                  'EvaluationInterval','5 seconds','CatalogDigest',
                    (SELECT catalog_digest FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts')),
                  gen_random_uuid()::text,'S-1-5-21-1-2-3-1234',gen_random_uuid());
                """,connection,transaction);
            catalog.Parameters.AddWithValue("target",target.Value);
            PostgresException rejected=await Assert.ThrowsAsync<PostgresException>(async()=>await catalog.ExecuteNonQueryAsync());
            Assert.Equal("22023",rejected.SqlState); Assert.Equal("alert rule is outside the approved catalog contract",rejected.MessageText);
        }
    }

    private static async Task<Guid> SeedBlockingQueueRuleAsync(RepositoryTestDatabase database,MonitoredInstanceId target)
    {
        Guid rule=Guid.NewGuid(); await ExecuteAsync(database,"""
            INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,
              confirmation_count,clear_confirmation_count,confirmation_window,evaluation_interval,enabled,updated_at)
            VALUES(@rule,@target,@name,6,NULL,1,60,10,2,2,interval '5 minutes',interval '5 seconds',true,clock_timestamp()-interval '1 minute');
            """,("rule",rule),("target",target.Value),("name","blocking.queue."+rule.ToString("N")));
        return rule;
    }
    private static async Task<NpgsqlTransaction> ConfigureBlockingQueueTransactionAsync(NpgsqlConnection connection,MonitoredInstanceId target)
    {
        NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
        await using var scope=new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator; SET LOCAL TIME ZONE 'UTC'; SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction);
        scope.Parameters.AddWithValue("target",target.Value.ToString("D")); await scope.ExecuteNonQueryAsync(); return transaction;
    }
    private static void AddBlockingQueueParameters(NpgsqlCommand command,MonitoredInstanceId target,WorkerLeaseIdentity lease,int? limit=null)
    {
        command.Parameters.AddWithValue("target",target.Value); command.Parameters.AddWithValue("key",lease.Key.Value);
        command.Parameters.AddWithValue("owner",lease.Owner.Value); command.Parameters.AddWithValue("fence",lease.FencingToken.Value);
        if (limit is not null) command.Parameters.AddWithValue("limit",limit.Value);
    }
    private static async Task<int> ReconcileBlockingQueueAsync(RepositoryTestDatabase database,MonitoredInstanceId target,WorkerLeaseIdentity lease,int limit=100)
    {
        await using NpgsqlConnection connection=await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction=await ConfigureBlockingQueueTransactionAsync(connection,target);
        await using var command=new NpgsqlCommand("SELECT alerting.reconcile_blocking_alert_evaluations(@target,@key,@owner,@fence,@limit);",connection,transaction);
        AddBlockingQueueParameters(command,target,lease,limit);
        int result=(int)(await command.ExecuteScalarAsync()??throw new InvalidDataException()); await transaction.CommitAsync(); return result;
    }
    private static async Task<List<JsonObject>> ClaimBlockingQueueAsync(RepositoryTestDatabase database,MonitoredInstanceId target,WorkerLeaseIdentity lease,int limit=100)
    {
        await using NpgsqlConnection connection=await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction=await ConfigureBlockingQueueTransactionAsync(connection,target);
        await using var command=new NpgsqlCommand("SELECT observations FROM alerting.claim_blocking_alert_evaluations(@target,@key,@owner,@fence,@limit);",connection,transaction);
        AddBlockingQueueParameters(command,target,lease,limit); var result=new List<JsonObject>();
        await using (NpgsqlDataReader rows=await command.ExecuteReaderAsync())
            while(await rows.ReadAsync()) result.Add((JsonObject)JsonNode.Parse(rows.GetString(0))![0]!.DeepClone());
        await transaction.CommitAsync(); return result;
    }
    private static async Task AssertBlockingQueueClaimAsync(RepositoryTestDatabase database,MonitoredInstanceId target,JsonObject observation,WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection=await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction=await ConfigureBlockingQueueTransactionAsync(connection,target);
        await using var command=new NpgsqlCommand("SELECT alerting.assert_blocking_evaluation_claim(@target,@observation,@key,@owner,@fence);",connection,transaction);
        AddBlockingQueueParameters(command,target,lease);
        command.Parameters.AddWithValue("observation",NpgsqlDbType.Jsonb,observation.ToJsonString());
        await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
    }
    private static Task ExpireBlockingQueueClaimsAsync(RepositoryTestDatabase database,MonitoredInstanceId target)=>ExecuteAsync(database,
        "UPDATE alerting.evaluation_queue SET claimed_until=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND completed_at IS NULL;",("target",target.Value));
    private static async Task<long> BlockingQueueCountAsync(RepositoryTestDatabase database,MonitoredInstanceId target,string sql)
    {
        await using var command=database.DataSource.CreateCommand(sql); command.Parameters.AddWithValue("target",target.Value);
        return (long)(await command.ExecuteScalarAsync()??throw new InvalidDataException());
    }
}
