using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    [Fact]
    public async Task BlockingAlertEvidenceReadsCompleteSnapshotsAndFencesScopeAndRunRoutes()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); Guid emptyRun=Guid.NewGuid();
        DateTimeOffset at=(await ReadRepositoryClockAsync(database)).AddSeconds(-1);
        await SeedTargetAndRunAsync(database,target,emptyRun,"blocking.current",at,0);
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector);
        var port=new PostgreSqlBlockingAlertEvidencePort(collector);
        BlockingAlertEvidence empty=await port.ReadAsync(target,1,lease,Timeout,CancellationToken.None);
        Assert.Equal(BlockingAlertEvidenceReason.Current,empty.Reason);
        Assert.Equal(0,empty.MaximumWaitMilliseconds); Assert.Equal(0,empty.RetainedEdges);
        Assert.Equal(emptyRun,empty.RunId); Assert.Null(empty.SourceObservedAtUtc);

        at=await ReadRepositoryClockAsync(database); Guid current=Guid.NewGuid();
        await SeedTargetAndRunAsync(database,target,current,"blocking.current",at);
        await EnsureBlockingPartitionAsync(database,at);
        await InsertBlockingAlertEdgeAsync(database,target,current,at,71,65000);
        await InsertBlockingAlertEdgeAsync(database,target,current,at,72,12500);
        BlockingAlertEvidence evidence=await port.ReadAsync(target,1,lease,Timeout,CancellationToken.None);
        Assert.Equal(BlockingAlertEvidenceReason.Current,evidence.Reason); Assert.Equal(current,evidence.RunId);
        Assert.Equal(65000,evidence.MaximumWaitMilliseconds); Assert.Equal(65d,evidence.MaximumWaitSeconds);
        Assert.Equal(2,evidence.RetainedEdges); Assert.NotNull(evidence.ValidUntilUtc);

        // Exercise the actual routed health view against the mirrored run source.
        await ExecuteAsync(database,"UPDATE system.collection_run_v2_health_read_route SET use_v2=true;");
        BlockingAlertEvidence routed=await port.ReadAsync(target,1,lease,Timeout,CancellationToken.None);
        Assert.Equal(evidence.RunId,routed.RunId); Assert.Equal(evidence.MaximumWaitMilliseconds,routed.MaximumWaitMilliseconds);
        Assert.Equal(evidence.CompletionDigest,routed.CompletionDigest); Assert.Equal(evidence.ScheduleRevision,routed.ScheduleRevision);
        Assert.Equal(evidence.SourceDigest,routed.SourceDigest); Assert.True(routed.ReadAtUtc>evidence.ReadAtUtc);
        var other=new MonitoredInstanceId(Guid.NewGuid()); Guid otherRun=Guid.NewGuid();
        at=await ReadRepositoryClockAsync(database);
        await SeedTargetAndRunAsync(database,other,otherRun,"blocking.current",at,1);
        await InsertBlockingAlertEdgeAsync(database,other,otherRun,at,71,999000);
        Assert.Equal(65000,(await port.ReadAsync(target,1,lease,Timeout,CancellationToken.None)).MaximumWaitMilliseconds);
        Assert.Equal(999000,(await port.ReadAsync(other,1,lease,Timeout,CancellationToken.None)).MaximumWaitMilliseconds);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(async()=>
            await port.ReadAsync(target,2,lease,Timeout,CancellationToken.None))).SqlState);
        var wrongOwner=new WorkerLeaseIdentity(lease.Key,new(Guid.NewGuid()),lease.FencingToken);
        Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(async()=>
            await port.ReadAsync(target,1,wrongOwner,Timeout,CancellationToken.None))).SqlState);

        await using (var unscoped=collector.CreateCommand("SELECT * FROM alerting.read_blocking_alert_evidence(@target,1,@owner,@fence);"))
        {
            unscoped.Parameters.AddWithValue("target",target.Value); unscoped.Parameters.AddWithValue("owner",lease.Owner.Value);
            unscoped.Parameters.AddWithValue("fence",lease.FencingToken.Value);
            Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(async()=>await unscoped.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var privileges=database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner),has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='alerting.read_blocking_alert_evidence(uuid,bigint,uuid,bigint)'::regprocedure;
            """))
        {
            await using NpgsqlDataReader roles=await privileges.ExecuteReaderAsync(); Assert.True(await roles.ReadAsync());
            Assert.Equal("sqlobserver_migrator",roles.GetString(0)); Assert.True(roles.GetBoolean(1));
            Assert.False(roles.GetBoolean(2)); Assert.False(roles.GetBoolean(3)); Assert.False(roles.GetBoolean(4));
        }
        await ExecuteAsync(database,"""
            UPDATE control.worker_lease SET acquired_at=statement_timestamp()-interval '3 minutes',
              renewed_at=statement_timestamp()-interval '2 minutes',expires_at=statement_timestamp()-interval '1 minute'
            WHERE work_key='alerts/evaluation/reconcile';
            """);
        Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(async()=>
            await port.ReadAsync(target,1,lease,Timeout,CancellationToken.None))).SqlState);
    }

    [Fact]
    public async Task BlockingAlertEvidenceDoesNotInventZeroFromMissingPartialStaleOrInconsistentSources()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector=database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease=await BlockingAlertLeaseAsync(collector);
        var port=new PostgreSqlBlockingAlertEvidencePort(collector);
        async Task Unknown(MonitoredInstanceId target,BlockingAlertEvidenceReason reason)
        {
            BlockingAlertEvidence evidence=await port.ReadAsync(target,1,lease,Timeout,CancellationToken.None);
            Assert.Equal(reason,evidence.Reason); Assert.Null(evidence.MaximumWaitMilliseconds);
            Assert.Null(evidence.MaximumWaitSeconds); Assert.Null(evidence.ValidUntilUtc);
        }
        async Task<(MonitoredInstanceId Target,Guid Run,DateTimeOffset At)> Seed(int count=1,string outcome="succeeded",bool loss=false,TimeSpan? age=null)
        {
            var target=new MonitoredInstanceId(Guid.NewGuid()); Guid run=Guid.NewGuid();
            DateTimeOffset at=(await ReadRepositoryClockAsync(database))-(age??TimeSpan.Zero);
            await SeedTargetAndRunAsync(database,target,run,"blocking.current",at,count,outcome:outcome,loss:loss);
            await EnsureBlockingPartitionAsync(database,at); return (target,run,at);
        }
        var absent=new MonitoredInstanceId(Guid.NewGuid()); await InsertActiveTargetAsync(database,absent);
        await Unknown(absent,BlockingAlertEvidenceReason.SourceUnavailable);
        var missing=await Seed(); await Unknown(missing.Target,BlockingAlertEvidenceReason.SourceInconsistent);
        var partial=await Seed(outcome:"partial",loss:true);
        await InsertBlockingAlertEdgeAsync(database,partial.Target,partial.Run,partial.At,71,65000);
        await Unknown(partial.Target,BlockingAlertEvidenceReason.SourceIncomplete);
        var stale=await Seed(age:TimeSpan.FromHours(1));
        await InsertBlockingAlertEdgeAsync(database,stale.Target,stale.Run,stale.At,71,65000);
        await Unknown(stale.Target,BlockingAlertEvidenceReason.SourceStale);
        var oldRow=await Seed();
        await InsertBlockingAlertEdgeAsync(database,oldRow.Target,oldRow.Run,oldRow.At.AddHours(-1),71,65000);
        await Unknown(oldRow.Target,BlockingAlertEvidenceReason.SourceStale);
        var future=await Seed();
        await InsertBlockingAlertEdgeAsync(database,future.Target,future.Run,future.At.AddMinutes(1),71,65000);
        await Unknown(future.Target,BlockingAlertEvidenceReason.SourceStale);
        var disabled=await Seed(0);
        await ExecuteAsync(database,"UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target AND collector_id='blocking.current';",("target",disabled.Target.Value));
        await Unknown(disabled.Target,BlockingAlertEvidenceReason.CollectorDisabled);
        var reconfigured=await Seed(0); await BumpScheduleRevisionAsync(database,reconfigured.Target,"blocking.current");
        await Unknown(reconfigured.Target,BlockingAlertEvidenceReason.SourceUnavailable);
        var failed=await Seed(); await InsertBlockingAlertEdgeAsync(database,failed.Target,failed.Run,failed.At,71,65000);
        await SeedTargetAndRunAsync(database,failed.Target,Guid.NewGuid(),"blocking.current",await ReadRepositoryClockAsync(database),0,outcome:"permission_denied");
        await Unknown(failed.Target,BlockingAlertEvidenceReason.SourceUnavailable);
        var excessive=await Seed();
        await InsertBlockingAlertEdgeAsync(database,excessive.Target,excessive.Run,excessive.At,71,9007199254740992);
        await Unknown(excessive.Target,BlockingAlertEvidenceReason.SourceInconsistent);
        var self=await Seed(); await InsertBlockingAlertEdgeAsync(database,self.Target,self.Run,self.At,99,65000);
        await Unknown(self.Target,BlockingAlertEvidenceReason.SourceInconsistent);
        var revision=await Seed(); await InsertBlockingAlertEdgeAsync(database,revision.Target,revision.Run,revision.At,71,65000,2);
        await Unknown(revision.Target,BlockingAlertEvidenceReason.SourceInconsistent);
        var overflow=await Seed(1025);
        await ExecuteAsync(database,"""
            INSERT INTO events.blocking_edge(observed_at,edge_id,collection_run_id,instance_id,target_revision,blocked_session_id,
              blocker_kind,blocker_session_id,wait_type,waiting_task_count,wait_duration_ms,root_blocker_session_id,chain_depth,chain_state,collected_at)
            SELECT @at,gen_random_uuid(),@run,@target,1,n,'session',32767,'LCK_M_S',1,65000,32767,1,'resolved',@at
            FROM generate_series(1,1025) n;
            """,("at",overflow.At),("run",overflow.Run),("target",overflow.Target.Value));
        await Unknown(overflow.Target,BlockingAlertEvidenceReason.SourceInconsistent);
    }

    private static async Task<WorkerLeaseIdentity> BlockingAlertLeaseAsync(NpgsqlDataSource collector)=>Assert.IsType<WorkerLease>(
        (await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("alerts/evaluation/reconcile"),new(Guid.NewGuid()),
            new(TimeSpan.FromMinutes(2)),Timeout),CancellationToken.None)).Lease).Identity;
    private static Task InsertBlockingAlertEdgeAsync(RepositoryTestDatabase database,MonitoredInstanceId target,Guid run,
        DateTimeOffset at,int blocked,long duration,long revision=1)=>ExecuteAsync(database,"""
            INSERT INTO events.blocking_edge(observed_at,edge_id,collection_run_id,instance_id,target_revision,blocked_session_id,
              blocker_kind,blocker_session_id,wait_type,waiting_task_count,wait_duration_ms,root_blocker_session_id,chain_depth,chain_state,collected_at)
            VALUES(@at,gen_random_uuid(),@run,@target,@revision,@blocked,'session',99,'LCK_M_S',1,@duration,99,1,'resolved',@at);
            """,("at",at),("run",run),("target",target.Value),("revision",revision),("blocked",blocked),("duration",duration));
}
