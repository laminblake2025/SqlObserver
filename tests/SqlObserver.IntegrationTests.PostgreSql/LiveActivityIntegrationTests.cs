using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class LiveActivityIntegrationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task CadenceClaimsProcessTwentyFiveTargetsWithoutHoldingSlotsForTenSeconds()
    {
        await using var database=await fixture.CreateDatabaseAsync();
        var timeout=new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        var migrated=await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults,timeout),CancellationToken.None);
        Assert.False(migrated.HasFailures);
        await using(var seed=database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT gen_random_uuid(),'live-cadence-'||g,'Live cadence','sql01',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,
                statement_timestamp(),statement_timestamp(),statement_timestamp()
            FROM generate_series(1,25) g
            """))
            await seed.ExecuteNonQueryAsync();

        await using var collectorSource=database.CreateCollectorDataSource();
        var repository=new PostgreSqlLiveActivityRepository(collectorSource);
        var leases=new PostgreSqlWorkerLeasePort(collectorSource);
        await using(var privileges=database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_collector','live_activity.claim_target(uuid,bigint,uuid,interval)','EXECUTE')
              AND NOT has_function_privilege('sqlobserver_server','live_activity.claim_target(uuid,bigint,uuid,interval)','EXECUTE')
              AND NOT has_table_privilege('sqlobserver_collector','live_activity.cadence_state','UPDATE')
            """))
            Assert.Equal(true,await privileges.ExecuteScalarAsync());
        var owner=new WorkerExecutionId(Guid.NewGuid());
        var duration=new WorkerLeaseDuration(TimeSpan.FromSeconds(30));
        var seen=new HashSet<Guid>();
        LiveActivityTarget? firstTarget=null;
        WorkerLeaseIdentity? firstLease=null;
        var started=System.Diagnostics.Stopwatch.StartNew();
        while(seen.Count<25)
        {
            var targets=await repository.TargetsAsync(CancellationToken.None);
            Assert.NotEmpty(targets);
            foreach(var target in targets)
            {
                Assert.True(seen.Add(target.Id),"A target was selected again before its cadence elapsed.");
                var claimed=await repository.ClaimAsync(target,owner,duration,CancellationToken.None);
                Assert.Equal(LeaseAcquisitionStatus.Acquired,claimed.Status);
                if(firstTarget is null) { firstTarget=target; firstLease=claimed.Lease!.Identity; }
                Assert.Equal(LeaseReleaseStatus.Released,await leases.ReleaseAsync(new(claimed.Lease!.Identity,timeout),CancellationToken.None));
            }
        }
        Assert.True(started.Elapsed<TimeSpan.FromSeconds(10),"Twenty-five targets could not be claimed within one sampling interval.");
        Assert.Empty(await repository.TargetsAsync(CancellationToken.None));
        Assert.Equal(LeaseAcquisitionStatus.Contended,(await repository.ClaimAsync(firstTarget!,new WorkerExecutionId(Guid.NewGuid()),duration,CancellationToken.None)).Status);

        await using(var due=database.DataSource.CreateCommand("UPDATE live_activity.cadence_state SET next_due_at=clock_timestamp()-interval '1 second' WHERE target_id=@target"))
        {
            due.Parameters.AddWithValue("target",firstTarget!.Id);
            Assert.Equal(1,await due.ExecuteNonQueryAsync());
        }
        var stale=await repository.ClaimAsync(firstTarget! with { Revision=2 },new WorkerExecutionId(Guid.NewGuid()),duration,CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Contended,stale.Status);
        var races=await Task.WhenAll(
            repository.ClaimAsync(firstTarget!,new WorkerExecutionId(Guid.NewGuid()),duration,CancellationToken.None),
            repository.ClaimAsync(firstTarget!,new WorkerExecutionId(Guid.NewGuid()),duration,CancellationToken.None));
        var afterDue=Assert.Single(races,result=>result.Status==LeaseAcquisitionStatus.Acquired);
        Assert.Single(races,result=>result.Status==LeaseAcquisitionStatus.Contended);
        Assert.True(afterDue.Lease!.Identity.FencingToken.Value>firstLease!.FencingToken.Value);
        await repository.FailedAsync(firstTarget!.Id,afterDue.Lease.Identity,CancellationToken.None);
        Assert.Equal(LeaseReleaseStatus.Released,await leases.ReleaseAsync(new(afterDue.Lease.Identity,timeout),CancellationToken.None));
        await using(var backoff=database.DataSource.CreateCommand("""
            SELECT consecutive_failures,next_due_at>clock_timestamp()+interval '9 seconds'
            FROM live_activity.cadence_state WHERE target_id=@target
            """))
        {
            backoff.Parameters.AddWithValue("target",firstTarget.Id);
            await using var reader=await backoff.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)1,reader.GetInt16(0));
            Assert.True(reader.GetBoolean(1));
        }
        await using(var due=database.DataSource.CreateCommand("UPDATE live_activity.cadence_state SET next_due_at=clock_timestamp()-interval '1 second' WHERE target_id=@target"))
        {
            due.Parameters.AddWithValue("target",firstTarget.Id);
            await due.ExecuteNonQueryAsync();
        }
        var retry=await repository.ClaimAsync(firstTarget,new WorkerExecutionId(Guid.NewGuid()),duration,CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired,retry.Status);
        await repository.FailedAsync(firstTarget.Id,retry.Lease!.Identity,CancellationToken.None);
        Assert.Equal(LeaseReleaseStatus.Released,await leases.ReleaseAsync(new(retry.Lease.Identity,timeout),CancellationToken.None));
        await using(var backoff=database.DataSource.CreateCommand("""
            SELECT consecutive_failures,next_due_at>clock_timestamp()+interval '19 seconds'
            FROM live_activity.cadence_state WHERE target_id=@target
            """))
        {
            backoff.Parameters.AddWithValue("target",firstTarget.Id);
            await using var reader=await backoff.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)2,reader.GetInt16(0));
            Assert.True(reader.GetBoolean(1));
        }
        await using(var due=database.DataSource.CreateCommand("UPDATE live_activity.cadence_state SET next_due_at=clock_timestamp()-interval '1 second' WHERE target_id=@target"))
        {
            due.Parameters.AddWithValue("target",firstTarget.Id);
            await due.ExecuteNonQueryAsync();
        }
        var recovery=await repository.ClaimAsync(firstTarget,new WorkerExecutionId(Guid.NewGuid()),duration,CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired,recovery.Status);
        await repository.CommitAsync(firstTarget,recovery.Lease!.Identity,
            new LiveActivityCapture(Guid.NewGuid(),DateTimeOffset.UtcNow,false,[],[]),CancellationToken.None);
        Assert.Equal(LeaseReleaseStatus.Released,await leases.ReleaseAsync(new(recovery.Lease.Identity,timeout),CancellationToken.None));
        await using(var recovered=database.DataSource.CreateCommand("""
            SELECT cadence.consecutive_failures,collection.failed
            FROM live_activity.cadence_state cadence
            JOIN live_activity.collection_state collection ON collection.target_id=cadence.target_id
            WHERE cadence.target_id=@target
            """))
        {
            recovered.Parameters.AddWithValue("target",firstTarget.Id);
            await using var reader=await recovered.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)0,reader.GetInt16(0));
            Assert.False(reader.GetBoolean(1));
        }
    }

    [Fact]
    public async Task TargetSelectionRotatesAcrossTheFleetInsteadOfStarvingTargetsAfterTheFirstTen()
    {
        await using var database=await fixture.CreateDatabaseAsync();
        var timeout=new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        var migrated=await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults,timeout),CancellationToken.None);
        Assert.False(migrated.HasFailures);

        await using(var seed=database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT gen_random_uuid(),'live-rotation-'||g,'Live rotation','sql01',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,
                statement_timestamp(),statement_timestamp(),statement_timestamp()
            FROM generate_series(1,25) g
            """))
            await seed.ExecuteNonQueryAsync();

        await using var collectorSource=database.CreateCollectorDataSource();
        var repository=new PostgreSqlLiveActivityRepository(collectorSource);
        var seen=new HashSet<Guid>();
        for(var cycle=0;cycle<3;cycle++)
        {
            var targets=await repository.TargetsAsync(CancellationToken.None);
            Assert.Equal(10,targets.Count);
            foreach(var target in targets) seen.Add(target.Id);
            await using var captured=database.DataSource.CreateCommand("""
                INSERT INTO live_activity.snapshot(id,target_id,revision,observed_at,truncated)
                SELECT gen_random_uuid(),t.instance_id,t.revision,clock_timestamp(),false
                FROM control.observation_target t WHERE t.instance_id=ANY(@ids)
                """);
            captured.Parameters.AddWithValue("ids",targets.Select(target=>target.Id).ToArray());
            await captured.ExecuteNonQueryAsync();
        }

        Assert.Equal(25,seen.Count);
    }

    [Fact]
    public async Task FilteringPagingLifetimesMinuteSnapshotsAndExpiryAreRepositoryBounded()
    {
        await using var database=await fixture.CreateDatabaseAsync();
        var timeout=new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        var migrated=await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,timeout),CancellationToken.None);
        Assert.False(migrated.HasFailures);
        Guid target=Guid.NewGuid();
        await using(var command=database.DataSource.CreateCommand("INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at) VALUES(@id,@key,'Live test','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp())"))
        {
            command.Parameters.AddWithValue("id",target); command.Parameters.AddWithValue("key","live-"+target.ToString("N")); await command.ExecuteNonQueryAsync();
        }
        var repository=new PostgreSqlLiveActivityRepository(database.DataSource);
        var liveTarget=Assert.Single(await repository.TargetsAsync(CancellationToken.None));
        var lease=await new PostgreSqlWorkerLeasePort(database.DataSource).AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey("collector/live-activity/"+target.ToString("D")),new WorkerExecutionId(Guid.NewGuid()),new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),timeout),CancellationToken.None);
        var now=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // Keep both observations inside the previous UTC minute, regardless of test start boundary.
        var minute=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,now.Minute,0,TimeSpan.Zero).AddMinutes(-1);
        var rows=Enumerable.Range(1,120).Select(i=>new LiveActivityRow { Identity="life"+i,SessionId=i,RequestId=0,Status="running",IsUser=true,DatabaseId=i<=60?1:2,DatabaseName=i<=60?"First":"Second",CpuMs=i,Reads=i,Writes=i,LogicalReads=i }).ToArray();
        rows[1]=rows[1] with {SessionId=1,RequestId=1};
        rows[4]=rows[4] with {Blocker=99};
        rows=[..rows,new LiveActivityRow {Identity="idle",SessionId=200,Status="sleeping",IsUser=true,DatabaseId=2,DatabaseName="Second"},
            new LiveActivityRow {Identity="system",SessionId=201,RequestId=0,Status="background",DatabaseId=2,DatabaseName="Second"}];
        var payload=new LiveActivityPayload(Guid.NewGuid(),new ProtectedSensitivePayload(SensitivePayloadKind.QueryText,new SensitivePayloadFingerprint(new byte[32]),"AES-256-GCM","test",new byte[12],new byte[16],new byte[16]));
        rows[0]=rows[0] with { QueryId=payload.Id,QueryState="available" };
        var first=new LiveActivityCapture(Guid.NewGuid(),minute.AddSeconds(1),false,rows,[payload]);
        await repository.CommitAsync(liveTarget,lease.Lease!.Identity,first,CancellationToken.None);
        var filter=new LiveActivityFilter(DatabaseId:2);
        var page=await repository.ReadAsync(new(target,null,filter),CancellationToken.None);
        Assert.Equal(50,page.Rows.Count); Assert.All(page.Rows,r=>Assert.Equal(2,r.DatabaseId)); Assert.NotNull(page.NextCursor);
        var end=await repository.ReadAsync(new(target,null,filter,page.NextCursor),CancellationToken.None);
        Assert.Equal(10,end.Rows.Count); Assert.Null(end.NextCursor);
        var idle=await repository.ReadAsync(new(target,null,filter with {IncludeIdle=true,Sort="session"}),CancellationToken.None);
        Assert.Equal(200,idle.Rows[0].SessionId); Assert.Null(idle.Rows[0].RequestId);
        var system=await repository.ReadAsync(new(target,null,filter with {IncludeSystem=true,Sort="session"}),CancellationToken.None);
        Assert.Equal(201,system.Rows[0].SessionId); Assert.False(system.Rows[0].IsUser);
        var blocked=await repository.ReadAsync(new(target,null,new LiveActivityFilter(BlockedOnly:true)),CancellationToken.None);
        Assert.Equal(99,Assert.Single(blocked.Rows).Blocker);
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.ReadAsync(new(target,null,filter with { DatabaseId=1 },page.NextCursor),CancellationToken.None));
        rows=rows.Select(r=>r with { CpuMs=r.CpuMs+2 }).ToArray();
        rows[0]=rows[0] with { Identity="reused-session" }; rows[1]=rows[1] with { CpuMs=0 };
        var second=new LiveActivityCapture(Guid.NewGuid(),minute.AddSeconds(11),false,rows,[]);
        await repository.CommitAsync(liveTarget,lease.Lease.Identity,second,CancellationToken.None);
        var deltas=await repository.ReadAsync(new(target,null,new LiveActivityFilter(Sort:"session",Descending:false)),CancellationToken.None);
        Assert.Null(deltas.Rows[0].Delta); Assert.Null(deltas.Rows[1].Delta); Assert.Equal("2",deltas.Rows[2].Delta!.CpuMs);
        Assert.Equal(2,deltas.Rows.Count(r=>r.SessionId==1));
        var truncated=new LiveActivityCapture(Guid.NewGuid(),minute.AddSeconds(21),true,rows,[]);
        var invalidLease=new WorkerLeaseIdentity(lease.Lease.Identity.Key,new WorkerExecutionId(Guid.NewGuid()),lease.Lease.Identity.FencingToken);
        await Assert.ThrowsAsync<PostgresException>(()=>repository.CommitAsync(liveTarget,invalidLease,truncated,CancellationToken.None));
        await repository.CommitAsync(liveTarget,lease.Lease.Identity,truncated,CancellationToken.None);
        var partial=await repository.ReadAsync(new(target,null,filter),CancellationToken.None);
        Assert.True(partial.Truncated); Assert.All(partial.Rows,r=>Assert.Null(r.Delta));
        var history=await repository.HistoryAsync(target,now.AddHours(-24),now,CancellationToken.None);
        Assert.Equal(first.Id,Assert.Single(history).Id);
        Guid deadlockEvent=Guid.NewGuid();
        var triggerLease=await new PostgreSqlWorkerLeasePort(database.DataSource).AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey("collector/live-activity-deadlock/"+target.ToString("D")),new WorkerExecutionId(Guid.NewGuid()),new WorkerLeaseDuration(TimeSpan.FromMinutes(2)),timeout),CancellationToken.None);
        var triggeredObserved=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var triggered=new LiveActivityCapture(Guid.NewGuid(),triggeredObserved,false,rows,[],null,deadlockEvent,triggeredObserved.AddSeconds(-1));
        await repository.CommitAsync(liveTarget,triggerLease.Lease!.Identity,triggered,CancellationToken.None);
        Assert.True(await repository.HasDeadlockSnapshotAsync(target,deadlockEvent,CancellationToken.None));
        await repository.CommitAsync(liveTarget,triggerLease.Lease.Identity,triggered with { Id=Guid.NewGuid(),ObservedUtc=triggeredObserved.AddSeconds(1) },CancellationToken.None);
        var triggeredHistory=await repository.HistoryAsync(target,now.AddHours(-24),triggeredObserved.AddSeconds(2),CancellationToken.None);
        Assert.Contains(triggeredHistory, snapshot=>snapshot.Id==triggered.Id && snapshot.DeadlockEventId==deadlockEvent && snapshot.DeadlockOccurredUtc==triggered.DeadlockOccurredUtc);
        Assert.Equal(2,triggeredHistory.Count);
        Assert.NotNull(await repository.QueryAsync(target,first.Id,"life1",CancellationToken.None));
        Assert.Null(await repository.QueryAsync(Guid.NewGuid(),first.Id,"life1",CancellationToken.None));
        Assert.Null(await repository.QueryAsync(target,first.Id,"reused-session",CancellationToken.None));
        await repository.FailedAsync(target,lease.Lease.Identity,CancellationToken.None);
        Assert.Equal("stale",(await repository.ReadAsync(new(target,null,filter),CancellationToken.None)).State);
        await using(var privileges=database.DataSource.CreateCommand("SELECT has_function_privilege('sqlobserver_server','live_activity.read_page(uuid,uuid,jsonb,integer)','EXECUTE') AND NOT has_function_privilege('sqlobserver_collector','live_activity.query_payload(uuid,uuid,text)','EXECUTE') AND NOT has_table_privilege('sqlobserver_server','live_activity.payload','SELECT') AND NOT has_table_privilege('sqlobserver_collector','live_activity.observation','INSERT')"))
            Assert.Equal(true,await privileges.ExecuteScalarAsync());
        Assert.Empty(await repository.HistoryAsync(target,minute.AddMinutes(-2),minute.AddMinutes(-1),CancellationToken.None));
        // Ten target evidence sets and ten viewers per target. Reads cannot create collection work.
        await using(var seed=database.DataSource.CreateCommand("INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at) SELECT gen_random_uuid(),'live-extra-'||g,'Load target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp() FROM generate_series(1,9) g")) await seed.ExecuteNonQueryAsync();
        var dueTargets=await repository.TargetsAsync(CancellationToken.None);
        Assert.Equal(9,dueTargets.Count);
        Assert.DoesNotContain(dueTargets,t=>t.Id==target);
        var targets=dueTargets.Append(liveTarget).ToArray();
        var watch=System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(targets.Where(t=>t.Id!=target).Select(async t=>
        {
            var acquired=await new PostgreSqlWorkerLeasePort(database.DataSource).AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey("collector/live-activity/"+t.Id.ToString("D")),new WorkerExecutionId(Guid.NewGuid()),new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),timeout),CancellationToken.None);
            await repository.CommitAsync(t,acquired.Lease!.Identity,new LiveActivityCapture(Guid.NewGuid(),now,false,rows.Select(r=>r with { QueryId=null,QueryState="omitted" }).ToArray(),[]),CancellationToken.None);
        }));
        var viewerPages=await Task.WhenAll(targets.SelectMany(t=>Enumerable.Range(0,10).Select(_=>repository.ReadAsync(new(t.Id,null,filter),CancellationToken.None))));
        Assert.Equal(100,viewerPages.Length); Assert.All(viewerPages,p=>Assert.Equal(50,p.Rows.Count));
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(10),"Ten-target repository workload exceeded one sampling interval.");
        await using(var unchanged=database.DataSource.CreateCommand("SELECT count(*) FROM live_activity.snapshot")) Assert.Equal(13L,await unchanged.ExecuteScalarAsync());
        // Exact visibility boundary must be enforced even before physical cleanup.
        await using(var expire=database.DataSource.CreateCommand("UPDATE live_activity.snapshot SET observed_at=clock_timestamp()-interval '24 hours' WHERE target_id=@id"))
        { expire.Parameters.AddWithValue("id",target); await expire.ExecuteNonQueryAsync(); }
        Assert.Empty(await repository.HistoryAsync(target,now.AddHours(-25),now,CancellationToken.None));
        Assert.Null((await repository.ReadAsync(new(target,first.Id,new()),CancellationToken.None)).SnapshotId);
        Assert.Null(await repository.QueryAsync(target,first.Id,"life1",CancellationToken.None));
        await using(var agePayload=database.DataSource.CreateCommand("UPDATE live_activity.payload SET created_at=clock_timestamp()-interval '6 minutes'")) await agePayload.ExecuteNonQueryAsync();
        await using(var orphanBurst=database.DataSource.CreateCommand("INSERT INTO live_activity.payload(target_id,id,protected,created_at) SELECT @target,gen_random_uuid(),'{}',clock_timestamp()-interval '6 minutes' FROM generate_series(1,9000)"))
        { orphanBurst.Parameters.AddWithValue("target",target); await orphanBurst.ExecuteNonQueryAsync(); }
        await repository.CleanupAsync(CancellationToken.None);
        await using var count=database.DataSource.CreateCommand("SELECT count(*) FROM live_activity.snapshot WHERE target_id=@target"); count.Parameters.AddWithValue("target",target);
        Assert.Equal(0L,await count.ExecuteScalarAsync());
        await using var payloadCount=database.DataSource.CreateCommand("SELECT count(*) FROM live_activity.payload");
        Assert.Equal(809L,await payloadCount.ExecuteScalarAsync());
        await repository.CleanupAsync(CancellationToken.None);
        Assert.Equal(0L,await payloadCount.ExecuteScalarAsync());
        // A new reference to previously deduplicated text must survive concurrent maintenance.
        var reused=new LiveActivityCapture(Guid.NewGuid(),DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),false,
            [rows[0] with { Identity="returned",QueryId=payload.Id }],[payload]);
        await Task.WhenAll(repository.CleanupAsync(CancellationToken.None),repository.CommitAsync(liveTarget,lease.Lease.Identity,reused,CancellationToken.None));
        Assert.NotNull(await repository.QueryAsync(target,reused.Id,"returned",CancellationToken.None));
    }
}
