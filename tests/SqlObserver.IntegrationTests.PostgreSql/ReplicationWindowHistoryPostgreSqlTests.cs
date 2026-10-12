using System.Data;
using System.Globalization;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class ReplicationWindowHistoryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CanonicalCommitReplayPreservesTwoSubscriptionsOfOnePublicationAfterOriginalSeal(bool seal)
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture,seal);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await ReplicationWindowFixture.CommitAsync(db,target,2);await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);
        var list=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(ReplicationWindowFixture.List(target,window),CancellationToken.None));
        Assert.Equal(1,list.Coverage.CompleteRunCount);Assert.Equal(2,list.Coverage.RetainedRowCount);Assert.Equal(run.Id.Value,Assert.Single(list.Items).RunId);
        var request=ReplicationWindowFixture.Exact(target,window,run.Id.Value,1);var first=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(request,CancellationToken.None));
        var second=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(request with {Cursor=first.NextAnchor},CancellationToken.None));
        var one=Assert.Single(first.Items);var two=Assert.Single(second.Items);Assert.NotEqual(one.RowIdentity,two.RowIdentity);Assert.NotEqual(one.SubscriptionFingerprint,two.SubscriptionFingerprint);
        Assert.True(string.CompareOrdinal(one.RowIdentity,two.RowIdentity)<0);Assert.Equal("2000000000",one.PendingCommands);Assert.Equal("healthy",one.ReportedStatus);
        Assert.Null(one.Topology);Assert.Null(one.RatePerSecond);Assert.Null(one.LastSuccessAgeSeconds);Assert.Equal("monitor_capture",first.ObservationTimeBasis);Assert.Equal("legacy_unqualified",first.StatusQualification);
        Assert.Equal(first.Receipt,second.Receipt);Assert.Equal(first.ReadEpoch,second.ReadEpoch);Assert.Equal(first.CutoffUtc,second.CutoffUtc);Assert.Null(second.NextAnchor);
        Assert.Equal(first.Receipt!.MonitorObservedAtUtc,one.MonitorObservedAtUtc);Assert.NotEqual(one.MonitorObservedAtUtc,one.CollectedAtUtc);
        if(seal)Assert.Equal(0L,await DatabaseFileWindowFixture.CountAsync(db,"SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;",target));
    }
    [Theory]
    [InlineData(CollectorRunOutcome.PermissionDenied)] [InlineData(CollectorRunOutcome.TimedOut)] [InlineData(CollectorRunOutcome.TransientFailure)] [InlineData(CollectorRunOutcome.PermanentFailure)] [InlineData(CollectorRunOutcome.Unsupported)]
    public async Task NewestFailedAttemptNeverFallsBackToOlderSuccessfulPayload(CollectorRunOutcome outcome)
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        await ReplicationWindowFixture.CommitAsync(db,target,1);var failed=await ReplicationWindowFixture.CommitAsync(db,target,0,outcome);await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);
        var page=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(ReplicationWindowFixture.List(target,window),CancellationToken.None));Assert.Equal(failed.Id.Value,page.Items[0].RunId);Assert.Equal(1,page.Coverage.FailedRunCount);
        var exact=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,failed.Id.Value),CancellationToken.None));Assert.Equal("collection_failed",exact.Reason);Assert.NotNull(exact.Receipt);Assert.Empty(exact.Items);
    }
    [Fact]
    public async Task EmptyPartialAndUnknownReportedStatusHaveDistinctSourceAccounting()
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var empty=await ReplicationWindowFixture.CommitAsync(db,target,0);var lost=await ReplicationWindowFixture.CommitAsync(db,target,1,CollectorRunOutcome.Partial);var unknown=await ReplicationWindowFixture.CommitAsync(db,target,1,status:ReplicationStatus.Unknown);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);
        var page=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(ReplicationWindowFixture.List(target,window),CancellationToken.None));Assert.Equal(1,page.Coverage.EmptyRunCount);Assert.Equal(1,page.Coverage.LostRunCount);Assert.Equal(0,page.Coverage.FailedRunCount);
        var a=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,empty.Id.Value),CancellationToken.None));Assert.Equal("observed_empty",a.Reason);Assert.Null(a.Receipt!.MonitorObservedAtUtc);Assert.Empty(a.Items);
        var b=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,lost.Id.Value),CancellationToken.None));Assert.Equal("collection_loss",b.Reason);Assert.Single(b.Items);
        var c=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,unknown.Id.Value),CancellationToken.None));Assert.Equal("available",c.State);Assert.False(Assert.Single(c.Items).StateAvailable);Assert.Equal("unknown",c.Items[0].ReportedStatus);
    }
    [Fact]
    public async Task ChangedCadenceOnlyChangesWindowCoverageNotSelectedReceipt()
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await ReplicationWindowFixture.CommitAsync(db,target,1);
        await using(var schedule=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET schedule_revision=schedule_revision+1,updated_at=statement_timestamp() WHERE instance_id=@target AND collector_id='replication.health';")){schedule.Parameters.AddWithValue("target",target);await schedule.ExecuteNonQueryAsync();}
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);var service=new ReplicationWindowHistoryService(reader);
        var auth=ReplicationWindowFixture.Auth(target);var page=Assert.IsType<ReplicationWindowHistoryPage>(await service.ListAsync(auth,ReplicationWindowFixture.List(target,window),CancellationToken.None));
        var exact=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await service.GetAsync(auth,ReplicationWindowFixture.Exact(target,window,run.Id.Value),CancellationToken.None));
        Assert.Equal("partial",page.State);Assert.True(page.Coverage.HasVisibilityGap);Assert.Equal(1,page.Coverage.CompleteRunCount);Assert.Equal(Assert.Single(page.Items),exact.Receipt);Assert.Equal("observed_rows",exact.Reason);
    }
    [Fact]
    public async Task MonitorClockAndPartialVisibilityRemainExplicitWithoutInventedSourceFields()
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var skewed=await ReplicationWindowFixture.CommitAsync(db,target,1,observedOffset:TimeSpan.FromMinutes(-10));var local=await ReplicationWindowFixture.CommitAsync(db,target,1,coverage:ReplicationCoverage.LocalSummary);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);var service=new ReplicationWindowHistoryService(reader);
        var a=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await service.GetAsync(ReplicationWindowFixture.Auth(target),ReplicationWindowFixture.Exact(target,window,skewed.Id.Value),CancellationToken.None));
        Assert.Equal("source_clock_skew",a.Reason);Assert.Equal(skewed.Work.RepositoryTimeUtc.AddMinutes(-10),Assert.Single(a.Items).MonitorObservedAtUtc);Assert.NotEqual(a.Receipt!.CompletedAtUtc,a.Items[0].MonitorObservedAtUtc);
        var b=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await service.GetAsync(ReplicationWindowFixture.Auth(target),ReplicationWindowFixture.Exact(target,window,local.Id.Value),CancellationToken.None));
        Assert.Equal("source_partial",b.Reason);Assert.Equal("local_summary",Assert.Single(b.Items).SourceCoverage);Assert.Equal("partial",b.State);Assert.Null(b.Items[0].RatePerSecond);
    }
    [Fact]
    public async Task FixedCutoffIgnoresNewCompletionsAndExactEpochIsIndependent()
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var one=await ReplicationWindowFixture.CommitAsync(db,target,2);await ReplicationWindowFixture.CommitAsync(db,target,1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);var request=ReplicationWindowFixture.List(target,window,1);
        var first=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(request,CancellationToken.None));var exact=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,one.Id.Value,1),CancellationToken.None));
        await ReplicationWindowFixture.CommitAsync(db,target,1);
        var tail=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(request with {Cursor=first.NextAnchor},CancellationToken.None));Assert.Equal(2,tail.Coverage.RunCount);Assert.Equal(first.ReadEpoch,tail.ReadEpoch);
        var current=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(request,CancellationToken.None));Assert.Equal(3,current.Coverage.RunCount);Assert.NotEqual(first.ReadEpoch,current.ReadEpoch);
        var rows=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,one.Id.Value,1,exact.NextAnchor),CancellationToken.None));Assert.Equal(exact.ReadEpoch,rows.ReadEpoch);Assert.Equal(exact.Receipt,rows.Receipt);
    }
    [Fact]
    public async Task RevisionScopeClosedAnchorsAndNativeExpiryPreserveExactSelection()
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db),other=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await ReplicationWindowFixture.CommitAsync(db,target,2);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);var request=ReplicationWindowFixture.Exact(target,window,run.Id.Value,1);var exact=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(request,CancellationToken.None));
        Assert.Equal(400,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.GetAsync(request with {Cursor=exact.NextAnchor! with {AfterRowIdentity=new('f',64)}},CancellationToken.None).AsTask())).StatusCode);
        Assert.Equal(409,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.GetAsync(request with {Cursor=exact.NextAnchor! with {IssuedAtUtc=exact.NextAnchor.IssuedAtUtc.AddMinutes(-16),CutoffUtc=exact.CutoffUtc.AddMinutes(-16)}},CancellationToken.None).AsTask())).StatusCode);
        var absent=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(request with {RunId=Guid.NewGuid(),Cursor=null},CancellationToken.None));Assert.Null(absent.Receipt);Assert.Empty(absent.Items);
        var outside=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(request with {FromUtc=exact.Receipt!.CompletedAtUtc.AddTicks(10)},CancellationToken.None));Assert.Equal("snapshot_outside_window",outside.Reason);
        await using(var change=db.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2,updated_at=statement_timestamp() WHERE instance_id=@target;")){change.Parameters.AddWithValue("target",target);await change.ExecuteNonQueryAsync();}
        Assert.Equal(409,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.ListAsync(ReplicationWindowFixture.List(target,window),CancellationToken.None).AsTask())).StatusCode);
        var old=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(ReplicationWindowFixture.List(target,window) with {TargetRevision=2},CancellationToken.None));Assert.Equal("revision_boundary",old.Reason);Assert.True(old.Coverage.HasVisibilityGap);
        Assert.NotNull((await reader.GetAsync(request with {TargetRevision=2,Cursor=null},CancellationToken.None))!.Receipt);
        var mixed=new AuthorizationContext(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(other)])),new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new(target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new ReplicationWindowHistoryService(reader).GetAsync(mixed,request with {TargetRevision=2,Cursor=null},CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task SourcePartitionDisappearanceInvalidatesCursorsAndExposesMissingPayload()
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await ReplicationWindowFixture.CommitAsync(db,target,2);await ReplicationWindowFixture.CommitAsync(db,target,1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);var first=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(ReplicationWindowFixture.List(target,window,1),CancellationToken.None));
        var selected=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,run.Id.Value,1),CancellationToken.None));
        // Isolated source disappearance only; existing M10 retention tests qualify the leased worker gates.
        await using(var partition=db.DataSource.CreateCommand("SELECT DISTINCT c.relname FROM telemetry.replication_snapshot_v2 s JOIN pg_class c ON c.oid=s.tableoid WHERE s.run_id=@run;"))
        {partition.Parameters.AddWithValue("run",run.Id.Value);string name=Assert.IsType<string>(await partition.ExecuteScalarAsync());Assert.StartsWith("replication_snapshot_v2_",name,StringComparison.Ordinal);using var quote=new NpgsqlCommandBuilder();await using var detach=db.DataSource.CreateCommand("SET ROLE sqlobserver_migrator;ALTER TABLE telemetry.replication_snapshot_v2 DETACH PARTITION telemetry."+quote.QuoteIdentifier(name)+";RESET ROLE;");await detach.ExecuteNonQueryAsync();}
        Assert.Equal(409,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.ListAsync(ReplicationWindowFixture.List(target,window,1,first.NextAnchor),CancellationToken.None).AsTask())).StatusCode);
        Assert.Equal(400,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.GetAsync(ReplicationWindowFixture.Exact(target,window,run.Id.Value,1,selected.NextAnchor),CancellationToken.None).AsTask())).StatusCode);
        var fresh=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,run.Id.Value),CancellationToken.None));Assert.Equal("payload_missing",fresh.Reason);Assert.Empty(fresh.Items);Assert.Equal(2,fresh.Receipt!.ExpectedRowCount);
        Assert.Equal(2,(await reader.ListAsync(ReplicationWindowFixture.List(target,window),CancellationToken.None))!.Coverage.MissingPayloadRunCount);
    }
    [Fact]
    public async Task NativePrivilegesScopeAndCanonicalDependencyCannotBeBypassed()
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await ReplicationWindowFixture.CommitAsync(db,target,1);
        await using var server=db.CreateServerDataSource();await using var native=await server.OpenConnectionAsync();await using var tx=await native.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var denied=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);SELECT reporting.read_replication_window_history(@target,1,@from,@to,1);",native,tx);denied.Parameters.AddWithValue("scope",Guid.NewGuid().ToString("D"));denied.Parameters.AddWithValue("target",target);denied.Parameters.AddWithValue("from",window.FromUtc);denied.Parameters.AddWithValue("to",window.ToUtc);Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
        await using var grants=db.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_server','reporting.read_replication_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector','reporting.read_replication_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
              AND NOT has_function_privilege('sqlobserver_server','reporting.replication_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint)','EXECUTE')
              AND NOT has_table_privilege('sqlobserver_server','telemetry.replication_snapshot_v2','SELECT')
              AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='telemetry.replication_snapshot_v2'::regclass)
              AND EXISTS(SELECT 1 FROM pg_constraint WHERE conname='fk_replication_canonical_run' AND convalidated)
              AND EXISTS(SELECT 1 FROM system.collection_run_reviewed_references() WHERE child_relation='telemetry.replication_snapshot_v2'::regclass AND column_name='run_id')
              AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace,LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE n.nspname='reporting' AND p.proname IN('replication_window_manifest','read_replication_window_history') AND a.grantee=0 AND a.privilege_type='EXECUTE');
            """);Assert.True(Assert.IsType<bool>(await grants.ExecuteScalarAsync()));
        await using var orphan=db.DataSource.CreateCommand("INSERT INTO telemetry.replication_snapshot_v2(instance_id,target_revision,run_id,observed_at,collected_at,topology_fingerprint,role,synchronization_state,visibility_scope,state_available) VALUES(@target,1,gen_random_uuid(),statement_timestamp(),statement_timestamp(),decode(repeat('ff',32),'hex'),'distributor','unknown',1,false);");orphan.Parameters.AddWithValue("target",target);Assert.Equal("23503",(await Assert.ThrowsAsync<PostgresException>(()=>orphan.ExecuteNonQueryAsync())).SqlState);
        await using(var references=db.DataSource.CreateCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);SELECT count(*) FROM system.collection_run_reference_holds(@target,@run) WHERE relation_name='telemetry.replication_snapshot_v2';"))
        {references.Parameters.AddWithValue("scope",target.ToString("D"));references.Parameters.AddWithValue("target",target);references.Parameters.AddWithValue("run",run.Id.Value);await using var rows=await references.ExecuteReaderAsync();Assert.True(await rows.NextResultAsync());Assert.True(await rows.ReadAsync());Assert.True(rows.GetInt64(0)>0);}
        // Dependency inspection intentionally uses a privileged fixed role. Exercise
        // scope RLS with a disposable ordinary role, without changing production grants.
        await using var scoped=await db.DataSource.OpenConnectionAsync();await using var scopeTx=await scoped.BeginTransactionAsync();string probeRole=$"sqlobserver_replication_scope_{Guid.NewGuid():N}";
        await using(var create=new NpgsqlCommand($"CREATE ROLE \"{probeRole}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;GRANT USAGE ON SCHEMA telemetry TO \"{probeRole}\";GRANT SELECT ON telemetry.replication_snapshot_v2 TO \"{probeRole}\";SET LOCAL ROLE \"{probeRole}\";",scoped,scopeTx))await create.ExecuteNonQueryAsync();
        await using(var own=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);SELECT count(*) FROM telemetry.replication_snapshot_v2;",scoped,scopeTx))
        {own.Parameters.AddWithValue("scope",target.ToString("D"));await using var rows=await own.ExecuteReaderAsync();Assert.True(await rows.NextResultAsync());Assert.True(await rows.ReadAsync());Assert.Equal(1L,rows.GetInt64(0));}
        await using(var foreign=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);SELECT count(*) FROM telemetry.replication_snapshot_v2;",scoped,scopeTx))
        {foreign.Parameters.AddWithValue("scope",Guid.NewGuid().ToString("D"));await using var rows=await foreign.ExecuteReaderAsync();Assert.True(await rows.NextResultAsync());Assert.True(await rows.ReadAsync());Assert.Equal(0L,rows.GetInt64(0));}
        await scopeTx.RollbackAsync();
    }
    [Fact]
    public async Task UpgradeRetainsAndDisclosesExistingLegacyUnboundRowsWithoutAssigningACompletion()
    {
        await using var db=await fixture.CreateDatabaseAsync();var migrator=new PostgreSqlMigrationPort(db.DataSource);
        var prefix=await migrator.ApplyPendingAsync(new(362,ReplicationWindowFixture.Timeout),CancellationToken.None);Assert.False(prefix.HasFailures);Assert.Equal(362,prefix.Results[^1].Migration.Number.Value);
        Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        await using(var legacy=db.DataSource.CreateCommand("INSERT INTO telemetry.replication_snapshot_v2(instance_id,target_revision,run_id,observed_at,collected_at,topology_fingerprint,role,synchronization_state,visibility_scope,state_available) VALUES(@target,1,gen_random_uuid(),statement_timestamp(),statement_timestamp(),decode(repeat('aa',32),'hex'),'distributor','unknown',3,false);")){legacy.Parameters.AddWithValue("target",target);await legacy.ExecuteNonQueryAsync();}
        var upgrade=await migrator.ApplyPendingAsync(new(MigrationBatchResult.MaximumResults,ReplicationWindowFixture.Timeout),CancellationToken.None);Assert.False(upgrade.HasFailures);
        await using var server=db.CreateServerDataSource();var page=Assert.IsType<ReplicationWindowHistoryPage>(await new PostgreSqlReplicationWindowHistoryRepositoryPort(server).ListAsync(ReplicationWindowFixture.List(target,window),CancellationToken.None));
        Assert.Equal("source_unbound",page.Reason);Assert.Equal("unavailable",page.State);Assert.Equal(0,page.Coverage.RunCount);Assert.Equal(1,page.Coverage.UnboundSourceRowCount);Assert.True(page.Coverage.HasVisibilityGap);Assert.Empty(page.Items);
        await using var validation=db.DataSource.CreateCommand("SELECT convalidated FROM pg_constraint WHERE conrelid='telemetry.replication_snapshot_v2'::regclass AND conname='fk_replication_canonical_run';");Assert.False(Assert.IsType<bool>(await validation.ExecuteScalarAsync()));
    }
    [Theory]
    [InlineData(20481,0)] [InlineData(123,2048)]
    public async Task SyntheticNegativeCohortsRefuseWholeWindowOverflowButKeepIndependentExact(int runs,int rows)
    {
        await using var db=await ReplicationWindowFixture.CreateAsync(fixture,true);Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var selected=await ReplicationWindowFixture.CommitAsync(db,target,1);
        // Explicit synthetic bound test, not evidence that a production source emitted these attempts.
        await using(var seed=db.DataSource.CreateCommand("""
            INSERT INTO telemetry.collection_run_v2(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT gen_random_uuid(),@target,'replication.health',1,1,1,1,'test/replication-bounded-cohort',gen_random_uuid(),1,decode(repeat('aa',32),'hex'),@at+i*interval '1 microsecond',@at+i*interval '1 microsecond' FROM generate_series(1,@runs) i;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            SELECT r.run_id,'succeeded','completed',1,0,1,@rows,@rows,@rows,0,0,0,0,0,false,false,'none',true,0,0,sha256(convert_to(r.run_id::text,'UTF8')),r.started_at FROM telemetry.collection_run_v2 r WHERE r.instance_id=@target AND r.work_key='test/replication-bounded-cohort';
            INSERT INTO telemetry.replication_snapshot_v2(instance_id,target_revision,run_id,observed_at,collected_at,topology_fingerprint,role,synchronization_state,visibility_scope,state_available)
            SELECT @target,1,r.run_id,r.started_at,r.started_at,sha256(convert_to(i::text,'UTF8')),'distributor','unknown',1,false FROM telemetry.collection_run_v2 r CROSS JOIN generate_series(1,@rows) i WHERE r.instance_id=@target AND r.work_key='test/replication-bounded-cohort';
            """)){seed.Parameters.AddWithValue("target",target);seed.Parameters.AddWithValue("at",window.FromUtc.AddMilliseconds(10));seed.Parameters.AddWithValue("runs",runs);seed.Parameters.AddWithValue("rows",rows);await seed.ExecuteNonQueryAsync();}
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);Assert.Equal(422,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.ListAsync(ReplicationWindowFixture.List(target,window),CancellationToken.None).AsTask())).StatusCode);
        Assert.Single((await reader.GetAsync(ReplicationWindowFixture.Exact(target,window,selected.Id.Value),CancellationToken.None))!.Items);
    }
}

internal static class ReplicationWindowFixture
{
    internal static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(30));
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture,bool seal=false)=>await TempDbWindowFixture.CreateAsync(fixture,seal);
    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase db)
    {
        Guid target=await DatabaseFileWindowFixture.TargetAsync(db);await EnableAsync(db,target);return target;
    }
    internal static async Task EnableAsync(RepositoryTestDatabase db,Guid target)
    {
        await using var enabled=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET enabled=CASE WHEN collector_id='replication.health' THEN true ELSE enabled END,next_due_at=statement_timestamp()+interval '1 day',last_outcome=CASE WHEN collector_id IN('capability.connection','engine.core') THEN 'succeeded' ELSE last_outcome END,last_succeeded_at=CASE WHEN collector_id IN('capability.connection','engine.core') THEN statement_timestamp() ELSE last_succeeded_at END WHERE instance_id=@target;");enabled.Parameters.AddWithValue("target",target);Assert.True(await enabled.ExecuteNonQueryAsync()>0);
    }
    internal static ReplicationWindowHistoryListRequest List(Guid target,DatabaseFileWindowFixture.Window window,int limit=100,ReplicationWindowHistoryCursor? cursor=null)=>new(target,1,window.FromUtc,window.ToUtc,limit,cursor,Timeout);
    internal static ReplicationWindowHistorySnapshotRequest Exact(Guid target,DatabaseFileWindowFixture.Window window,Guid run,int limit=100,ReplicationWindowHistoryCursor? cursor=null)=>new(target,1,window.FromUtc,window.ToUtc,run,1,limit,cursor,Timeout);
    internal static AuthorizationContext Auth(Guid target)=>new(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(target)]))]);
    internal static async Task<DatabaseFileWindowFixture.Run> BeginAsync(RepositoryTestDatabase db,NpgsqlDataSource collector,PostgreSqlCollectorRuntimeRepositoryPort runtime,Guid target)
    {
        await using(var due=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET next_due_at=statement_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='replication.health';")){due.Parameters.AddWithValue("target",target);await due.ExecuteNonQueryAsync();}
        var work=Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems,Timeout),CancellationToken.None)).Items,item=>item.TargetId.Value==target&&item.CollectorId.Value=="replication.health");
        var acquired=await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new WorkerLeaseKey($"collector/run/replication.health/{target:N}"),new WorkerExecutionId(Guid.NewGuid()),new WorkerLeaseDuration(WorkerLeaseDuration.Maximum),Timeout),CancellationToken.None);
        var lease=Assert.IsType<WorkerLease>(acquired.Lease).Identity;var id=new CollectorRunId(Guid.NewGuid());Assert.Equal(CollectorRunStartStatus.Started,(await runtime.BeginRunAsync(new(work,id,lease,Timeout),CancellationToken.None)).Status);return new(work,id,lease);
    }
    internal static async Task<DatabaseFileWindowFixture.Run> CommitAsync(RepositoryTestDatabase db,Guid target,int count,CollectorRunOutcome outcome=CollectorRunOutcome.Succeeded,ReplicationStatus status=ReplicationStatus.Healthy,ReplicationCoverage coverage=ReplicationCoverage.Complete,TimeSpan? observedOffset=null)
    {
        await using var collector=db.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector,new IdentityFingerprintKey(new byte[32]));var run=await BeginAsync(db,collector,runtime,target);var work=run.Work;
        bool success=outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial;
        string publication=new('a',64);var rows=Enumerable.Range(1,count).Select(index=>new ReplicationObservation(work.TargetId,work.TargetRevision,ReplicationTopologyState.Transactional,ReplicationRole.Distributor,publication,index.ToString("x64",CultureInfo.InvariantCulture),status,ReplicationBounds.MaximumPendingCommands,1.25m,9m,new(TimeSpan.FromSeconds(1)),coverage)).ToArray();
        var loss=outcome==CollectorRunOutcome.Partial?new CollectorLossEvidence(CollectorLossKind.SourceRowLimit,1,false):CollectorLossEvidence.None;
        var snapshot=new ReplicationHealthSnapshot(work.TargetId,work.TargetRevision,run.Id,work.RepositoryTimeUtc+(observedOffset??TimeSpan.Zero),loss.HasLoss||coverage!=ReplicationCoverage.Complete?OperationalObservationState.Partial:count==0?OperationalObservationState.NoData:OperationalObservationState.Complete,rows,coverage,count+(loss.HasLoss?1:0),loss.HasLoss);
        var payload=success?new CollectorPayload(operationalHealth:new OperationalHealthPayload(snapshot,count,count*192)):new CollectorPayload();
        var reason=outcome switch {CollectorRunOutcome.Partial=>CollectorRunReason.SourceRowLimit,CollectorRunOutcome.PermissionDenied=>CollectorRunReason.RequiredPermissionMissing,CollectorRunOutcome.TimedOut=>CollectorRunReason.DeadlineExceeded,CollectorRunOutcome.TransientFailure=>CollectorRunReason.TransientTargetFailure,CollectorRunOutcome.PermanentFailure=>CollectorRunReason.PermanentTargetFailure,CollectorRunOutcome.Unsupported=>CollectorRunReason.TargetUnsupported,_=>CollectorRunReason.Completed};
        var summary=new CollectorRunSummary(run.Id,work.TargetId,work.TargetRevision,work.CollectorId,work.CollectorManifestVersion,work.OutputSchemaVersion,outcome,reason,TimeSpan.FromMilliseconds(1),1,new(success?count+(loss.HasLoss?1:0):0,payload.ItemCount,payload.EstimatedSizeBytes,payload.EstimatedSizeBytes),loss);
        var circuit=CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc);
        if(outcome is CollectorRunOutcome.TimedOut or CollectorRunOutcome.TransientFailure)
        {
            await using var contract=db.DataSource.CreateCommand("SELECT circuit_failure_threshold,circuit_open_interval FROM control.collector_contract WHERE collector_id='replication.health' AND collector_version=@version;");contract.Parameters.AddWithValue("version",work.CollectorManifestVersion);
            await using var policy=await contract.ExecuteReaderAsync();Assert.True(await policy.ReadAsync());int failures=checked(work.Circuit.ConsecutiveFailures+1);
            bool open=work.Circuit.State==CollectorCircuitState.HalfOpen||failures>=policy.GetInt32(0);
            circuit=new(open?CollectorCircuitState.Open:CollectorCircuitState.Closed,failures,work.RepositoryTimeUtc,open?work.RepositoryTimeUtc+policy.GetTimeSpan(1):null);
        }
        var commit=new CommitCollectorRunRequest(work,summary,payload,circuit,run.Lease,Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed,(await runtime.CommitRunAsync(commit,CancellationToken.None)).Status);Assert.Equal(CollectorRunCommitStatus.Replayed,(await runtime.CommitRunAsync(commit,CancellationToken.None)).Status);
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(run.Lease,Timeout),CancellationToken.None);return run;
    }
}
