using Npgsql;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    private const string AssignmentOwnerA = "S-1-5-21-8101";
    private const string AssignmentOwnerB = "S-1-5-21-8102";
    private static readonly int[] AssignmentRaceStatuses = [200,409];

    [Fact]
    public async Task AssignmentAssignReassignAndUnassignKeepAlertAndSnoozeEvidenceIntact()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,rule,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        await server.SetSnoozeAsync(SnoozeRequest(target,alert,episode,1,TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1))),CancellationToken.None);
        AlertAssignmentWriteReceipt assigned=await server.SetAssignmentAsync(AssignmentRequest(target,alert,episode,2,AssignmentOwnerA,"Responder A"),CancellationToken.None);
        Assert.Equal(3,assigned.Revision); Assert.Equal(AssignmentOwnerA,assigned.AssigneeSid);
        AlertAssignmentStatus current=(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!;
        Assert.Equal(AssignmentOwnerA,current.AssigneeSid); Assert.Equal("Responder A",current.AssigneeName);
        Assert.Equal(AlertState.Firing,current.State); Assert.Equal("S-1-5-18",current.AssignedBy);
        await server.SetAssignmentAsync(AssignmentRequest(target,alert,episode,3,AssignmentOwnerB,"Responder B"),CancellationToken.None);
        await server.SetAssignmentAsync(AssignmentRequest(target,alert,episode,4,null,null),CancellationToken.None);
        current=(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!;
        Assert.Equal(5,current.Revision); Assert.Null(current.AssigneeSid); Assert.Null(current.AssigneeName);
        Assert.Null(current.AssignmentReason); Assert.Null(current.AssignedBy); Assert.Null(current.AssignedAtUtc);
        Assert.True((await server.GetSnoozeAsync(target,alert,Timeout(),CancellationToken.None))!.Snoozed);
        await using var verify=database.DataSource.CreateCommand("SELECT state,delivery_suppressed,evidence_digest IS NULL,acknowledged_at IS NULL,last_operation_id IS NULL FROM alerting.rule_state WHERE instance_id=@target AND rule_id=@rule; SELECT count(*),bool_and(subject_kind='alerting' AND subject_identifier=@target::text AND safe_details ? 'operationId' AND safe_details->>'reason'='Synthetic assignment triage') FROM audit.activity WHERE action_name IN('alert.assign','alert.unassign');");
        verify.Parameters.AddWithValue("target",target.Value); verify.Parameters.AddWithValue("rule",rule);
        await using NpgsqlDataReader rows=await verify.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync()); Assert.Equal(3,rows.GetInt16(0)); Assert.False(rows.GetBoolean(1));
        for(int i=2;i<5;i++) Assert.True(rows.GetBoolean(i));
        Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(3L,rows.GetInt64(0)); Assert.True(rows.GetBoolean(1));
        AlertAssignmentHistoryPage history=await server.ListAssignmentHistoryAsync(target,alert,50,null,Timeout(),CancellationToken.None);
        Assert.Equal(3,history.Items.Count); Assert.Null(history.NextBeforeOperationId);
        Assert.Null(history.Items[0].AssigneeSid); Assert.Equal(AssignmentOwnerB,history.Items[0].PreviousAssigneeSid);
        Assert.Equal(AssignmentOwnerB,history.Items[1].AssigneeSid); Assert.Equal(AssignmentOwnerA,history.Items[1].PreviousAssigneeSid);
        Assert.Equal("Responder A",history.Items[1].PreviousAssigneeName);
    }

    [Fact]
    public async Task AssignmentReplaySurvivesRosterRevocationRenameLaterUnassignAndEpisodeChange()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,_,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        AlertAssignmentWriteRequest first=AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,"Original responder");
        AlertAssignmentWriteReceipt receipt=await server.SetAssignmentAsync(first,CancellationToken.None);
        AssertAssignmentReceiptEqual(receipt,await server.SetAssignmentAsync(first with { AssigneeEligible=false,ApprovedAssigneeName=null },CancellationToken.None));
        AssertAssignmentReceiptEqual(receipt,await server.SetAssignmentAsync(first with { ApprovedAssigneeName="Renamed responder" },CancellationToken.None));
        Assert.Equal("Original responder",(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!.AssigneeName);
        await server.SetAssignmentAsync(AssignmentRequest(target,alert,episode,2,null,null),CancellationToken.None);
        await using (var change=database.DataSource.CreateCommand("UPDATE alerting.rule_state SET alert_episode_id=@episode,revision=revision+1 WHERE instance_id=@target;"))
        { change.Parameters.AddWithValue("episode",Guid.NewGuid()); change.Parameters.AddWithValue("target",target.Value); Assert.Equal(1,await change.ExecuteNonQueryAsync()); }
        AssertAssignmentReceiptEqual(receipt,await server.SetAssignmentAsync(first with { AssigneeEligible=false,ApprovedAssigneeName=null },CancellationToken.None));
        Assert.Null((await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!.AssigneeSid);
        Assert.Equal(2,(await server.ListAssignmentHistoryAsync(target,alert,50,null,Timeout(),CancellationToken.None)).Items.Count);
    }

    [Fact]
    public async Task AssignmentDivergentPayloadActorActionAndStaleEpisodeRevisionAreConflicts()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,_,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        AlertAssignmentWriteRequest first=AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,"Responder A");
        await server.SetAssignmentAsync(first,CancellationToken.None);
        foreach(AlertAssignmentWriteRequest conflict in new[]
        {
            AssignmentRequest(target,alert,episode,1,AssignmentOwnerB,"Responder B",first.IdempotencyKey),
            AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,"Responder A",first.IdempotencyKey,reason:"Different reason"),
            AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,"Responder A",first.IdempotencyKey,actor:"S-1-5-19"),
            AssignmentRequest(target,alert,episode,1,null,null,first.IdempotencyKey),
            AssignmentRequest(target,alert,Guid.NewGuid(),2,AssignmentOwnerA,"Responder A"),
            AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,"Responder A"),
        })
            Assert.Equal(409,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>server.SetAssignmentAsync(conflict,CancellationToken.None).AsTask())).StatusCode);
        Assert.Single((await server.ListAssignmentHistoryAsync(target,alert,50,null,Timeout(),CancellationToken.None)).Items);
    }

    [Fact]
    public async Task AssignmentFirstWriteRequiresApprovedResponderButUnassignRequiresNoRosterEntry()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,_,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        AlertAssignmentWriteRequest refused=AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,null) with { AssigneeEligible=false };
        AlertRepositoryOperationException exception=await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>server.SetAssignmentAsync(refused,CancellationToken.None).AsTask());
        Assert.Equal(422,exception.StatusCode); Assert.Equal("assignee_not_approved",exception.Code);
        Assert.Equal(1,(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!.Revision);
        Assert.Empty((await server.ListAssignmentHistoryAsync(target,alert,50,null,Timeout(),CancellationToken.None)).Items);
        await server.SetAssignmentAsync(AssignmentRequest(target,alert,episode,1,null,null) with { AssigneeEligible=false },CancellationToken.None);
        Assert.Equal(2,(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task AssignmentAndSnoozeSameRevisionRaceHaveOneWinnerWithoutCrossFeatureOverwrite()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,_,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        async Task<int> AssignAsync()
        { try { await server.SetAssignmentAsync(AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,"Responder A"),CancellationToken.None); return 200; } catch(AlertRepositoryOperationException exception) { return exception.StatusCode; } }
        async Task<int> SnoozeAsync()
        { try { await server.SetSnoozeAsync(SnoozeRequest(target,alert,episode,1,TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1))),CancellationToken.None); return 200; } catch(AlertRepositoryOperationException exception) { return exception.StatusCode; } }
        int[] outcomes=await Task.WhenAll(AssignAsync(),SnoozeAsync());
        Assert.Equal(AssignmentRaceStatuses,outcomes.Order().ToArray());
        AlertAssignmentStatus assignment=(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!;
        AlertSnoozeStatus snooze=(await server.GetSnoozeAsync(target,alert,Timeout(),CancellationToken.None))!;
        Assert.Equal(2,assignment.Revision); Assert.Equal(2,snooze.Revision);
        Assert.Equal(outcomes[0]==200,assignment.AssigneeSid is not null); Assert.Equal(outcomes[1]==200,snooze.Snoozed);
    }

    [Fact]
    public async Task AssignmentHistoryPagesNativeSequenceAcrossEpisodesWithoutDuplicateOrMissingOperations()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,_,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        var operations=new List<Guid>();
        for(int i=0;i<5;i++)
        {
            AlertAssignmentWriteRequest request=AssignmentRequest(target,alert,episode,i+1,AssignmentOwnerA,"Responder A");
            operations.Add(Guid.Parse(request.IdempotencyKey)); await server.SetAssignmentAsync(request,CancellationToken.None);
        }
        Guid nextEpisode=Guid.NewGuid();
        await using (var change=database.DataSource.CreateCommand("UPDATE alerting.rule_state SET alert_episode_id=@episode,revision=revision+1 WHERE instance_id=@target;"))
        { change.Parameters.AddWithValue("episode",nextEpisode); change.Parameters.AddWithValue("target",target.Value); Assert.Equal(1,await change.ExecuteNonQueryAsync()); }
        Assert.Null((await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!.AssigneeSid);
        AlertAssignmentWriteRequest next=AssignmentRequest(target,alert,nextEpisode,7,AssignmentOwnerB,"Responder B");
        operations.Add(Guid.Parse(next.IdempotencyKey)); await server.SetAssignmentAsync(next,CancellationToken.None);
        var actual=new List<AlertAssignmentHistoryItem>(); Guid? before=null;
        do
        {
            AlertAssignmentHistoryPage page=await server.ListAssignmentHistoryAsync(target,alert,2,before,Timeout(),CancellationToken.None);
            Assert.InRange(page.Items.Count,1,2); actual.AddRange(page.Items); before=page.NextBeforeOperationId;
        } while(before is not null);
        Assert.Equal(operations.AsEnumerable().Reverse(),actual.Select(item=>item.OperationId));
        Assert.Equal(6,actual.Select(item=>item.OperationId).Distinct().Count());
        Assert.Equal(nextEpisode,actual[0].EpisodeId); Assert.Null(actual[0].PreviousAssigneeSid);
        Assert.Equal(episode,actual[1].EpisodeId);
        Assert.Equal(404,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>server.ListAssignmentHistoryAsync(target,Guid.NewGuid(),2,operations[0],Timeout(),CancellationToken.None).AsTask())).StatusCode);
        Assert.Equal(404,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>server.ListAssignmentHistoryAsync(target,alert,2,Guid.NewGuid(),Timeout(),CancellationToken.None).AsTask())).StatusCode);
    }

    [Fact]
    public async Task AssignmentNativeHistoryRejectsForeignAnchorAndPreservesPrivateForcedRlsBoundaries()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,_,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        AlertAssignmentWriteRequest request=AssignmentRequest(target,alert,episode,1,AssignmentOwnerA,"Responder A");
        await server.SetAssignmentAsync(request,CancellationToken.None);
        await using var verify=database.DataSource.CreateCommand("""
            SELECT bool_and(c.relrowsecurity AND c.relforcerowsecurity)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='alerting' AND c.relname IN('episode_assignment','assignment_operation');
            SELECT has_function_privilege('sqlobserver_server','alerting.get_episode_assignment(uuid,uuid)','EXECUTE'),
             has_function_privilege('sqlobserver_collector','alerting.get_episode_assignment(uuid,uuid)','EXECUTE'),
             has_table_privilege('sqlobserver_server','alerting.episode_assignment','SELECT'),
             has_table_privilege('sqlobserver_server','alerting.assignment_operation','UPDATE'),
             has_sequence_privilege('sqlobserver_server','alerting.assignment_operation_change_sequence_seq','USAGE');
            SELECT bool_and(has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')),
             NOT EXISTS(SELECT 1 FROM pg_proc f JOIN pg_namespace ns ON ns.oid=f.pronamespace,
              LATERAL aclexplode(coalesce(f.proacl,acldefault('f',f.proowner))) acl
              WHERE ns.nspname='alerting' AND f.proname IN('get_episode_assignment','set_episode_assignment','list_assignment_history')
               AND acl.grantee=0 AND acl.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='alerting' AND p.proname IN('get_episode_assignment','set_episode_assignment','list_assignment_history');
            """);
        await using (NpgsqlDataReader rows=await verify.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync()); Assert.True(rows.GetBoolean(0));
            Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.True(rows.GetBoolean(0));
            for(int i=1;i<5;i++) Assert.False(rows.GetBoolean(i));
            Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync());
            Assert.True(rows.GetBoolean(0)); Assert.True(rows.GetBoolean(1));
        }
        await using NpgsqlConnection connection=await serverSource.OpenConnectionAsync();
        await using var denied=new NpgsqlCommand("SELECT * FROM alerting.get_episode_assignment(@target,@alert);",connection);
        denied.Parameters.AddWithValue("target",target.Value); denied.Parameters.AddWithValue("alert",alert);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal(404,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>server.ListAssignmentHistoryAsync(new MonitoredInstanceId(Guid.NewGuid()),alert,2,Guid.Parse(request.IdempotencyKey),Timeout(),CancellationToken.None).AsTask())).StatusCode);
    }

    [Fact]
    public async Task AssignmentCollectorSamplesAndHistoricalReplayPreserveHumanOwnerAndRevision()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); Guid ruleId=Guid.NewGuid();
        var lease=new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/assignment-evaluation"),new WorkerExecutionId(Guid.NewGuid()),new FencingToken(1));
        await SeedTargetAndLeaseAsync(database,target.Value,lease);
        await SeedRuleAndDestinationAsync(database,target.Value,ruleId,Guid.NewGuid());
        AlertCatalogEntry catalog=AlertCatalog.Entries.Single(x=>x.Kind==AlertRuleKind.MetricThreshold);
        var rule=new AlertRuleDefinition(ruleId,catalog.Name,catalog.Kind,new MetricId(catalog.Metric!),catalog.Comparison,catalog.Threshold,catalog.Hysteresis,catalog.ConfirmationCount,catalog.ConfirmationWindow,catalog.EvaluationInterval);
        await using NpgsqlDataSource collectorSource=database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var collector=new PostgreSqlAlertRepositoryPort(collectorSource); var server=new PostgreSqlAlertRepositoryPort(serverSource);
        DateTimeOffset observed=TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-2));
        var (batch,state)=await PersistClearConfirmationObservationAsync(database,collector,lease,rule,target,observed,2);
        Assert.Equal(AlertState.Firing,state.State);
        AlertAssignmentWriteReceipt receipt=await server.SetAssignmentAsync(AssignmentRequest(target,state.AlertId!.Value,state.EpisodeId!.Value,state.Revision,AssignmentOwnerA,"Responder A"),CancellationToken.None);
        ReplaySnapshot before=await SnapshotReplayRowsAsync(database,target.Value,ruleId);
        Assert.Equal(0,(await collector.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        Assert.Equal(before,await SnapshotReplayRowsAsync(database,target.Value,ruleId));
        Assert.Equal(receipt.Revision,(await collector.GetStateAsync(target,ruleId,Timeout(),CancellationToken.None))!.Revision);
        var (_,sampleState)=await PersistClearConfirmationObservationAsync(database,collector,lease,rule,target,observed.AddSeconds(30),2);
        AlertAssignmentStatus current=(await server.GetAssignmentAsync(target,state.AlertId.Value,Timeout(),CancellationToken.None))!;
        Assert.Equal(AssignmentOwnerA,current.AssigneeSid); Assert.Equal("Responder A",current.AssigneeName);
        Assert.Equal(sampleState.Revision,current.Revision); Assert.Equal(sampleState.State,current.State);
        Assert.Single((await server.ListAssignmentHistoryAsync(target,state.AlertId.Value,50,null,Timeout(),CancellationToken.None)).Items);
    }

    [Fact]
    public async Task AssignmentAcknowledgedEpisodeKeepsAcknowledgementAndRejectsNewCommandsAfterResolution()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,rule,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        await using NpgsqlDataSource collectorSource=database.CreateCollectorDataSource();
        var collector=new PostgreSqlAlertRepositoryPort(collectorSource);
        await server.AcknowledgeAsync(new AlertAcknowledgeRequest(alert,Guid.NewGuid().ToString("D"),Audit(target,AdministrativeAuditAction.AcknowledgeAlert),Timeout(),1,null,episode),CancellationToken.None);
        AlertRuleState acknowledged=(await collector.GetStateAsync(target,rule,Timeout(),CancellationToken.None))!;
        Assert.NotNull(acknowledged.AcknowledgedUtc);
        Assert.Equal("S-1-5-18",acknowledged.AcknowledgedBy);
        AlertAssignmentWriteRequest request=AssignmentRequest(target,alert,episode,2,AssignmentOwnerA,"Responder A");
        AlertAssignmentWriteReceipt receipt=await server.SetAssignmentAsync(request,CancellationToken.None);
        Assert.Equal(AlertState.Acknowledged,(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!.State);
        AlertRuleState assigned=(await collector.GetStateAsync(target,rule,Timeout(),CancellationToken.None))!;
        Assert.Equal(acknowledged.AcknowledgedUtc,assigned.AcknowledgedUtc);
        Assert.Equal(acknowledged.AcknowledgedBy,assigned.AcknowledgedBy);
        Assert.Equal(acknowledged.LastOperationId,assigned.LastOperationId);
        Assert.Equal(acknowledged.EvidenceDigest,assigned.EvidenceDigest);
        Assert.Equal(acknowledged.Revision+1,assigned.Revision);
        Assert.Equal(AlertState.Acknowledged,assigned.State);
        await using (var resolve=database.DataSource.CreateCommand("UPDATE alerting.rule_state SET state=5,resolved_at=clock_timestamp(),revision=revision+1 WHERE instance_id=@target;"))
        { resolve.Parameters.AddWithValue("target",target.Value); Assert.Equal(1,await resolve.ExecuteNonQueryAsync()); }
        AssertAssignmentReceiptEqual(receipt,await server.SetAssignmentAsync(request,CancellationToken.None));
        Assert.Equal(409,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>server.SetAssignmentAsync(AssignmentRequest(target,alert,episode,4,null,null),CancellationToken.None).AsTask())).StatusCode);
        AlertAssignmentStatus resolved=(await server.GetAssignmentAsync(target,alert,Timeout(),CancellationToken.None))!;
        Assert.Equal(AlertState.Resolved,resolved.State); Assert.Equal(4,resolved.Revision); Assert.Equal(AssignmentOwnerA,resolved.AssigneeSid);
    }

    [Fact]
    public async Task AssignmentNativeCommandsRejectUnsafeIdentitiesReasonLabelAndForgedDigest()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,_,alert,episode,_)=await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        foreach((Guid rawAlert,Guid rawEpisode,Guid correlation,string reason,string name,bool forged) in new[]
        {
            (Guid.Empty,episode,Guid.NewGuid(),"investigate","Responder A",false),
            (alert,Guid.Empty,Guid.NewGuid(),"investigate","Responder A",false),
            (alert,episode,Guid.Empty,"investigate","Responder A",false),
            (alert,episode,Guid.NewGuid(),"unsafe\nreason","Responder A",false),
            (alert,episode,Guid.NewGuid(),new string('é',257),"Responder A",false),
            (alert,episode,Guid.NewGuid(),"investigate","unsafe\nname",false),
            (alert,episode,Guid.NewGuid(),"investigate",new string('é',65),false),
            (alert,episode,Guid.NewGuid(),"investigate","Responder A",true),
        })
        {
            string canonical=string.Join('|',"alert.assign",target.Value.ToString("D"),rawAlert.ToString("D"),rawEpisode.ToString("D"),"1",AssignmentOwnerA,reason);
            string digest=forged?new string('a',64):Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            await using NpgsqlConnection connection=await serverSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
            await using var scope=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction);
            scope.Parameters.AddWithValue("target",target.Value.ToString("D")); await scope.ExecuteNonQueryAsync();
            await using var command=new NpgsqlCommand("SELECT * FROM alerting.set_episode_assignment(@target,@alert,@episode,1,@sid,@name,true,@reason,@operation,'S-1-5-18',@correlation,@digest);",connection,transaction);
            command.Parameters.AddWithValue("target",target.Value); command.Parameters.AddWithValue("alert",rawAlert);
            command.Parameters.AddWithValue("episode",rawEpisode); command.Parameters.AddWithValue("sid",AssignmentOwnerA); command.Parameters.AddWithValue("name",name);
            command.Parameters.AddWithValue("reason",reason); command.Parameters.AddWithValue("operation",Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("correlation",correlation); command.Parameters.AddWithValue("digest",digest);
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var count=database.DataSource.CreateCommand("SELECT count(*) FROM alerting.assignment_operation;");
        Assert.Equal(0L,(long)(await count.ExecuteScalarAsync())!);
    }

    private static AlertAssignmentWriteRequest AssignmentRequest(MonitoredInstanceId target,Guid alert,Guid episode,long revision,
        string? assignee,string? name,string? operation=null,string reason="Synthetic assignment triage",string actor="S-1-5-18") =>
        new(alert,episode,revision,assignee is null?null:new ActorSecurityIdentifier(assignee),reason,
            operation??Guid.NewGuid().ToString("D"),new AdministrativeAuditEnvelope(new ActorSecurityIdentifier(actor),
                new AuditCorrelationId(Guid.NewGuid()),assignee is null?AdministrativeAuditAction.UnassignAlert:AdministrativeAuditAction.AssignAlert,target),Timeout())
        { AssigneeEligible=true,ApprovedAssigneeName=name };

    private static void AssertAssignmentReceiptEqual(AlertAssignmentWriteReceipt expected,AlertAssignmentWriteReceipt actual)
    {
        Assert.Equal(expected.Audit.AuditId.Value,actual.Audit.AuditId.Value);
        Assert.Equal(expected.Audit.RecordedAtUtc,actual.Audit.RecordedAtUtc);
        Assert.Equal(expected.Revision,actual.Revision); Assert.Equal(expected.AssigneeSid,actual.AssigneeSid);
        Assert.Equal(expected.AppliedAtUtc,actual.AppliedAtUtc);
    }
}
