using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class SqlVolumePlanningWindowPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task RealSealedCanonicalWriterKeepsNewestFailureAndIndependentRawSnapshotPaging()
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture); Guid target=await SqlVolumePlanningFixture.TargetAsync(db);
        var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var success=await SqlVolumePlanningFixture.CommitAsync(db,target,3);
        var failure=await SqlVolumePlanningFixture.CommitAsync(db,target,0,CollectorRunOutcome.PermissionDenied);
        await using var server=db.CreateServerDataSource(); var service=SqlVolumePlanningFixture.Service(server);
        var list=Assert.IsType<SqlVolumePlanningWindowPage>(await service.ListAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.List(target,window,1),CancellationToken.None));
        Assert.Equal(failure.Id.Value,Assert.Single(list.Items).RunId); Assert.Equal("collection_failed",list.Items[0].Reason); Assert.NotNull(list.NextAnchor);
        var older=Assert.IsType<SqlVolumePlanningWindowPage>(await service.ListAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.List(target,window,1,list.NextAnchor),CancellationToken.None));
        Assert.Equal(success.Id.Value,Assert.Single(older.Items).RunId); Assert.Null(older.NextAnchor);
        var request=SqlVolumePlanningFixture.Exact(target,window,success.Id.Value,1);
        var first=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),request,CancellationToken.None));
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture),Assert.Single(first.Items).TotalBytes);
        Assert.Equal("insufficient_history",first.Items[0].Forecast.State); Assert.Equal(1,first.Items[0].Forecast.SampleDays);
        var second=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),request with { Cursor=first.NextAnchor },CancellationToken.None));
        Assert.NotEqual(first.Items[0].VolumeKey,Assert.Single(second.Items).VolumeKey); Assert.Equal(first.ReadEpoch,second.ReadEpoch);
        Assert.Equal(first.NextAnchor!.IssuedAtUtc,second.NextAnchor!.IssuedAtUtc);
        var failed=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,failure.Id.Value),CancellationToken.None));
        Assert.Empty(failed.Items); Assert.Equal("collection_failed",failed.Reason); Assert.NotNull(failed.Receipt);
        await using var original=db.DataSource.CreateCommand("SELECT count(*) FROM telemetry.collection_run;"); Assert.Equal(0L,await original.ExecuteScalarAsync());
    }
    [Theory]
    [InlineData(CollectorRunOutcome.Succeeded,0,"observed_empty")]
    [InlineData(CollectorRunOutcome.Partial,1,"collection_loss")]
    [InlineData(CollectorRunOutcome.PermissionDenied,0,"collection_failed")]
    public async Task EmptyAndLossAttemptsRemainRealCanonicalReceipts(CollectorRunOutcome outcome,int count,string reason)
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture); var target=await SqlVolumePlanningFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await SqlVolumePlanningFixture.CommitAsync(db,target,count,outcome);await using var server=db.CreateServerDataSource();var service=SqlVolumePlanningFixture.Service(server);
        var page=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,run.Id.Value),CancellationToken.None));
        Assert.Equal(reason,page.Reason);Assert.Equal(count,page.Items.Count);Assert.NotNull(page.Receipt);
        Assert.All(page.Items,static item=>Assert.Null(item.Forecast.DaysUntilFull));
    }
    [Fact]
    public async Task FullCurrentCapacityPreservesActualDailyBinAndFailedLookbackRefusesOnlyForecast()
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);var target=await SqlVolumePlanningFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var full=await SqlVolumePlanningFixture.CommitAsync(db,target,1,available:0);await using var server=db.CreateServerDataSource();var service=SqlVolumePlanningFixture.Service(server);
        var page=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,full.Id.Value),CancellationToken.None));
        Assert.Equal("full",Assert.Single(page.Items).Forecast.State);Assert.Equal(1,page.Items[0].Forecast.SampleDays);Assert.Single(page.Items[0].Forecast.DailySamples);
        await SqlVolumePlanningFixture.CommitAsync(db,target,0,CollectorRunOutcome.PermissionDenied);
        var next=await SqlVolumePlanningFixture.CommitAsync(db,target,1);var current=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,next.Id.Value),CancellationToken.None));
        Assert.Equal("visibility_gaps",Assert.Single(current.Items).Forecast.State);Assert.Empty(current.Items[0].Forecast.DailySamples);Assert.Equal("partial",current.Lookback!.State);
        var old=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,full.Id.Value),CancellationToken.None));
        Assert.Equal("full",Assert.Single(old.Items).Forecast.State);Assert.Equal(page.Lookback!.AsOfUtc,old.Lookback!.AsOfUtc);
    }
    [Fact]
    public async Task SourceRetirementInvalidatesUnreadPagesAndDoesNotReplaceMissingSelectedPayload()
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);var target=await SqlVolumePlanningFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var first=await SqlVolumePlanningFixture.CommitAsync(db,target,2);await SqlVolumePlanningFixture.CommitAsync(db,target,1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlSqlVolumePlanningWindowRepositoryPort(server);var request=SqlVolumePlanningFixture.List(target,window,1);
        var page=Assert.IsType<SqlVolumePlanningWindowPage>(await reader.ListAsync(request,CancellationToken.None));
        // Model loss of an attached retained source; do not claim this is a retention-worker qualification.
        await SqlVolumePlanningFixture.DetachSourceAsync(db,first.Id.Value);
        Assert.Equal(409,(await Assert.ThrowsAsync<SqlVolumePlanningWindowReadException>(()=>reader.ListAsync(request with { Cursor=page.NextAnchor },CancellationToken.None).AsTask())).StatusCode);
        var detail=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await reader.GetAsync(SqlVolumePlanningFixture.Exact(target,window,first.Id.Value),CancellationToken.None));
        Assert.Equal("payload_missing",detail.Reason);Assert.Empty(detail.Items);Assert.NotNull(detail.Receipt);
    }
    [Fact]
    public async Task RevisionWindowMissingContextAndNativeGrantFencesAreExact()
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);var target=await SqlVolumePlanningFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var run=await SqlVolumePlanningFixture.CommitAsync(db,target,1);await using var server=db.CreateServerDataSource();var reader=new PostgreSqlSqlVolumePlanningWindowRepositoryPort(server);
        await using(var revision=db.DataSource.CreateCommand("UPDATE control.observation_target SET revision=revision+1,updated_at=statement_timestamp() WHERE instance_id=@target;")){revision.Parameters.AddWithValue("target",target);await revision.ExecuteNonQueryAsync();}
        Assert.Equal(409,(await Assert.ThrowsAsync<SqlVolumePlanningWindowReadException>(()=>reader.ListAsync(SqlVolumePlanningFixture.List(target,window),CancellationToken.None).AsTask())).StatusCode);
        var old=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await reader.GetAsync(SqlVolumePlanningFixture.Exact(target,window,run.Id.Value) with { TargetRevision=2 },CancellationToken.None));
        Assert.Equal(1,old.SampleTargetRevision);Assert.Equal(2,old.TargetRevision);Assert.NotNull(old.Receipt);
        var absent=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await reader.GetAsync(SqlVolumePlanningFixture.Exact(target,window,Guid.NewGuid()) with { TargetRevision=2 },CancellationToken.None));
        Assert.Equal("snapshot_not_retained",absent.Reason);Assert.Empty(absent.Items);
        var outside=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await reader.GetAsync(SqlVolumePlanningFixture.Exact(target,window,run.Id.Value) with { TargetRevision=2,FromUtc=old.Receipt!.CompletedAtUtc.AddTicks(10) },CancellationToken.None));
        Assert.Equal("snapshot_outside_window",outside.Reason);
        await using var grants=db.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_server','reporting.read_sql_volume_planning_window(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
             AND NOT has_function_privilege('sqlobserver_collector','reporting.read_sql_volume_planning_window(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)','EXECUTE')
             AND NOT has_function_privilege('sqlobserver_server','reporting.sql_volume_planning_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint)','EXECUTE')
             AND NOT has_table_privilege('sqlobserver_server','telemetry.sql_volume_snapshot','SELECT');
            """);Assert.True(Assert.IsType<bool>(await grants.ExecuteScalarAsync()));
        await using var connection=await server.OpenConnectionAsync();await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var scope=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@other,true); SELECT reporting.read_sql_volume_planning_window(@target,2,@from,@to,25);",connection,tx);
        scope.Parameters.AddWithValue("other",Guid.NewGuid().ToString("D"));scope.Parameters.AddWithValue("target",target);scope.Parameters.AddWithValue("from",window.FromUtc);scope.Parameters.AddWithValue("to",window.ToUtc);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>scope.ExecuteNonQueryAsync())).SqlState);
    }
    [Fact]
    public async Task GenuineProductionHttpPipelineRestoresRunSourceClocksAndPreventsMixedGrantReads()
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);// Explicit DBA-level test configuration uses a daily interval; no production cadence API is claimed.
        var target=await SqlVolumePlanningFixture.TargetAsync(db);var other=await SqlVolumePlanningFixture.TargetAsync(db);
        var window=await DatabaseFileWindowFixture.WindowAsync(db);var run=await SqlVolumePlanningFixture.CommitAsync(db,target,2);
        string connection=new NpgsqlConnectionStringBuilder(db.ConnectionString){Options="-c role=sqlobserver_server"}.ConnectionString;
        await using var app=new SqlVolumeWindowHistoryHttpFactory(connection,target,other);using var reader=app.Client("operator");using var mixed=app.Client("viewer");
        string root=$"/api/v1/observation-targets/{target:D}/resources/volume-planning/history",query=SqlVolumePlanningFixture.Query(window);
        using var page=JsonDocument.Parse(await reader.GetStringAsync(root+"?"+query+"&limit=1"));Assert.Equal(run.Id.Value,page.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        using var exact=JsonDocument.Parse(await reader.GetStringAsync(root+$"/{run.Id.Value:D}?"+query+"&sampleTargetRevision=1&limit=1"));
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture),exact.RootElement.GetProperty("items")[0].GetProperty("totalBytes").GetString());
        Assert.False(exact.RootElement.TryGetProperty("nextAnchor",out _));Assert.False(exact.RootElement.TryGetProperty("coverage",out _));
        Assert.Equal(HttpStatusCode.Forbidden,(await mixed.GetAsync(root+"?"+query)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await reader.GetAsync(root+"?"+query+"&cutoffUtc=2026-01-01T00%3A00%3A00Z")).StatusCode);
        string body=exact.RootElement.GetRawText();Assert.DoesNotContain("source.invalid",body,StringComparison.Ordinal);Assert.DoesNotContain("host_name",body,StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("mount_point","identity_not_comparable")]
    [InlineData("file_scoped_unknown","identity_not_comparable")]
    public async Task NonComparableSourceIdentityNeverProducesPlanningArithmetic(string identity,string reason)
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);var target=await SqlVolumePlanningFixture.TargetAsync(db);
        var at=SqlVolumeWindowHistoryFixture.Now().AddMinutes(-10);var run=await SqlVolumeWindowHistoryFixture.RunAsync(db,target,at,identity:identity);
        var window=new DatabaseFileWindowFixture.Window(at.AddMinutes(-1),at.AddMinutes(1));await using var server=db.CreateServerDataSource();var service=SqlVolumePlanningFixture.Service(server);
        var page=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,run),CancellationToken.None));
        Assert.Equal(reason,Assert.Single(page.Items).Forecast.State);Assert.Empty(page.Items[0].Forecast.DailySamples);Assert.Null(page.Items[0].Forecast.DaysUntilFull);
    }
    [Fact]
    public async Task UnknownCapacitySourceSkewAndMissingPayloadAreVisibleReceipts()
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);var target=await SqlVolumePlanningFixture.TargetAsync(db);
        var at=SqlVolumeWindowHistoryFixture.Now().AddMinutes(-20);var unknown=await SqlVolumeWindowHistoryFixture.RunAsync(db,target,at,total:null,available:null);
        var missing=await SqlVolumeWindowHistoryFixture.RunAsync(db,target,at.AddMinutes(1),retain:false);
        var skewObserved=at.AddMinutes(-10);
        var skew=await SqlVolumeWindowHistoryFixture.RunAsync(db,target,at.AddMinutes(2),observed:skewObserved);
        var window=new DatabaseFileWindowFixture.Window(at.AddMinutes(-1),at.AddMinutes(4));await using var server=db.CreateServerDataSource();var service=SqlVolumePlanningFixture.Service(server);
        foreach(var pair in new[]{(unknown,"capacity_unknown"),(missing,"payload_missing"),(skew,"source_clock_skew")})
        {
            var page=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,pair.Item1),CancellationToken.None));
            Assert.Equal(pair.Item2,page.Reason);Assert.All(page.Items,static item=>Assert.Null(item.Forecast.DaysUntilFull));
            if(pair.Item1==skew)Assert.Equal(skewObserved,Assert.Single(page.Items).ObservedAtUtc);
        }
    }
    [Theory]
    [InlineData(10001,1)]
    [InlineData(251,1000)]
    public async Task WholeCohortOverflowRefusesListWhileIndependentSnapshotOnlyRefusesForecast(int runs,int volumes)
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);var target=await SqlVolumePlanningFixture.TargetAsync(db);
        var at=SqlVolumeWindowHistoryFixture.Now().AddHours(-4);
        // Synthetic retained canonical cohort tests only bounded read accounting; selected snapshot uses the real writer.
        await SqlVolumeWindowHistoryFixture.BulkAsync(db,target,at,runs,volumes,false);
        var selected=await SqlVolumePlanningFixture.CommitAsync(db,target,1);var window=new DatabaseFileWindowFixture.Window(at,selected.Work.RepositoryTimeUtc.AddHours(1));
        await using var server=db.CreateServerDataSource();var service=SqlVolumePlanningFixture.Service(server);
        Assert.Equal(422,(await Assert.ThrowsAsync<SqlVolumePlanningWindowReadException>(()=>service.ListAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.List(target,window),CancellationToken.None).AsTask())).StatusCode);
        var exact=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await service.GetAsync(SqlVolumePlanningFixture.Auth(target),SqlVolumePlanningFixture.Exact(target,window,selected.Id.Value),CancellationToken.None));
        Assert.Equal("lookback_overflow",exact.Lookback!.Reason);Assert.Null(exact.Lookback.Coverage);Assert.Equal("lookback_overflow",Assert.Single(exact.Items).Forecast.State);
        Assert.NotNull(exact.Receipt);Assert.Equal("9007199254740993",exact.Items[0].AvailableBytes);
    }
    [Fact]
    public async Task LateCommittedLookbackAndClosedCursorChangesCannotMixForecastPages()
    {
        await using var db=await SqlVolumePlanningFixture.CreateAsync(fixture);var target=await SqlVolumePlanningFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var selected=await SqlVolumePlanningFixture.CommitAsync(db,target,2);await using var server=db.CreateServerDataSource();var reader=new PostgreSqlSqlVolumePlanningWindowRepositoryPort(server);
        var request=SqlVolumePlanningFixture.Exact(target,window,selected.Id.Value,1);var page=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await reader.GetAsync(request,CancellationToken.None));
        Assert.NotNull(page.NextAnchor);
        await Assert.ThrowsAsync<ArgumentException>(()=>reader.GetAsync(request with { Limit=2,Cursor=page.NextAnchor },CancellationToken.None).AsTask());
        Assert.Equal(400,(await Assert.ThrowsAsync<SqlVolumePlanningWindowReadException>(()=>reader.GetAsync(request with { Cursor=page.NextAnchor! with { AfterVolumeKey=new string('f',64) } },CancellationToken.None).AsTask())).StatusCode);
        await SqlVolumeWindowHistoryFixture.RunAsync(db,target,selected.Work.RepositoryTimeUtc.AddMinutes(-1));
        Assert.Equal(409,(await Assert.ThrowsAsync<SqlVolumePlanningWindowReadException>(()=>reader.GetAsync(request with { Cursor=page.NextAnchor },CancellationToken.None).AsTask())).StatusCode);
    }
}

[Collection(IsolatedClockPostgreSqlCollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class SqlVolumePlanningWindowClockPostgreSqlTests(IsolatedClockPostgreSqlFixture fixture)
{
    [Fact]
    public async Task FiveGenuineDailyCanonicalCommitsForecastAsOfPinnedRewindThroughRealHttp()
    {
        await using var db=await fixture.CreateDatabaseAsync(CancellationToken.None);await SqlVolumePlanningFixture.MigrateAsync(db);await SqlVolumeWindowHistoryFixture.SealAsync(db);
        // Explicit DBA-level test configuration uses a daily interval; no production cadence API is claimed.
        var target=await SqlVolumePlanningFixture.TargetAsync(db);var other=await SqlVolumePlanningFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        SqlVolumePlanningFixture.Run? selected=null;
        for(int i=0;i<5;i++)
        { if(i>0)await fixture.AdvanceAsync(TimeSpan.FromDays(1),CancellationToken.None);selected=await SqlVolumePlanningFixture.CommitAsync(db,target,2,available:5000-i*1000,total:10000); }
        await using(var now=db.DataSource.CreateCommand("SELECT clock_timestamp();")){window=window with { ToUtc=new DateTimeOffset(Assert.IsType<DateTime>(await now.ExecuteScalarAsync()),TimeSpan.Zero).AddHours(1) };}
        string serverConnection=new NpgsqlConnectionStringBuilder(db.ConnectionString){Options="-c role=sqlobserver_server"}.ConnectionString;
        await using var app=new SqlVolumeWindowHistoryHttpFactory(serverConnection,target,other);using var reader=app.Client("operator");
        string route=$"/api/v1/observation-targets/{target:D}/resources/volume-planning/history/{selected!.Id.Value:D}?"+SqlVolumePlanningFixture.Query(window)+"&sampleTargetRevision=1&limit=1";
        using var before=JsonDocument.Parse(await reader.GetStringAsync(route));var forecast=before.RootElement.GetProperty("items")[0].GetProperty("forecast");
        Assert.Equal("estimated",forecast.GetProperty("state").GetString());Assert.Equal(5,forecast.GetProperty("sampleDays").GetInt32());Assert.InRange(forecast.GetProperty("daysUntilFull").GetDouble(),.99,1.01);
        Assert.Equal(5,forecast.GetProperty("dailySamples").GetArrayLength());Assert.Equal("1000",before.RootElement.GetProperty("items")[0].GetProperty("availableBytes").GetString());
        await using(var server=db.CreateServerDataSource())await using(var connection=await server.OpenConnectionAsync())await using(var legacy=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);SELECT * FROM reporting.list_sql_volume_capacity_history(@target,1,@run,@keys);",connection))
        {
            legacy.Parameters.AddWithValue("scope",target.ToString("D"));legacy.Parameters.AddWithValue("target",target);legacy.Parameters.AddWithValue("run",selected.Id.Value);
            legacy.Parameters.Add("keys",NpgsqlTypes.NpgsqlDbType.Array|NpgsqlTypes.NpgsqlDbType.Bytea).Value=new[]{new byte[32]};
            await using var rows=await legacy.ExecuteReaderAsync();Assert.True(await rows.NextResultAsync());int count=0;while(await rows.ReadAsync()){Assert.Equal(new string('0',64),rows.GetString(0));Assert.Equal(10000,rows.GetInt64(2));count++;}Assert.Equal(5,count);
        }
        string cursor=Assert.IsType<string>(before.RootElement.GetProperty("nextCursor").GetString());
        await fixture.AdvanceAsync(TimeSpan.FromDays(1),CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict,(await reader.GetAsync(route+"&cursor="+Uri.EscapeDataString(cursor))).StatusCode);
        await SqlVolumePlanningFixture.CommitAsync(db,target,0,CollectorRunOutcome.PermissionDenied);
        using var after=JsonDocument.Parse(await reader.GetStringAsync(route));
        Assert.Equal(before.RootElement.GetProperty("readEpoch").GetString(),after.RootElement.GetProperty("readEpoch").GetString());
        Assert.Equal(before.RootElement.GetProperty("lookback").GetRawText(),after.RootElement.GetProperty("lookback").GetRawText());
        Assert.Equal(forecast.GetRawText(),after.RootElement.GetProperty("items")[0].GetProperty("forecast").GetRawText());
    }
}

internal static class SqlVolumePlanningFixture
{
    internal static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(30));
    internal sealed record Run(CollectorDueWorkItem Work,CollectorRunId Id,WorkerLeaseIdentity Lease);
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture)
    { var db=await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);await SqlVolumeWindowHistoryFixture.SealAsync(db);await using var version=db.DataSource.CreateCommand("SHOW server_version_num;");Assert.Equal("180004",Assert.IsType<string>(await version.ExecuteScalarAsync()));return db; }
    internal static async Task MigrateAsync(RepositoryTestDatabase db)
    { var result=await new PostgreSqlMigrationPort(db.DataSource).ApplyPendingAsync(new(MigrationBatchResult.MaximumResults,Timeout),CancellationToken.None);Assert.False(result.HasFailures,string.Join(',',result.Results.Where(r=>r.Outcome==MigrationOutcome.Failed).Select(r=>r.FailureCode)));await SqlVolumeWindowHistoryFixture.CutoverCanonicalForeignKeysAsync(db);await using var version=db.DataSource.CreateCommand("SHOW server_version_num;");Assert.Equal("180004",Assert.IsType<string>(await version.ExecuteScalarAsync())); }
    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase db)
    {
        var target=await DatabaseFileWindowFixture.TargetAsync(db);await using var configure=db.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET next_due_at=statement_timestamp()+interval '1 year',
             last_outcome=CASE WHEN collector_id IN('engine.core','database.files') THEN 'succeeded' ELSE last_outcome END,
             last_succeeded_at=CASE WHEN collector_id IN('engine.core','database.files') THEN statement_timestamp() ELSE last_succeeded_at END
             WHERE instance_id=@target;
            UPDATE control.collector_schedule SET enabled=true,collection_interval=interval '1 day' WHERE instance_id=@target AND collector_id='storage.volume';
            """);configure.Parameters.AddWithValue("target",target);await configure.ExecuteNonQueryAsync();return target;
    }
    internal static async Task<Run> CommitAsync(RepositoryTestDatabase db,Guid target,int volumes,CollectorRunOutcome outcome=CollectorRunOutcome.Succeeded,long available=9007199254740993,long total=long.MaxValue)
    {
        await using var collector=db.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        await using(var due=db.DataSource.CreateCommand("UPDATE control.collector_schedule SET last_succeeded_at=statement_timestamp() WHERE instance_id=@target AND collector_id IN('engine.core','database.files'); UPDATE control.collector_schedule SET next_due_at=statement_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='storage.volume';")){due.Parameters.AddWithValue("target",target);await due.ExecuteNonQueryAsync();}
        var work=Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems,Timeout),CancellationToken.None)).Items,item=>item.TargetId.Value==target&&item.CollectorId.Value=="storage.volume");
        var acquisition=await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new WorkerLeaseKey($"collector/run/storage.volume/{target:N}"),new WorkerExecutionId(Guid.NewGuid()),new WorkerLeaseDuration(WorkerLeaseDuration.Maximum),Timeout),CancellationToken.None);
        var lease=Assert.IsType<WorkerLease>(acquisition.Lease).Identity;var id=new CollectorRunId(Guid.NewGuid());Assert.Equal(CollectorRunStartStatus.Started,(await runtime.BeginRunAsync(new(work,id,lease,Timeout),CancellationToken.None)).Status);
        bool success=outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial;
        var items=Enumerable.Range(0,volumes).Select(i=>new SqlVolumeObservation(work.TargetId,work.TargetRevision,i.ToString("x64",CultureInfo.InvariantCulture),SqlVolumeIdentityKind.VolumeId,1,total,available,work.RepositoryTimeUtc)).ToArray();
        var payload=success?new CollectorPayload(sqlVolumes:new SqlVolumeObservationBatch(items)):CollectorPayload.Empty;
        var loss=outcome==CollectorRunOutcome.Partial?new CollectorLossEvidence(CollectorLossKind.SourceRowLimit,1,false):CollectorLossEvidence.None;
        var reason=outcome switch { CollectorRunOutcome.PermissionDenied=>CollectorRunReason.RequiredPermissionMissing,CollectorRunOutcome.Partial=>CollectorRunReason.SourceRowLimit,_=>CollectorRunReason.Completed };
        var summary=new CollectorRunSummary(id,work.TargetId,work.TargetRevision,work.CollectorId,work.CollectorManifestVersion,work.OutputSchemaVersion,outcome,reason,TimeSpan.FromMilliseconds(1),1,
            new(success?volumes+(loss.HasLoss?1:0):0,payload.ItemCount,payload.EstimatedSizeBytes,payload.EstimatedSizeBytes),loss);
        Assert.Equal(CollectorRunCommitStatus.Committed,(await runtime.CommitRunAsync(new(work,summary,payload,CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc),lease,Timeout),CancellationToken.None)).Status);
        Assert.Equal(LeaseReleaseStatus.Released,await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease,Timeout),CancellationToken.None));return new(work,id,lease);
    }
    internal static SqlVolumePlanningWindowService Service(NpgsqlDataSource source)=>new(new PostgreSqlSqlVolumePlanningWindowRepositoryPort(source));
    internal static AuthorizationContext Auth(Guid target)=>new(new("S-1-5-21-799"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target)]))]);
    internal static SqlVolumePlanningWindowListRequest List(Guid target,DatabaseFileWindowFixture.Window window,int limit=100,SqlVolumePlanningWindowCursor? cursor=null)=>new(target,1,window.FromUtc,window.ToUtc,limit,cursor,Timeout);
    internal static SqlVolumePlanningWindowSnapshotRequest Exact(Guid target,DatabaseFileWindowFixture.Window window,Guid run,int limit=100,SqlVolumePlanningWindowCursor? cursor=null)=>new(target,1,window.FromUtc,window.ToUtc,run,1,limit,cursor,Timeout);
    internal static string Query(DatabaseFileWindowFixture.Window window)=>"targetRevision=1&fromUtc="+Uri.EscapeDataString(window.FromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture))+"&toUtc="+Uri.EscapeDataString(window.ToUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture));
    internal static async Task DetachSourceAsync(RepositoryTestDatabase db,Guid run)
    {
        await using var query=db.DataSource.CreateCommand("SELECT child.relname FROM pg_inherits i JOIN pg_class child ON child.oid=i.inhrelid WHERE i.inhparent='telemetry.sql_volume_snapshot'::regclass AND EXISTS(SELECT 1 FROM telemetry.sql_volume_snapshot s WHERE s.run_id=@run AND s.tableoid=child.oid);");query.Parameters.AddWithValue("run",run);
        string name=Assert.IsType<string>(await query.ExecuteScalarAsync());await using var detach=db.DataSource.CreateCommand($"ALTER TABLE telemetry.sql_volume_snapshot DETACH PARTITION telemetry.\"{name}\";");await detach.ExecuteNonQueryAsync();
    }
}
