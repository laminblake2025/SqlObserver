using System.Data;
using System.Globalization;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class BackupAvailabilityWindowHistoryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Theory]
    [InlineData("backup",false)] [InlineData("backup",true)] [InlineData("availability",false)] [InlineData("availability",true)]
    public async Task RealCanonicalTypedCommitReplayAndSourcePagesSurviveOriginalSeal(string kind,bool seal)
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture,seal);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,kind);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,3);await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);
        var list=Assert.IsType<BackupAvailabilityWindowHistoryPage>(await reader.ListAsync(BackupAvailabilityWindowFixture.List(kind,target,window),CancellationToken.None));Assert.Equal(1,list.Coverage.CompleteRunCount);Assert.Equal(run.Id.Value,Assert.Single(list.Items).RunId);
        if(kind=="backup")
        {
            var request=BackupAvailabilityWindowFixture.Exact("backup",target,window,run.Id.Value,1);var first=(await reader.GetBackupsAsync(request,CancellationToken.None))!;
            var second=(await reader.GetBackupsAsync(request with {Cursor=first.NextAnchor},CancellationToken.None))!;var third=(await reader.GetBackupsAsync(request with {Cursor=second.NextAnchor},CancellationToken.None))!;
            Assert.Equal("9223372036854775807",Assert.Single(first.Items).SizeBytes);Assert.Equal("9007199254740993",first.Items[0].BackupSetId);Assert.Null(first.Items[0].LastFinishUtc);
            Assert.Equal("2026-10-04T10:00:00.1234560",first.Items[0].SourceLocalFinish);Assert.True(first.Items[0].SourceTimeUnknown);Assert.Equal(first.Receipt,third.Receipt);Assert.Null(third.NextAnchor);
            Assert.Equal(first.CutoffUtc,third.CutoffUtc);Assert.Equal(first.ReadEpoch,third.ReadEpoch);Assert.Equal(first.NextAnchor!.IssuedAtUtc,second.NextAnchor!.IssuedAtUtc);
            Assert.Equal(3,new[]{first.Items[0].DatabaseFingerprint,second.Items[0].DatabaseFingerprint,third.Items[0].DatabaseFingerprint}.Distinct().Count());
        }
        else
        {
            var request=BackupAvailabilityWindowFixture.Exact("replicas",target,window,run.Id.Value,1);var first=(await reader.GetReplicasAsync(request,CancellationToken.None))!;
            var second=(await reader.GetReplicasAsync(request with {Cursor=first.NextAnchor},CancellationToken.None))!;var third=(await reader.GetReplicasAsync(request with {Cursor=second.NextAnchor},CancellationToken.None))!;
            var databases=(await reader.GetDatabasesAsync(request with {Stream="databases",Limit=100,Cursor=null},CancellationToken.None))!;
            Assert.Equal(first.Receipt,databases.Receipt);Assert.Equal(first.ReadEpoch,databases.ReadEpoch);Assert.Equal(3,databases.Items.Count);Assert.Equal(6,first.Receipt!.StoredRowCount);Assert.Null(third.NextAnchor);
            Assert.Equal(first.CutoffUtc,third.CutoffUtc);Assert.Equal(first.ReadEpoch,third.ReadEpoch);Assert.Equal(first.NextAnchor!.IssuedAtUtc,second.NextAnchor!.IssuedAtUtc);
            Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryRequestValidation.Bind(first.NextAnchor,"availability","databases",target,1,window.FromUtc,window.ToUtc,1,run.Id.Value,1));
        }
        if(seal)Assert.Equal(0L,await DatabaseFileWindowFixture.CountAsync(db,"SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;",target));
    }
    [Theory]
    [InlineData("backup",CollectorRunOutcome.PermissionDenied)] [InlineData("backup",CollectorRunOutcome.TimedOut)] [InlineData("backup",CollectorRunOutcome.TransientFailure)]
    [InlineData("availability",CollectorRunOutcome.PermissionDenied)] [InlineData("availability",CollectorRunOutcome.Unsupported)] [InlineData("availability",CollectorRunOutcome.PermanentFailure)]
    public async Task NewestFailureIsRetainedWithoutOlderSuccessSubstitution(string kind,CollectorRunOutcome outcome)
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,kind);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,1);var failed=await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,0,outcome);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);var list=(await reader.ListAsync(BackupAvailabilityWindowFixture.List(kind,target,window),CancellationToken.None))!;
        Assert.Equal(failed.Id.Value,list.Items[0].RunId);Assert.Equal("collection_failed",list.Items[0].Reason);Assert.Equal(1,list.Coverage.FailedRunCount);Assert.Equal(0,list.Coverage.MissingPayloadRunCount);
        if(kind=="backup"){var exact=(await reader.GetBackupsAsync(BackupAvailabilityWindowFixture.Exact("backup",target,window,failed.Id.Value),CancellationToken.None))!;Assert.NotNull(exact.Receipt);Assert.Empty(exact.Items);Assert.Equal("collection_failed",exact.Reason);}
        else{var exact=(await reader.GetReplicasAsync(BackupAvailabilityWindowFixture.Exact("replicas",target,window,failed.Id.Value),CancellationToken.None))!;Assert.NotNull(exact.Receipt);Assert.Empty(exact.Items);Assert.Equal("collection_failed",exact.Reason);}
    }
    [Theory]
    [InlineData("backup")] [InlineData("availability")]
    public async Task KnownEmptyAndLossHaveDistinctActualAccounting(string kind)
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,kind);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var empty=await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,0);await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,1,CollectorRunOutcome.Partial);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);var page=(await reader.ListAsync(BackupAvailabilityWindowFixture.List(kind,target,window),CancellationToken.None))!;
        Assert.Equal(1,page.Coverage.EmptyRunCount);Assert.Equal(1,page.Coverage.LostRunCount);Assert.Equal(1,page.Coverage.CompleteRunCount);var receipt=Assert.Single(page.Items,item=>item.RunId==empty.Id.Value);Assert.Equal("observed_empty",receipt.Reason);Assert.Equal(6,receipt.SourceState);Assert.Null(receipt.MonitorObservedAtUtc);
        Assert.Equal("collection_loss",page.Items[0].Reason);Assert.Equal(1,page.Items[0].MinimumLostItems);
    }
    [Fact]
    public async Task FixedCutoffScopeRevisionAndClosedAnchorsRemainIndependent()
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture,true);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,"backup"),other=await BackupAvailabilityWindowFixture.TargetAsync(db,"backup");var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var selected=await BackupAvailabilityWindowFixture.CommitAsync(db,target,"backup",2);await BackupAvailabilityWindowFixture.CommitAsync(db,target,"backup",1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);var request=BackupAvailabilityWindowFixture.List("backup",target,window,1);var first=(await reader.ListAsync(request,CancellationToken.None))!;
        await BackupAvailabilityWindowFixture.CommitAsync(db,target,"backup",0,CollectorRunOutcome.PermissionDenied);var tail=(await reader.ListAsync(request with {Cursor=first.NextAnchor},CancellationToken.None))!;Assert.Equal(first.ReadEpoch,tail.ReadEpoch);Assert.Equal(selected.Id.Value,Assert.Single(tail.Items).RunId);
        var exactRequest=BackupAvailabilityWindowFixture.Exact("backup",target,window,selected.Id.Value,1);var exact=(await reader.GetBackupsAsync(exactRequest,CancellationToken.None))!;
        Assert.Equal(400,(await Assert.ThrowsAsync<BackupAvailabilityWindowHistoryReadException>(()=>reader.GetBackupsAsync(exactRequest with {Cursor=exact.NextAnchor! with {AfterFirstKey=new('f',64)}},CancellationToken.None).AsTask())).StatusCode);
        Assert.Equal(409,(await Assert.ThrowsAsync<BackupAvailabilityWindowHistoryReadException>(()=>reader.GetBackupsAsync(exactRequest with {Cursor=exact.NextAnchor! with {IssuedAtUtc=exact.NextAnchor.IssuedAtUtc.AddMinutes(-16),CutoffUtc=exact.CutoffUtc.AddMinutes(-16)}},CancellationToken.None).AsTask())).StatusCode);
        var missing=(await reader.GetBackupsAsync(exactRequest with {RunId=Guid.NewGuid(),Cursor=null},CancellationToken.None))!;Assert.Equal("snapshot_not_retained",missing.Reason);Assert.Empty(missing.Items);
        var outside=(await reader.GetBackupsAsync(exactRequest with {FromUtc=exact.Receipt!.CompletedAtUtc.AddTicks(10),Cursor=null},CancellationToken.None))!;Assert.Equal("snapshot_outside_window",outside.Reason);
        await using(var update=db.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2,updated_at=statement_timestamp() WHERE instance_id=@target;")){update.Parameters.AddWithValue("target",target);await update.ExecuteNonQueryAsync();}
        Assert.Equal(409,(await Assert.ThrowsAsync<BackupAvailabilityWindowHistoryReadException>(()=>reader.ListAsync(request,CancellationToken.None).AsTask())).StatusCode);
        var current=(await reader.ListAsync(request with {TargetRevision=2},CancellationToken.None))!;Assert.Equal("revision_boundary",current.Reason);Assert.True(current.Coverage.HasVisibilityGap);
        Assert.NotNull((await reader.GetBackupsAsync(exactRequest with {TargetRevision=2},CancellationToken.None))!.Receipt);
        var mixed=new SqlObserver.Domain.Authorization.AuthorizationContext(new("S-1-5-21-1234"),SqlObserver.Domain.Authorization.AuthorizationPrincipalState.Active,[new(SqlObserver.Domain.Authorization.ApplicationRole.Viewer,SqlObserver.Domain.Authorization.TargetAuthorizationScope.ForTargets([new(other)])),new(SqlObserver.Domain.Authorization.ApplicationRole.Auditor,SqlObserver.Domain.Authorization.TargetAuthorizationScope.ForTargets([new(target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new BackupAvailabilityWindowHistoryService(reader).ListAsync(mixed,request with {TargetRevision=2},CancellationToken.None).AsTask());
    }
    [Theory]
    [InlineData("backup")] [InlineData("availability")]
    public async Task RetainedSourceDisappearanceChangesEpochAndCannotClaimEmpty(string kind)
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,kind);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,1);var run=await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,2);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);var request=BackupAvailabilityWindowFixture.List(kind,target,window,1);var page=(await reader.ListAsync(request,CancellationToken.None))!;
        string parent=kind=="backup"?"backup_status_snapshot":"availability_group_replica_snapshot";
        // Isolated disappearance modeling; authorized retention-worker gates are exercised in the existing retention suites.
        await using(var partition=db.DataSource.CreateCommand("SELECT DISTINCT c.relname FROM telemetry."+parent+" s JOIN pg_class c ON c.oid=s.tableoid WHERE s.run_id=@run;"))
        {partition.Parameters.AddWithValue("run",run.Id.Value);string name=Assert.IsType<string>(await partition.ExecuteScalarAsync());Assert.StartsWith(parent+"_",name,StringComparison.Ordinal);using var quote=new NpgsqlCommandBuilder();await using var detach=db.DataSource.CreateCommand("SET ROLE sqlobserver_migrator;ALTER TABLE telemetry."+parent+" DETACH PARTITION telemetry."+quote.QuoteIdentifier(name)+";RESET ROLE;");await detach.ExecuteNonQueryAsync();}
        Assert.Equal(409,(await Assert.ThrowsAsync<BackupAvailabilityWindowHistoryReadException>(()=>reader.ListAsync(request with {Cursor=page.NextAnchor},CancellationToken.None).AsTask())).StatusCode);
        var fresh=(await reader.ListAsync(request,CancellationToken.None))!;Assert.Equal("payload_missing",fresh.Items[0].Reason);Assert.Equal(2,fresh.Coverage.MissingPayloadRunCount);Assert.True(fresh.Coverage.HasVisibilityGap);
    }
    [Fact]
    public async Task CadenceChangesOnlyWindowCoverageNotSelectedReceipt()
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,"availability");var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await BackupAvailabilityWindowFixture.CommitAsync(db,target,"availability",1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);var before=(await reader.ListAsync(BackupAvailabilityWindowFixture.List("availability",target,window),CancellationToken.None))!;
        await using(var update=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET collection_interval=interval '1 minute' WHERE instance_id=@target AND collector_id='availability-groups.health';")){update.Parameters.AddWithValue("target",target);await update.ExecuteNonQueryAsync();}
        var exact=(await reader.GetReplicasAsync(BackupAvailabilityWindowFixture.Exact("replicas",target,window,run.Id.Value),CancellationToken.None))!;var after=(await reader.ListAsync(BackupAvailabilityWindowFixture.List("availability",target,window),CancellationToken.None))!;
        Assert.Equal(before.Items[0],exact.Receipt);Assert.Equal(after.Items[0],exact.Receipt);Assert.NotEqual(before.ReadEpoch,after.ReadEpoch);
    }
    [Fact]
    public async Task NativeScopeAndPrivilegesDoNotGrantPrivateSourceOrLedgerReads()
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,"backup");var window=await DatabaseFileWindowFixture.WindowAsync(db);await BackupAvailabilityWindowFixture.CommitAsync(db,target,"backup",1);
        await using var server=db.CreateServerDataSource();await using var connection=await server.OpenConnectionAsync();await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var denied=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);SELECT reporting.read_backup_availability_window_history('backup','receipts',@target,1,@from,@to,1);",connection,tx);
        denied.Parameters.AddWithValue("scope",Guid.NewGuid().ToString("D"));denied.Parameters.AddWithValue("target",target);denied.Parameters.AddWithValue("from",window.FromUtc);denied.Parameters.AddWithValue("to",window.ToUtc);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
        await using var grants=db.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_server','reporting.read_backup_availability_window_history(text,text,uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
             AND NOT has_function_privilege('sqlobserver_collector','reporting.read_backup_availability_window_history(text,text,uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
             AND NOT has_table_privilege('sqlobserver_server','telemetry.m9_commit_replay','SELECT')
             AND NOT has_table_privilege('sqlobserver_server','telemetry.backup_status_snapshot','SELECT')
             AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace,LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE n.nspname='reporting' AND p.proname IN('backup_availability_window_manifest','backup_availability_window_rows','read_backup_availability_window_history') AND a.grantee=0 AND a.privilege_type='EXECUTE');
            """);Assert.True(Assert.IsType<bool>(await grants.ExecuteScalarAsync()));
    }
    [Fact]
    public async Task UnsupportedNewerCanonicalSourceKeepsTrueExpectedCountWithoutLegacyMissingInference()
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture,true);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,"backup");var window=await DatabaseFileWindowFixture.WindowAsync(db);
        await BackupAvailabilityWindowFixture.CommitAsync(db,target,"backup",1);await BackupAvailabilityWindowFixture.SyntheticAttemptsAsync(db,target,window,"backup",1,2,20000);
        await using var server=db.CreateServerDataSource();var page=(await new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server).ListAsync(BackupAvailabilityWindowFixture.List("backup",target,window),CancellationToken.None))!;
        var row=Assert.Single(page.Items,item=>item.CollectorVersion==2);Assert.Equal(20000,row.ExpectedRowCount);Assert.Equal("source_schema_unsupported",row.Reason);Assert.Equal(0,page.Coverage.MissingPayloadRunCount);
    }
    [Fact]
    public async Task WholeWindowRunOverflowDoesNotBlockIndependentExact()
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture,true);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,"backup");var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await BackupAvailabilityWindowFixture.CommitAsync(db,target,"backup",1);
        await BackupAvailabilityWindowFixture.SyntheticAttemptsAsync(db,target,window,"backup",20480);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);
        Assert.Equal(422,(await Assert.ThrowsAsync<BackupAvailabilityWindowHistoryReadException>(()=>reader.ListAsync(BackupAvailabilityWindowFixture.List("backup",target,window),CancellationToken.None).AsTask())).StatusCode);
        Assert.Single((await reader.GetBackupsAsync(BackupAvailabilityWindowFixture.Exact("backup",target,window,run.Id.Value),CancellationToken.None))!.Items);
    }
    [Fact]
    public async Task MissingLegacyReplayIsExplicitAndNeverBecomesKnownEmpty()
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture,true);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,"backup");var window=await DatabaseFileWindowFixture.WindowAsync(db);
        await BackupAvailabilityWindowFixture.SyntheticAttemptsAsync(db,target,window,"backup",1,successful:true);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);var page=(await reader.ListAsync(BackupAvailabilityWindowFixture.List("backup",target,window),CancellationToken.None))!;
        var row=Assert.Single(page.Items);Assert.Equal("source_header_missing",row.Reason);Assert.Null(row.SourceState);Assert.Null(row.MonitorObservedAtUtc);Assert.Equal(1,page.Coverage.MissingPayloadRunCount);Assert.Equal(0,page.Coverage.EmptyRunCount);
        var exact=(await reader.GetBackupsAsync(BackupAvailabilityWindowFixture.Exact("backup",target,window,row.RunId),CancellationToken.None))!;Assert.Equal(row,exact.Receipt);Assert.Empty(exact.Items);
    }
    [Fact]
    public async Task RetainedRowOverflowRefusesWindowBeforeHashingButExactRetainsOwnRun()
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture,true);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,"backup");var window=await DatabaseFileWindowFixture.WindowAsync(db);var selected=await BackupAvailabilityWindowFixture.CommitAsync(db,target,"backup",1);
        await BackupAvailabilityWindowFixture.SyntheticAttemptsAsync(db,target,window,"backup",164,expected:1537,successful:true);
        // Synthetic bounded source burst, not a claim that these rows passed the fenced writer.
        await using(var bulk=db.DataSource.CreateCommand("""
            INSERT INTO telemetry.backup_status_snapshot(instance_id,target_revision,run_id,observed_at,database_fingerprint,backup_kind,source_time_unknown,coverage)
            SELECT @target,1,r.run_id,r.started_at,sha256(convert_to(i::text,'UTF8')),1,false,2
            FROM telemetry.collection_run_v2 r CROSS JOIN generate_series(1,1537) i
            WHERE r.instance_id=@target AND r.work_key='test/legacy-bounded-cohort';
            """)){bulk.Parameters.AddWithValue("target",target);await bulk.ExecuteNonQueryAsync();}
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(server);
        Assert.Equal(422,(await Assert.ThrowsAsync<BackupAvailabilityWindowHistoryReadException>(()=>reader.ListAsync(BackupAvailabilityWindowFixture.List("backup",target,window),CancellationToken.None).AsTask())).StatusCode);
        Assert.Single((await reader.GetBackupsAsync(BackupAvailabilityWindowFixture.Exact("backup",target,window,selected.Id.Value),CancellationToken.None))!.Items);
    }
}

internal static class BackupAvailabilityWindowFixture
{
    internal static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(30));
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture,bool seal=false)
    {var db=await DatabaseFileWindowFixture.CreateAsync(fixture,seal);await using var version=db.DataSource.CreateCommand("SHOW server_version_num;");Assert.Equal("180004",Assert.IsType<string>(await version.ExecuteScalarAsync()));return db;}
    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase db,string kind)
    {
        Guid target=await DatabaseFileWindowFixture.TargetAsync(db);await using var command=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET enabled=true,next_due_at=statement_timestamp()+interval '1 day' WHERE instance_id=@target AND collector_id=@collector;");command.Parameters.AddWithValue("target",target);command.Parameters.AddWithValue("collector",kind=="backup"?"backups.status":"availability-groups.health");Assert.Equal(1,await command.ExecuteNonQueryAsync());return target;
    }
    internal static BackupAvailabilityWindowHistoryListRequest List(string kind,Guid target,DatabaseFileWindowFixture.Window window,int limit=100,BackupAvailabilityWindowHistoryCursor? cursor=null)=>new(kind,target,1,window.FromUtc,window.ToUtc,limit,cursor,Timeout);
    internal static BackupAvailabilityWindowHistorySnapshotRequest Exact(string stream,Guid target,DatabaseFileWindowFixture.Window window,Guid run,int limit=100,BackupAvailabilityWindowHistoryCursor? cursor=null)=>new(stream,target,1,window.FromUtc,window.ToUtc,run,1,limit,cursor,Timeout);
    internal static async Task<DatabaseFileWindowFixture.Run> CommitAsync(RepositoryTestDatabase db,Guid target,string kind,int count,CollectorRunOutcome outcome=CollectorRunOutcome.Succeeded)
    {
        string collectorId=kind=="backup"?"backups.status":"availability-groups.health";
        await using var collector=db.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        await using(var due=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET next_due_at=statement_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id=@collector;")){due.Parameters.AddWithValue("target",target);due.Parameters.AddWithValue("collector",collectorId);await due.ExecuteNonQueryAsync();}
        var work=Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems,Timeout),CancellationToken.None)).Items,item=>item.TargetId.Value==target&&item.CollectorId.Value==collectorId);
        var acquired=await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new WorkerLeaseKey($"collector/run/{collectorId}/{target:N}"),new WorkerExecutionId(Guid.NewGuid()),new WorkerLeaseDuration(WorkerLeaseDuration.Maximum),Timeout),CancellationToken.None);
        var lease=Assert.IsType<WorkerLease>(acquired.Lease).Identity;var id=new CollectorRunId(Guid.NewGuid());Assert.Equal(CollectorRunStartStatus.Started,(await runtime.BeginRunAsync(new(work,id,lease,Timeout),CancellationToken.None)).Status);
        bool success=outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial;bool partial=outcome==CollectorRunOutcome.Partial;
        IOperationalHealthSnapshot snapshot;
        if(kind=="backup")
        {
            var rows=Enumerable.Range(1,count).Select(index=>new BackupStatusObservation(work.TargetId,work.TargetRevision,index.ToString("x64",CultureInfo.InvariantCulture),BackupKind.Full,null,new DateTime(2026,10,4,10,0,0,DateTimeKind.Unspecified).AddTicks(1234560),true,long.MaxValue,false,true,false,BackupCoverage.Complete){BackupSetId=9007199254740993}).ToArray();
            snapshot=new BackupStatusSnapshot(work.TargetId,work.TargetRevision,id,work.RepositoryTimeUtc,count==0?OperationalObservationState.NoData:OperationalObservationState.Complete,rows,count+(partial?1:0),partial);
        }
        else
        {
            var replicas=Enumerable.Range(1,count).Select(index=>new AvailabilityReplicaObservation(work.TargetId,work.TargetRevision,new('a',64),index.ToString("x64",CultureInfo.InvariantCulture),"SECONDARY","ONLINE","CONNECTED",AvailabilityVisibilityScope.SecondaryLocalOnly,!partial)).ToArray();
            var databases=Enumerable.Range(1,count).Select(index=>new AvailabilityDatabaseObservation(work.TargetId,work.TargetRevision,new('a',64),index.ToString("x64",CultureInfo.InvariantCulture),"SYNCHRONIZING","ONLINE",AvailabilityVisibilityScope.SecondaryLocalOnly,!partial)).ToArray();
            snapshot=new AvailabilityGroupsSnapshot(work.TargetId,work.TargetRevision,id,work.RepositoryTimeUtc,count==0?OperationalObservationState.NoData:partial?OperationalObservationState.Degraded:OperationalObservationState.Complete,AvailabilityVisibilityScope.SecondaryLocalOnly,replicas,databases,partial);
        }
        int items=kind=="backup"?count:count*2;var payload=success?new CollectorPayload(operationalHealth:new OperationalHealthPayload(snapshot,items,items*160)):new CollectorPayload();
        var loss=partial?new CollectorLossEvidence(CollectorLossKind.SourceRowLimit,1,false):CollectorLossEvidence.None;
        var reason=outcome switch{CollectorRunOutcome.Partial=>CollectorRunReason.SourceRowLimit,CollectorRunOutcome.PermissionDenied=>CollectorRunReason.RequiredPermissionMissing,CollectorRunOutcome.TimedOut=>CollectorRunReason.DeadlineExceeded,CollectorRunOutcome.TransientFailure=>CollectorRunReason.TransientTargetFailure,CollectorRunOutcome.PermanentFailure=>CollectorRunReason.PermanentTargetFailure,CollectorRunOutcome.Unsupported=>CollectorRunReason.TargetUnsupported,_=>CollectorRunReason.Completed};
        var summary=new CollectorRunSummary(id,work.TargetId,work.TargetRevision,work.CollectorId,work.CollectorManifestVersion,work.OutputSchemaVersion,outcome,reason,TimeSpan.FromMilliseconds(1),1,new(success?items+(partial?1:0):0,payload.ItemCount,payload.EstimatedSizeBytes,payload.EstimatedSizeBytes),loss);
        var circuit=CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc);
        if(outcome is CollectorRunOutcome.TimedOut or CollectorRunOutcome.TransientFailure)
        {
            await using var contract=db.DataSource.CreateCommand("SELECT circuit_failure_threshold,circuit_open_interval FROM control.collector_contract WHERE collector_id=@collector AND collector_version=@version;");contract.Parameters.AddWithValue("collector",collectorId);contract.Parameters.AddWithValue("version",work.CollectorManifestVersion);
            await using var policy=await contract.ExecuteReaderAsync();Assert.True(await policy.ReadAsync());int failures=checked(work.Circuit.ConsecutiveFailures+1);bool open=work.Circuit.State==CollectorCircuitState.HalfOpen||failures>=policy.GetInt32(0);
            circuit=new(open?CollectorCircuitState.Open:CollectorCircuitState.Closed,failures,work.RepositoryTimeUtc,open?work.RepositoryTimeUtc+policy.GetTimeSpan(1):null);
        }
        var request=new CommitCollectorRunRequest(work,summary,payload,circuit,lease,Timeout);Assert.Equal(CollectorRunCommitStatus.Committed,(await runtime.CommitRunAsync(request,CancellationToken.None)).Status);Assert.Equal(CollectorRunCommitStatus.Replayed,(await runtime.CommitRunAsync(request,CancellationToken.None)).Status);
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease,Timeout),CancellationToken.None);return new(work,id,lease);
    }
    internal static async Task SyntheticAttemptsAsync(RepositoryTestDatabase db,Guid target,DatabaseFileWindowFixture.Window window,string kind,int count,int version=1,int expected=0,bool successful=false)
    {
        // Explicit synthetic malformed/overflow canonical metadata; not a production source-write claim.
        await using var command=db.DataSource.CreateCommand("""
            INSERT INTO telemetry.collection_run_v2(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT gen_random_uuid(),@target,@collector,@version,@version,1,1,'test/legacy-bounded-cohort',gen_random_uuid(),1,decode(repeat('aa',32),'hex'),@at+i*interval '1 microsecond',@at+i*interval '1 microsecond' FROM generate_series(1,@count) i;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            SELECT r.run_id,CASE WHEN @version=1 AND NOT @success THEN 'timed_out' ELSE 'succeeded' END,CASE WHEN @version=1 AND NOT @success THEN 'deadline_exceeded' ELSE 'completed' END,1,0,1,@expected,@expected,@expected,0,0,0,0,0,false,false,'none',true,0,0,sha256(convert_to(r.run_id::text,'UTF8')),r.started_at FROM telemetry.collection_run_v2 r WHERE r.instance_id=@target AND r.work_key='test/legacy-bounded-cohort';
            """);command.Parameters.AddWithValue("target",target);command.Parameters.AddWithValue("collector",kind=="backup"?"backups.status":"availability-groups.health");command.Parameters.AddWithValue("version",version);command.Parameters.AddWithValue("count",count);command.Parameters.AddWithValue("expected",expected);command.Parameters.AddWithValue("success",successful);command.Parameters.AddWithValue("at",window.FromUtc.AddMilliseconds(10));await command.ExecuteNonQueryAsync();
    }
}
