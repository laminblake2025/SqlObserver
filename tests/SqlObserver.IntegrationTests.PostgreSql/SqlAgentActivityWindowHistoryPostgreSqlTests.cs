using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using static SqlObserver.IntegrationTests.PostgreSql.SqlAgentActivityPersistenceTests;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentActivityWindowHistoryPostgreSqlTests(PostgreSql18Fixture fixture,Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(513)]
    public async Task SummariesExcludeJobsAndExactSourceRemainsReadableAfterRevocation(int sourceRows)
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);
        var target=await SeedAsync(database); var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        var run=await SqlAgentActivityHistoryFixture.CommitAsync(database,target,sourceRows,submicrosecond:true);
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var page=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window),CancellationToken.None));
        Assert.Equal(1,page.Coverage.RunCount);Assert.Equal(1,page.Coverage.RetainedReceiptCount);Assert.Equal(1,page.Coverage.RetainedCaptureCount);
        Assert.Equal(sourceRows==513?0:1,page.Coverage.CompleteSourceRunCount);Assert.Equal(0,page.Coverage.FailedRunCount);
        var summary=Assert.Single(page.Items);Assert.Equal(sourceRows,summary.SourceRowsRead);Assert.Equal(Math.Min(sourceRows,512),summary.RetainedJobCount);
        Assert.Equal(sourceRows==513?"source_truncated":"source_complete",summary.Reason);
        string listJson=await SqlAgentActivityHistoryFixture.NativeAsync(database,target,window);
        Assert.DoesNotContain("jobId",listJson,StringComparison.Ordinal);Assert.DoesNotContain("enrollment",listJson,StringComparison.Ordinal);
        await SqlAgentActivityHistoryFixture.RevokeAsync(server,target);
        var exact=Assert.IsType<SqlAgentActivityWindowHistoryExactPage>(await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,run.Id.Value),CancellationToken.None));
        var capture=Assert.IsType<SqlAgentActivityWindowHistoryCapture>(exact.Capture);
        Assert.Equal(sourceRows==513?"SourceTruncated":"ServiceNotRunning",capture.Gap);
        Assert.Equal(Math.Min(sourceRows,512),capture.Jobs.Count);Assert.All(capture.Jobs,job=>Assert.Equal("Unknown",job.CandidateState));
        Assert.Equal(run.Work.RepositoryTimeUtc.AddTicks(7).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",CultureInfo.InvariantCulture),capture.Before.SourceObservedAtUtc);
        if(sourceRows>0){Assert.Equal("2026-11-01T01:30:00.0000000",capture.Jobs[0].StartExecutionLocal);Assert.Equal("2026-11-01T01:20:00.0000000",capture.Jobs[0].StopExecutionLocal);}
        string detail=JsonSerializer.Serialize(exact,SqlAgentActivityHistoryFixture.Json);
        foreach(string privateKey in new[]{"enrollmentId","evidenceSha256","expectedWindowsSid","endpointHost","certificate","definitionSha256","duration","currentStep"})
            Assert.DoesNotContain(privateKey,detail,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1,capture.EnrollmentRevision);Assert.False(capture.ProductionAdmitted);Assert.False(capture.ServiceSessionAssociationQualified);Assert.Equal("Absent",capture.NativeQualification);
    }

    [Fact]
    public async Task CutoffIgnoresNewerCompletionsAndBookkeepingButConfigurationInvalidatesCursor()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);
        var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);var oldest=await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var first=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1),CancellationToken.None));
        var cursor=Assert.IsType<SqlAgentActivityWindowHistoryCursor>(first.NextAnchor);
        await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        var tail=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1,cursor),CancellationToken.None));
        Assert.Equal(oldest.Id.Value,Assert.Single(tail.Items).RunId);Assert.Equal(2,tail.Coverage.RunCount);Assert.Equal(first.ReadEpoch,tail.ReadEpoch);Assert.Equal(first.CutoffUtc,tail.CutoffUtc);
        await SqlAgentActivityHistoryFixture.ExecuteAsync(database,"UPDATE control.collector_schedule SET enabled=false,schedule_revision=schedule_revision+1,updated_at=clock_timestamp() WHERE instance_id=@target AND collector_id='sql-agent.activity-observations'",("target",target.Value));
        var changed=await Assert.ThrowsAsync<SqlAgentActivityWindowHistoryReadException>(async()=>await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1,cursor),CancellationToken.None));
        Assert.Equal(409,changed.StatusCode);
    }

    [Fact]
    public async Task CommitBecomingVisibleAfterCutoffInvalidatesTheOriginalManifest()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);
        var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);await SqlAgentActivityHistoryFixture.CommitAsync(database,target);await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        await using var server=database.CreateServerDataSource();await using var collector=database.CreateCollectorDataSource();
        var enrollment=await SqlAgentActivityHistoryFixture.EnrollmentAsync(database,server,collector,target);var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var pending=await BeginAsync(database,collector,runtime,target);
        await using var connection=await collector.OpenConnectionAsync();await using var transaction=await connection.BeginTransactionAsync();
        await SqlAgentActivityHistoryFixture.StageCommitAsync(connection,transaction,Commit(pending,Capture(pending,enrollment)));
        var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var first=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1),CancellationToken.None));
        Assert.Equal(2,first.Coverage.RunCount);await transaction.CommitAsync();
        var changed=await Assert.ThrowsAsync<SqlAgentActivityWindowHistoryReadException>(async()=>await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1,first.NextAnchor),CancellationToken.None));Assert.Equal(409,changed.StatusCode);
        var fresh=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window),CancellationToken.None));
        Assert.Equal(3,fresh.Coverage.RunCount);Assert.True(fresh.Items[0].CompletedAtUtc<=first.CutoffUtc);
    }

    [Fact]
    public async Task FailedAndRevisionCancelledAttemptsRemainVisibleThroughSealedCanonicalReads()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture,seal:true);var target=await SeedAsync(database);
        var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);var retained=await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        await SqlAgentActivityHistoryFixture.CommitAsync(database,target,outcome:CollectorRunOutcome.PermissionDenied,reason:CollectorRunReason.RequiredPermissionMissing);
        await using var collector=database.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var pending=await BeginAsync(database,collector,runtime,target);
        await SqlAgentActivityHistoryFixture.ExecuteAsync(database,"UPDATE control.observation_target SET revision=2,display_name='Agent history revised',updated_at=clock_timestamp() WHERE instance_id=@target",("target",target.Value));
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var page=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window) with {TargetRevision=2},CancellationToken.None));
        Assert.Equal(3,page.Coverage.RunCount);Assert.Equal(2,page.Coverage.FailedRunCount);Assert.Equal(2,page.Coverage.RetainedReceiptCount);Assert.Equal(1,page.Coverage.RetainedCaptureCount);
        Assert.Equal("revision_boundary",page.Reason);Assert.Equal(pending.Id.Value,page.Items[0].RunId);Assert.Equal("target_revision_changed",page.Items[0].CanonicalReason);
        Assert.False(page.Items[0].ReceiptRetained);Assert.Equal("collection_failed",page.Items[0].Reason);Assert.Null(page.Items[0].SourceRowsRead);
        var exact=Assert.IsType<SqlAgentActivityWindowHistoryExactPage>(await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,retained.Id.Value) with {TargetRevision=2},CancellationToken.None));Assert.NotNull(exact.Capture);Assert.Equal(1,exact.SampleTargetRevision);
    }

    [Fact]
    public async Task ExactMissingAndOutOfWindowSelectionsDoNotSubstituteAnotherObservation()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        var run=await SqlAgentActivityHistoryFixture.CommitAsync(database,target);await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var missing=Assert.IsType<SqlAgentActivityWindowHistoryExactPage>(await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,Guid.NewGuid()),CancellationToken.None));
        Assert.Equal("observation_not_retained",missing.Reason);Assert.Null(missing.Receipt);Assert.Null(missing.Capture);
        var outside=new SqlAgentActivityHistoryFixture.Window(window.FromUtc.AddDays(-2),window.FromUtc.AddDays(-1));
        var detail=Assert.IsType<SqlAgentActivityWindowHistoryExactPage>(await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,outside,run.Id.Value),CancellationToken.None));Assert.Equal("observation_outside_window",detail.Reason);Assert.Null(detail.Capture);
        var empty=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,outside),CancellationToken.None));Assert.Equal("no_completed_runs",empty.Reason);Assert.Empty(empty.Items);
    }

    [Theory]
    [InlineData("epoch")][InlineData("expiry")][InlineData("anchor")][InlineData("unknown")][InlineData("type")]
    public async Task RawCursorRejectsEpochDriftExpiryUnknownFieldsWrongTypesAndForeignAnchors(string mutation)
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        await SqlAgentActivityHistoryFixture.CommitAsync(database,target);await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var page=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1),CancellationToken.None));
        var cursor=JsonSerializer.SerializeToNode(page.NextAnchor,SqlAgentActivityHistoryFixture.Json)!.AsObject();
        switch(mutation){case "epoch":cursor["readEpoch"]=new string('a',64);break;case "expiry":cursor["issuedAtUtc"]=page.RepositoryTimeUtc.AddMinutes(-16);cursor["cutoffUtc"]=page.RepositoryTimeUtc.AddMinutes(-17);cursor["afterCompletedAtUtc"]=window.FromUtc;break;case "anchor":cursor["afterRunId"]=Guid.NewGuid();break;case "unknown":cursor["extra"]=true;break;case "type":cursor["limit"]="1";break;}
        var error=await Assert.ThrowsAsync<PostgresException>(()=>SqlAgentActivityHistoryFixture.NativeAsync(database,target,window,cursor.ToJsonString()));Assert.Equal(mutation is "epoch" or "expiry"?"40001":"22023",error.SqlState);
    }

    [Theory]
    [InlineData("digest")][InlineData("privatePin")][InlineData("date")][InlineData("accounting")][InlineData("job")]
    public async Task CorruptServedEnvelopeOrExactSourceIsRejected(string corruption)
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        var run=await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        string mutate=corruption switch{
            "privatePin"=>"jsonb_set(payload,'{observation,expectedWindowsSid}','\"S-1-5-18\"'::jsonb)",
            "date"=>"jsonb_set(payload,'{observation,before,sourceObservedAtUtc}','\"2026-01-01T24:00:00.0000000Z\"'::jsonb)",
            "job"=>"jsonb_set(payload,'{observation,jobs,0,requestedLocal}','\"2026-01-01T01:00:00.0000000Z\"'::jsonb)",
            "accounting"=>"jsonb_set(payload,'{observation,sourceRowsRead}','3'::jsonb)",_=>"payload"};
        await using(var connection=await database.DataSource.OpenConnectionAsync())await using(var transaction=await connection.BeginTransactionAsync())
        {
            await using var command=new NpgsqlCommand($"""
                SELECT set_config('session_replication_role','replica',true);
                UPDATE telemetry.sql_agent_activity_receipt SET payload={mutate} WHERE run_id=@run;
                UPDATE telemetry.sql_agent_activity_receipt SET completion_digest={(corruption=="digest"?"decode(repeat('ab',32),'hex')":"sha256(convert_to(payload::text,'UTF8'))")} WHERE run_id=@run;
                UPDATE telemetry.collection_run_outcome SET completion_digest=(SELECT completion_digest FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run) WHERE run_id=@run;
                """,connection,transaction);command.Parameters.AddWithValue("run",run.Id.Value);await command.ExecuteNonQueryAsync();await transaction.CommitAsync();
        }
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        // Summary bodies are digest-bound to writer validation and disclose no
        // job fields. This administrator-only mutation rewrites the canonical
        // digest too, proving exact job validation independently of the digest.
        if(corruption!="job")await Assert.ThrowsAsync<InvalidDataException>(async()=>await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,run.Id.Value),CancellationToken.None));
    }

    [Fact]
    public async Task OffPagePayloadIsCheckedOnlyWhenItsSummaryOrExactDetailIsServed()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        var old=await SqlAgentActivityHistoryFixture.CommitAsync(database,target,512);var newest=await SqlAgentActivityHistoryFixture.CommitAsync(database,target,512);
        // Preserve both immutable stored/canonical digest columns. An off-page
        // body mutation must be discovered by full validation when it is served.
        await using(var connection=await database.DataSource.OpenConnectionAsync())await using(var transaction=await connection.BeginTransactionAsync())
        {
            await using var mutate=new NpgsqlCommand("""
                SELECT set_config('session_replication_role','replica',true);
                UPDATE telemetry.sql_agent_activity_receipt SET payload=jsonb_set(payload,'{observation,expectedWindowsSid}','"S-1-5-18"'::jsonb) WHERE run_id=@run
                """,connection,transaction);mutate.Parameters.AddWithValue("run",old.Id.Value);await mutate.ExecuteNonQueryAsync();await transaction.CommitAsync();
        }
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var first=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1),CancellationToken.None));
        Assert.Equal(2,first.Coverage.RunCount);Assert.Equal(newest.Id.Value,Assert.Single(first.Items).RunId);
        Assert.NotNull((await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,newest.Id.Value),CancellationToken.None))!.Capture);
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window,1,first.NextAnchor),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,old.Id.Value),CancellationToken.None));
    }

    [Fact]
    public async Task ScopedEntryPointIsServerOnlyAndPrivateReceiptHelpersAreDenied()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        await SqlAgentActivityHistoryFixture.CommitAsync(database,target);await using var server=database.CreateServerDataSource();
        foreach(string sql in new[]{"SELECT payload FROM telemetry.sql_agent_activity_receipt","SELECT reporting.sql_agent_activity_public_detail('{}'::jsonb)"})
        {await using var forbidden=server.CreateCommand(sql);Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(async()=>await forbidden.ExecuteScalarAsync())).SqlState);}
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>SqlAgentActivityHistoryFixture.NativeAsync(database,target,window,scope:Guid.NewGuid()))).SqlState);
        await using var collector=database.CreateCollectorDataSource();await using var denied=collector.CreateCommand("SELECT reporting.read_sql_agent_activity_window_history(@target,1,@from,@to,1)");
        denied.Parameters.AddWithValue("target",target.Value);denied.Parameters.AddWithValue("from",window.FromUtc);denied.Parameters.AddWithValue("to",window.ToUtc);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(async()=>await denied.ExecuteScalarAsync())).SqlState);
        Assert.Equal(0L,await CountAsync(database,"SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='reporting' AND (p.proname LIKE 'sql_agent_activity_%' OR p.proname='read_sql_agent_activity_window_history') AND EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')"));
    }

    [Fact]
    public async Task MaximumSummaryPageWithMaximumRetainedJobsFitsTheRepositoryDeadline()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        for(int index=0;index<SqlAgentActivityWindowHistoryValidation.MaximumPageSize;index++)
            await SqlAgentActivityHistoryFixture.CommitAsync(database,target,512);
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var request=SqlAgentActivityHistoryFixture.List(target,window) with {Timeout=new(TimeSpan.FromSeconds(5))};
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        var page=Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(request,CancellationToken.None));
        elapsed.Stop();output.WriteLine($"Installed server-role summary read: 100 canonical runs x512 retained jobs, {elapsed.Elapsed.TotalMilliseconds:F1}ms; 5s repository deadline. Source fixtures are synthetic; this measures only this PostgreSQL reader.");
        Assert.Equal(100,page.Coverage.RunCount);Assert.Equal(100,page.Coverage.CompleteSourceRunCount);Assert.Equal(100,page.Items.Count);Assert.Null(page.NextAnchor);
        Assert.All(page.Items,item=>{Assert.Equal(512,item.SourceRowsRead);Assert.Equal(512,item.RetainedJobCount);});
        string serialized=JsonSerializer.Serialize(page,SqlAgentActivityHistoryFixture.Json);Assert.DoesNotContain("jobId",serialized,StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(serialized)<512*1024);
    }

    [Fact]
    public async Task WindowOverflowDoesNotPreventIndependentExactRunDetail()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        var retained=await SqlAgentActivityHistoryFixture.CommitAsync(database,target);await SqlAgentActivityHistoryFixture.AbandonAsync(database,target,SqlAgentActivityWindowHistoryValidation.MaximumRuns);
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var error=await Assert.ThrowsAsync<SqlAgentActivityWindowHistoryReadException>(async()=>await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window),CancellationToken.None));Assert.Equal(422,error.StatusCode);
        var exact=Assert.IsType<SqlAgentActivityWindowHistoryExactPage>(await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,retained.Id.Value),CancellationToken.None));Assert.NotNull(exact.Capture);
    }

    [Fact]
    public async Task CallerCancellationAndDefaultOffCollectionArePreserved()
    {
        await using var database=await SqlAgentActivityHistoryFixture.CreateAsync(fixture);var target=await SeedAsync(database);var window=await SqlAgentActivityHistoryFixture.WindowAsync(database);
        Assert.Equal(0L,await CountAsync(database,"SELECT count(*) FROM control.collector_schedule WHERE instance_id=@target AND collector_id='sql-agent.activity-observations' AND enabled",("target",target.Value)));
        Assert.Equal(0L,await CountAsync(database,"SELECT count(*) FROM system.retention_policy WHERE data_class='sql_agent_activity_observations' AND (enabled OR retain_for IS NOT NULL)"));
        await using var server=database.CreateServerDataSource();var reader=new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window),cancellation.Token));
    }
}

