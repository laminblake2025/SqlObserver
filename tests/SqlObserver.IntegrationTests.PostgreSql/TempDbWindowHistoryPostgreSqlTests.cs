using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class TempDbWindowHistoryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CanonicalTypedCommitReplayAndIndependentSummaryFilePagesSurviveOriginalSeal(bool seal)
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture,seal); Guid target=await TempDbWindowFixture.TargetAsync(db); var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await TempDbWindowFixture.CommitAsync(db,target,3);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);
        var list=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(TempDbWindowFixture.List(target,window),CancellationToken.None));
        Assert.Equal(1,list.Coverage.CompleteRunCount);Assert.Equal(run.Id.Value,Assert.Single(list.Items).RunId);
        var request=TempDbWindowFixture.Exact(target,window,run.Id.Value,1); var first=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(request,CancellationToken.None));
        Assert.Equal("9223372036854775807",first.Summary!.TotalBytes);Assert.Equal("9007199254740993",Assert.Single(first.Items).SizeBytes);
        var second=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(request with {Cursor=first.NextAnchor},CancellationToken.None));
        var third=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(request with {Cursor=second.NextAnchor},CancellationToken.None));
        Assert.Equal(2,Assert.Single(second.Items).FileId);Assert.Equal(3,Assert.Single(third.Items).FileId);Assert.Null(third.NextAnchor);
        Assert.Equal(first.Summary,third.Summary);Assert.Equal(first.Receipt,third.Receipt);Assert.Equal(first.CutoffUtc,third.CutoffUtc);Assert.Equal(first.ReadEpoch,third.ReadEpoch);
        Assert.Equal(first.NextAnchor!.IssuedAtUtc,second.NextAnchor!.IssuedAtUtc);Assert.Equal(first.Summary.ObservedAtUtc,third.Items[0].ObservedAtUtc);
        if(seal)Assert.Equal(0L,await DatabaseFileWindowFixture.CountAsync(db,"SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;",target));
    }
    [Theory]
    [InlineData(CollectorRunOutcome.PermissionDenied)] [InlineData(CollectorRunOutcome.TimedOut)] [InlineData(CollectorRunOutcome.TransientFailure)]
    [InlineData(CollectorRunOutcome.PermanentFailure)] [InlineData(CollectorRunOutcome.Unsupported)]
    public async Task NewestFailedReceiptRemainsSelectedInsteadOfOlderSummary(CollectorRunOutcome outcome)
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture);Guid target=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        await TempDbWindowFixture.CommitAsync(db,target,1);var failed=await TempDbWindowFixture.CommitAsync(db,target,0,outcome);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);
        var page=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(TempDbWindowFixture.List(target,window),CancellationToken.None));Assert.Equal(failed.Id.Value,page.Items[0].RunId);Assert.Equal(1,page.Coverage.FailedRunCount);
        var exact=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(TempDbWindowFixture.Exact(target,window,failed.Id.Value),CancellationToken.None));
        Assert.Equal("collection_failed",exact.Reason);Assert.NotNull(exact.Receipt);Assert.Null(exact.Summary);Assert.Empty(exact.Items);
    }
    [Fact]
    public async Task EmptyAndLostSourcesRemainDifferentAccountingWithoutHealthInference()
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture);Guid target=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var empty=await TempDbWindowFixture.CommitAsync(db,target,0);var lost=await TempDbWindowFixture.CommitAsync(db,target,1,CollectorRunOutcome.Partial);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);
        var page=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(TempDbWindowFixture.List(target,window),CancellationToken.None));Assert.Equal(1,page.Coverage.EmptyRunCount);Assert.Equal(1,page.Coverage.LostRunCount);
        var a=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(TempDbWindowFixture.Exact(target,window,empty.Id.Value),CancellationToken.None));Assert.Equal("observed_empty",a.Reason);Assert.Equal(6,a.Summary!.SourceState);Assert.Empty(a.Items);
        var b=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(TempDbWindowFixture.Exact(target,window,lost.Id.Value),CancellationToken.None));Assert.Equal("collection_loss",b.Reason);Assert.Single(b.Items);Assert.True(b.Summary!.Truncated);
    }
    [Fact]
    public async Task FixedCutoffIgnoresLaterCommitsButNewReadsSeeThemAndExactEpochIsIndependent()
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture);Guid target=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var one=await TempDbWindowFixture.CommitAsync(db,target,2);await TempDbWindowFixture.CommitAsync(db,target,1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);
        var request=TempDbWindowFixture.List(target,window,1);var first=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(request,CancellationToken.None));
        var exact=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(TempDbWindowFixture.Exact(target,window,one.Id.Value,1),CancellationToken.None));
        await TempDbWindowFixture.CommitAsync(db,target,1);
        var tail=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(request with {Cursor=first.NextAnchor},CancellationToken.None));Assert.Equal(2,tail.Coverage.RunCount);Assert.Equal(first.ReadEpoch,tail.ReadEpoch);
        var current=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(request,CancellationToken.None));Assert.Equal(3,current.Coverage.RunCount);Assert.NotEqual(first.ReadEpoch,current.ReadEpoch);
        var files=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(TempDbWindowFixture.Exact(target,window,one.Id.Value,1,exact.NextAnchor),CancellationToken.None));Assert.Equal(exact.ReadEpoch,files.ReadEpoch);Assert.Equal(exact.Summary,files.Summary);
    }
    [Fact]
    public async Task SourceClockIsRetainedLiterallyAndDoesNotBecomeCompletionTime()
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture);Guid target=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await TempDbWindowFixture.CommitAsync(db,target,1,observedOffset:TimeSpan.FromMinutes(1));await using var server=db.CreateServerDataSource();
        var page=Assert.IsType<TempDbWindowHistorySnapshotPage>(await new PostgreSqlTempDbWindowHistoryRepositoryPort(server).GetAsync(TempDbWindowFixture.Exact(target,window,run.Id.Value),CancellationToken.None));
        Assert.Equal("source_clock_skew",page.Reason);Assert.Equal("partial",page.State);Assert.True(page.Summary!.ObservedAtUtc>page.CutoffUtc);Assert.Equal(page.Summary.ObservedAtUtc,page.Receipt!.ObservedAtUtc);
    }
    [Fact]
    public async Task ExactContextScopeRevisionOutsideWindowAndExpiryNeverSubstituteLatest()
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture);Guid target=await TempDbWindowFixture.TargetAsync(db),other=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await TempDbWindowFixture.CommitAsync(db,target,2);var foreign=await TempDbWindowFixture.CommitAsync(db,other,1);await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);
        var request=TempDbWindowFixture.Exact(target,window,run.Id.Value,1);var exact=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(request,CancellationToken.None));
        var absent=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(request with {RunId=foreign.Id.Value},CancellationToken.None));Assert.Null(absent.Receipt);Assert.Null(absent.Summary);Assert.Empty(absent.Items);
        var outside=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(request with {FromUtc=exact.Receipt!.CompletedAtUtc.AddTicks(10)},CancellationToken.None));Assert.Equal("snapshot_outside_window",outside.Reason);
        Assert.Equal(409,(await Assert.ThrowsAsync<TempDbWindowHistoryReadException>(()=>reader.GetAsync(request with {Cursor=exact.NextAnchor! with {IssuedAtUtc=exact.NextAnchor.IssuedAtUtc.AddMinutes(-16),CutoffUtc=exact.CutoffUtc.AddMinutes(-16)}},CancellationToken.None).AsTask())).StatusCode);
        await using(var change=db.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2,updated_at=statement_timestamp() WHERE instance_id=@target;")){change.Parameters.AddWithValue("target",target);await change.ExecuteNonQueryAsync();}
        Assert.Equal(409,(await Assert.ThrowsAsync<TempDbWindowHistoryReadException>(()=>reader.ListAsync(TempDbWindowFixture.List(target,window),CancellationToken.None).AsTask())).StatusCode);
        var old=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(TempDbWindowFixture.List(target,window) with {TargetRevision=2},CancellationToken.None));Assert.Equal("revision_boundary",old.Reason);Assert.True(old.Coverage.HasVisibilityGap);
        Assert.NotNull((await reader.GetAsync(request with {TargetRevision=2},CancellationToken.None))!.Summary);
        var service=new TempDbWindowHistoryService(reader);var mixed=new AuthorizationContext(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(other)])),new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new(target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ListAsync(mixed,TempDbWindowFixture.List(target,window) with {TargetRevision=2},CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task SourcePartitionLossInvalidatesCursorAndPreservesSummaryWithExplicitMissingFiles()
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture);Guid target=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await TempDbWindowFixture.CommitAsync(db,target,2);await TempDbWindowFixture.CommitAsync(db,target,1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);
        var first=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(TempDbWindowFixture.List(target,window,1),CancellationToken.None));
        var selected=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(TempDbWindowFixture.Exact(target,window,run.Id.Value,1),CancellationToken.None));
        // The source is written through the production fenced writer. This isolated DDL models
        // source partition disappearance only; the existing paired-retention suite proves worker gates.
        await using(var partition=db.DataSource.CreateCommand("SELECT DISTINCT c.relname FROM telemetry.tempdb_file_snapshot s JOIN pg_class c ON c.oid=s.tableoid WHERE s.run_id=@run;"))
        {
            partition.Parameters.AddWithValue("run",run.Id.Value);string name=Assert.IsType<string>(await partition.ExecuteScalarAsync());
            Assert.StartsWith("tempdb_file_snapshot_",name,StringComparison.Ordinal);
            using var quoting=new NpgsqlCommandBuilder();
            await using var detach=db.DataSource.CreateCommand("SET ROLE sqlobserver_migrator;ALTER TABLE telemetry.tempdb_file_snapshot DETACH PARTITION telemetry."+quoting.QuoteIdentifier(name)+";RESET ROLE;");await detach.ExecuteNonQueryAsync();
        }
        Assert.Equal(409,(await Assert.ThrowsAsync<TempDbWindowHistoryReadException>(()=>reader.ListAsync(TempDbWindowFixture.List(target,window,1,first.NextAnchor),CancellationToken.None).AsTask())).StatusCode);
        Assert.Equal(409,(await Assert.ThrowsAsync<TempDbWindowHistoryReadException>(()=>reader.GetAsync(TempDbWindowFixture.Exact(target,window,run.Id.Value,1,selected.NextAnchor),CancellationToken.None).AsTask())).StatusCode);
        var fresh=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(TempDbWindowFixture.Exact(target,window,run.Id.Value),CancellationToken.None));
        Assert.Equal("payload_missing",fresh.Reason);Assert.Equal("partial",fresh.State);Assert.NotNull(fresh.Summary);Assert.Empty(fresh.Items);Assert.Equal(2,fresh.Receipt!.ExpectedFileCount);
        var list=Assert.IsType<TempDbWindowHistoryPage>(await reader.ListAsync(TempDbWindowFixture.List(target,window),CancellationToken.None));Assert.Equal(2,list.Coverage.MissingPayloadRunCount);Assert.True(list.Coverage.HasVisibilityGap);
    }
    [Fact]
    public async Task WholeWindowOverflowRefusesButIndependentExactSourceRemainsReadable()
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture,true);Guid target=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var selected=await TempDbWindowFixture.CommitAsync(db,target,1);
        await using(var seed=db.DataSource.CreateCommand("""
            INSERT INTO telemetry.collection_run_v2(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT gen_random_uuid(),@target,'tempdb.health',1,1,1,1,'test/tempdb-bounded-cohort',gen_random_uuid(),1,decode(repeat('aa',32),'hex'),@at+i*interval '1 microsecond',@at+i*interval '1 microsecond' FROM generate_series(1,10000) i;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            SELECT r.run_id,'timed_out','deadline_exceeded',1,0,1,0,0,0,0,0,0,0,0,false,false,'none',true,0,0,sha256(convert_to(r.run_id::text,'UTF8')),r.started_at FROM telemetry.collection_run_v2 r WHERE r.instance_id=@target AND r.work_key='test/tempdb-bounded-cohort';
            """))
        {seed.Parameters.AddWithValue("target",target);seed.Parameters.AddWithValue("at",window.FromUtc.AddMilliseconds(10));await seed.ExecuteNonQueryAsync();}
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);
        Assert.Equal(422,(await Assert.ThrowsAsync<TempDbWindowHistoryReadException>(()=>reader.ListAsync(TempDbWindowFixture.List(target,window),CancellationToken.None).AsTask())).StatusCode);
        Assert.NotNull((await reader.GetAsync(TempDbWindowFixture.Exact(target,window,selected.Id.Value),CancellationToken.None))!.Summary);
    }
    [Fact]
    public async Task NativeScopePrivilegesAndClosedCursorDoNotExposePrivateSource()
    {
        await using var db=await TempDbWindowFixture.CreateAsync(fixture);Guid target=await TempDbWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await TempDbWindowFixture.CommitAsync(db,target,2);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlTempDbWindowHistoryRepositoryPort(server);var request=TempDbWindowFixture.Exact(target,window,run.Id.Value,1);
        var page=Assert.IsType<TempDbWindowHistorySnapshotPage>(await reader.GetAsync(request,CancellationToken.None));
        Assert.Equal(400,(await Assert.ThrowsAsync<TempDbWindowHistoryReadException>(()=>reader.GetAsync(request with {Cursor=page.NextAnchor! with {AfterFileId=65535}},CancellationToken.None).AsTask())).StatusCode);
        await using var native=await server.OpenConnectionAsync();await using var tx=await native.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var denied=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);SELECT reporting.read_tempdb_window_history(@target,1,@from,@to,1);",native,tx);
        denied.Parameters.AddWithValue("scope",Guid.NewGuid().ToString("D"));denied.Parameters.AddWithValue("target",target);denied.Parameters.AddWithValue("from",window.FromUtc);denied.Parameters.AddWithValue("to",window.ToUtc);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
        await using var grants=db.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_server','reporting.read_tempdb_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
             AND NOT has_function_privilege('sqlobserver_collector','reporting.read_tempdb_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
             AND NOT has_function_privilege('sqlobserver_server','reporting.tempdb_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint)','EXECUTE')
             AND NOT has_table_privilege('sqlobserver_server','telemetry.tempdb_snapshot','SELECT')
             AND NOT has_table_privilege('sqlobserver_server','telemetry.tempdb_file_snapshot','SELECT')
             AND (SELECT bool_and(relrowsecurity AND relforcerowsecurity) FROM pg_class WHERE oid IN('telemetry.tempdb_snapshot'::regclass,'telemetry.tempdb_file_snapshot'::regclass))
             AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace,LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE n.nspname='reporting' AND p.proname IN('tempdb_window_manifest','read_tempdb_window_history') AND a.grantee=0 AND a.privilege_type='EXECUTE');
            """);Assert.True(Assert.IsType<bool>(await grants.ExecuteScalarAsync()));
    }
}

internal static class TempDbWindowFixture
{
    internal static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(30));
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture,bool seal=false)
    {
        var db=await DatabaseFileWindowFixture.CreateAsync(fixture,seal);
        await using var version=db.DataSource.CreateCommand("SHOW server_version_num;");Assert.Equal("180004",Assert.IsType<string>(await version.ExecuteScalarAsync()));return db;
    }
    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase db)
    {
        Guid target=await DatabaseFileWindowFixture.TargetAsync(db);
        await using var enabled=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET enabled=true,next_due_at=statement_timestamp()+interval '1 day' WHERE instance_id=@target AND collector_id='tempdb.health';");enabled.Parameters.AddWithValue("target",target);Assert.Equal(1,await enabled.ExecuteNonQueryAsync());return target;
    }
    internal static TempDbWindowHistoryListRequest List(Guid target,DatabaseFileWindowFixture.Window window,int limit=100,TempDbWindowHistoryCursor? cursor=null)=>new(target,1,window.FromUtc,window.ToUtc,limit,cursor,Timeout);
    internal static TempDbWindowHistorySnapshotRequest Exact(Guid target,DatabaseFileWindowFixture.Window window,Guid run,int limit=100,TempDbWindowHistoryCursor? cursor=null)=>new(target,1,window.FromUtc,window.ToUtc,run,1,limit,cursor,Timeout);
    internal static async Task<DatabaseFileWindowFixture.Run> CommitAsync(RepositoryTestDatabase db,Guid target,int files,CollectorRunOutcome outcome=CollectorRunOutcome.Succeeded,TimeSpan? observedOffset=null)
    {
        await using var collector=db.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        await using(var due=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET next_due_at=statement_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='tempdb.health';")){due.Parameters.AddWithValue("target",target);await due.ExecuteNonQueryAsync();}
        var work=Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems,Timeout),CancellationToken.None)).Items,item=>item.TargetId.Value==target&&item.CollectorId.Value=="tempdb.health");
        var acquired=await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new WorkerLeaseKey($"collector/run/tempdb.health/{target:N}"),new WorkerExecutionId(Guid.NewGuid()),new WorkerLeaseDuration(WorkerLeaseDuration.Maximum),Timeout),CancellationToken.None);
        var lease=Assert.IsType<WorkerLease>(acquired.Lease).Identity;var id=new CollectorRunId(Guid.NewGuid());Assert.Equal(CollectorRunStartStatus.Started,(await runtime.BeginRunAsync(new(work,id,lease,Timeout),CancellationToken.None)).Status);
        bool success=outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial;
        var rows=Enumerable.Range(1,files).Select(index=>new TempDbFileObservation(work.TargetId,work.TargetRevision,index,9007199254740993,9007199254740992,1,TempDbComponentState.Healthy)).ToArray();
        var snapshot=new TempDbSnapshot(work.TargetId,work.TargetRevision,id,work.RepositoryTimeUtc+(observedOffset??TimeSpan.Zero),files==0?OperationalObservationState.NoData:OperationalObservationState.Complete,long.MaxValue,9007199254740993,null,null,rows,outcome==CollectorRunOutcome.Partial);
        var payload=success?new CollectorPayload(operationalHealth:new OperationalHealthPayload(snapshot,files,files*96)):new CollectorPayload();
        var loss=outcome==CollectorRunOutcome.Partial?new CollectorLossEvidence(CollectorLossKind.SourceRowLimit,1,false):CollectorLossEvidence.None;
        var reason=outcome switch {CollectorRunOutcome.Partial=>CollectorRunReason.SourceRowLimit,CollectorRunOutcome.PermissionDenied=>CollectorRunReason.RequiredPermissionMissing,CollectorRunOutcome.TimedOut=>CollectorRunReason.DeadlineExceeded,CollectorRunOutcome.TransientFailure=>CollectorRunReason.TransientTargetFailure,CollectorRunOutcome.PermanentFailure=>CollectorRunReason.PermanentTargetFailure,CollectorRunOutcome.Unsupported=>CollectorRunReason.TargetUnsupported,_=>CollectorRunReason.Completed};
        var summary=new CollectorRunSummary(id,work.TargetId,work.TargetRevision,work.CollectorId,work.CollectorManifestVersion,work.OutputSchemaVersion,outcome,reason,TimeSpan.FromMilliseconds(1),1,new(success?files+(loss.HasLoss?1:0):0,payload.ItemCount,payload.EstimatedSizeBytes,payload.EstimatedSizeBytes),loss);
        var commit=new CommitCollectorRunRequest(work,summary,payload,CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc),lease,Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed,(await runtime.CommitRunAsync(commit,CancellationToken.None)).Status);Assert.Equal(CollectorRunCommitStatus.Replayed,(await runtime.CommitRunAsync(commit,CancellationToken.None)).Status);
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease,Timeout),CancellationToken.None);return new(work,id,lease);
    }
}