internal static class SqlAgentActivityHistoryFixture
{
    internal static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(30));
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal sealed record Window(DateTimeOffset FromUtc,DateTimeOffset ToUtc);
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture,bool seal=false)
    {var database=await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);if(seal)await SqlVolumeWindowHistoryFixture.SealAsync(database);return database;}
    internal static async Task<Window> WindowAsync(RepositoryTestDatabase database)
    {var now=await NowAsync(database);return new(now.AddHours(-1),now.AddHours(1));}
    internal static SqlAgentActivityWindowHistoryListRequest List(MonitoredInstanceId target,Window window,int limit=100,SqlAgentActivityWindowHistoryCursor? cursor=null)=>new(target.Value,1,window.FromUtc,window.ToUtc,limit,cursor,Timeout);
    internal static SqlAgentActivityWindowHistoryExactRequest Exact(MonitoredInstanceId target,Window window,Guid run)=>new(target.Value,1,window.FromUtc,window.ToUtc,run,1,Timeout);
    internal static async Task<SqlAgentModuleEnrollment> EnrollmentAsync(RepositoryTestDatabase database,NpgsqlDataSource server,NpgsqlDataSource collector,MonitoredInstanceId target)
    {
        var read=await new PostgreSqlSqlAgentModuleEnrollmentPort(collector).ResolveAsync(target,new(1),CancellationToken.None);
        return read?.Enrollment??await EnrollAsync(database,server,target);
    }
    internal static async Task<Run> CommitAsync(RepositoryTestDatabase database,MonitoredInstanceId target,int sourceRows=2,bool submicrosecond=false,
        CollectorRunOutcome outcome=CollectorRunOutcome.Succeeded,CollectorRunReason reason=CollectorRunReason.Completed)
    {
        await using var server=database.CreateServerDataSource();await using var collector=database.CreateCollectorDataSource();
        var enrollment=await EnrollmentAsync(database,server,collector,target);var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);var run=await BeginAsync(database,collector,runtime,target);
        var capture=outcome==CollectorRunOutcome.Succeeded?Capture(run,enrollment,sourceRows,submicrosecond?run.Work.RepositoryTimeUtc.AddTicks(7):null):null;
        Assert.Equal(CollectorRunCommitStatus.Committed,(await runtime.CommitRunAsync(Commit(run,capture,outcome,reason),CancellationToken.None)).Status);
        Assert.Equal(LeaseReleaseStatus.Released,await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(run.Lease,Timeout),CancellationToken.None));return run;
    }
    internal static async Task RevokeAsync(NpgsqlDataSource server,MonitoredInstanceId target)
    {
        await using var command=server.CreateCommand("SELECT result_status FROM control.set_sql_agent_module_enrollment(@target,1,1,@operation,NULL,@actor,@correlation)");
        command.Parameters.AddWithValue("target",target.Value);command.Parameters.AddWithValue("operation",Guid.NewGuid());command.Parameters.AddWithValue("actor","S-1-5-21-1-2-3-1001");command.Parameters.AddWithValue("correlation",Guid.NewGuid());Assert.Equal("applied",await command.ExecuteScalarAsync());
    }
    internal static async Task ExecuteAsync(RepositoryTestDatabase database,string sql,params (string Name,object Value)[] parameters)
    {await using var command=database.DataSource.CreateCommand(sql);foreach(var parameter in parameters)command.Parameters.AddWithValue(parameter.Name,parameter.Value);await command.ExecuteNonQueryAsync();}
    internal static async Task<string> NativeAsync(RepositoryTestDatabase database,MonitoredInstanceId target,Window window,string? cursor=null,Guid? scope=null)
    {
        await using var server=database.CreateServerDataSource();await using var connection=await server.OpenConnectionAsync();await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using(var set=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true)",connection,transaction)){set.Parameters.AddWithValue("scope",(scope??target.Value).ToString("D"));await set.ExecuteNonQueryAsync();}
        await using var command=new NpgsqlCommand("SELECT reporting.read_sql_agent_activity_window_history(@target,1,@from,@to,1,NULL,NULL,@cursor)",connection,transaction);
        command.Parameters.AddWithValue("target",target.Value);command.Parameters.AddWithValue("from",window.FromUtc);command.Parameters.AddWithValue("to",window.ToUtc);command.Parameters.Add("cursor",NpgsqlDbType.Jsonb).Value=(object?)cursor??DBNull.Value;
        string json=Assert.IsType<string>(await command.ExecuteScalarAsync());await transaction.CommitAsync();return json;
    }
    internal static async Task StageCommitAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CommitCollectorRunRequest request)
    {
        const BindingFlags hidden=BindingFlags.NonPublic|BindingFlags.Static;Type port=typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql=Assert.IsType<string>(port.GetField("CommitSqlAgentActivitySql",hidden)!.GetValue(null));
        byte[] digest=Assert.IsType<byte[]>(port.GetMethod("CreateRequestDigest",hidden)!.Invoke(null,[request.Work,request.Summary.RunId,request.Lease]));
        await using var command=new NpgsqlCommand(sql,connection,transaction);port.GetMethod("AddRunIdentity",hidden)!.Invoke(null,[command,request.Work,request.Summary.RunId,request.Lease,digest]);port.GetMethod("AddSummaryCommitParameters",hidden)!.Invoke(null,[command,request]);
        Type projection=port.Assembly.GetType("SqlObserver.Infrastructure.PostgreSql.SqlAgentActivityPersistencePayload")!;
        string outcome=Assert.IsType<string>(port.GetMethod("MapOutcome",hidden)!.Invoke(null,[request.Summary.Outcome]));string reason=Assert.IsType<string>(port.GetMethod("MapReason",hidden)!.Invoke(null,[request.Summary.Reason]));
        object body=projection.GetMethod("Build",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[request,outcome,reason])!;
        command.Parameters.AddWithValue("agent_activity_payload",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(body,Json));Assert.Equal("committed",await command.ExecuteScalarAsync());
    }
    internal static async Task AbandonAsync(RepositoryTestDatabase database,MonitoredInstanceId target,int count)
    {
        await using var collector=database.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);var pending=await BeginAsync(database,collector,runtime,target);
        Assert.Equal(LeaseRenewalStatus.Renewed,(await new PostgreSqlWorkerLeasePort(collector).RenewAsync(new(pending.Lease,new(WorkerLeaseDuration.Maximum),Timeout),CancellationToken.None)).Status);
        var digest=typeof(PostgreSqlCollectorRuntimeRepositoryPort).GetMethod("CreateRequestDigest",BindingFlags.NonPublic|BindingFlags.Static)!;var ids=new Guid[count];var digests=new byte[count][];
        for(int index=0;index<count;index++){ids[index]=Guid.NewGuid();digests[index]=Assert.IsType<byte[]>(digest.Invoke(null,[pending.Work,new CollectorRunId(ids[index]),pending.Lease]));}
        await using var command=collector.CreateCommand("""
            SELECT begun.result_status,count(*) FROM unnest(@runs,@digests) input(run_id,request_digest)
            CROSS JOIN LATERAL control.begin_collection_run(input.run_id,@target,1,'sql-agent.activity-observations',1,1,@schedule,@scheduled,@work,@owner,@fence,input.request_digest) begun GROUP BY begun.result_status
            """);command.CommandTimeout=180;command.Parameters.AddWithValue("runs",NpgsqlDbType.Array|NpgsqlDbType.Uuid,ids);command.Parameters.AddWithValue("digests",NpgsqlDbType.Array|NpgsqlDbType.Bytea,digests);
        command.Parameters.AddWithValue("target",target.Value);command.Parameters.AddWithValue("schedule",pending.Work.ScheduleRevision.Value);command.Parameters.AddWithValue("scheduled",pending.Work.ScheduledAtUtc);command.Parameters.AddWithValue("work",pending.Lease.Key.Value);command.Parameters.AddWithValue("owner",pending.Lease.Owner.Value);command.Parameters.AddWithValue("fence",pending.Lease.FencingToken.Value);
        await using var rows=await command.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.Equal("started",rows.GetString(0));Assert.Equal((long)count,rows.GetInt64(1));Assert.False(await rows.ReadAsync());
    }
}
