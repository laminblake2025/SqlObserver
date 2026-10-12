using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private static readonly int[] SeriesRaceStatuses=[200,409];
    [Fact]
    public async Task MaintenanceSeriesCompleteFiniteCreateAndExactReplayKeepImmutableReceipt()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        Guid target=Guid.NewGuid(),series=Guid.NewGuid(),operation=Guid.NewGuid();
        await SeedTargetOnlyAsync(database,target);
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        string definition=SeriesDefinitionJson(start,start.AddDays(2));
        string expansion=SeriesUtcExpansion(series,start,start.AddDays(2));
        string receipt=await NativeSeriesWriteAsync(database,target,series,null,null,definition,expansion,operation);
        Assert.Equal(receipt,await NativeSeriesWriteAsync(database,target,series,null,null,definition,"[]",operation));
        await using var check=database.DataSource.CreateCommand("SELECT count(*),bool_and(m.cancelled_at IS NULL) FROM alerting.maintenance_series_occurrence o JOIN alerting.maintenance_window m ON m.window_id=o.window_id WHERE o.series_id=@series; SELECT count(*) FROM audit.activity WHERE action_name='maintenance.series.create';");
        check.Parameters.AddWithValue("series",series);
        await using NpgsqlDataReader rows=await check.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync()); Assert.Equal(3L,rows.GetInt64(0)); Assert.True(rows.GetBoolean(1));
        Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(1L,rows.GetInt64(0));
    }

    [Fact]
    public async Task MaintenanceSeriesRejectsIncompleteSpoofedAndForeignExpansionBeforeAnyMaterialization()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        Guid target=Guid.NewGuid(),series=Guid.NewGuid(); await SeedTargetOnlyAsync(database,target);
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        string definition=SeriesDefinitionJson(start,start.AddDays(1));
        foreach(string expansion in new[]{"[]",SeriesUtcExpansion(series,start,start),SeriesUtcExpansion(Guid.NewGuid(),start,start.AddDays(1)),SeriesUtcExpansion(series,start,start.AddDays(1),offsetMinutes:60)})
            Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(()=>NativeSeriesWriteAsync(database,target,series,null,null,definition,expansion,Guid.NewGuid()))).SqlState);
        await using var count=database.DataSource.CreateCommand("SELECT count(*) FROM alerting.maintenance_series;");
        Assert.Equal(0L,(long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MaintenanceSeriesRemovedDateTombstoneAndCancellationFenceCannotBeReintroduced()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        Guid target=Guid.NewGuid(),series=Guid.NewGuid(); await SeedTargetOnlyAsync(database,target);
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        await NativeSeriesWriteAsync(database,target,series,null,null,SeriesDefinitionJson(start,start.AddDays(3)),SeriesUtcExpansion(series,start,start.AddDays(3)),Guid.NewGuid());
        await NativeSeriesWriteAsync(database,target,series,1,start,SeriesDefinitionJson(start,start.AddDays(1)),SeriesUtcExpansion(series,start,start.AddDays(1)),Guid.NewGuid());
        await NativeSeriesWriteAsync(database,target,series,2,start,SeriesDefinitionJson(start,start.AddDays(4)),SeriesUtcExpansion(series,start,start.AddDays(4)),Guid.NewGuid());
        await NativeSeriesWriteAsync(database,target,series,3,start.AddDays(1),null,"[]",Guid.NewGuid());
        await NativeSeriesWriteAsync(database,target,series,4,start,SeriesDefinitionJson(start,start.AddDays(5)),SeriesUtcExpansion(series,start,start.AddDays(5)),Guid.NewGuid());
        await using var check=database.DataSource.CreateCommand("SELECT o.local_date,m.cancelled_at IS NOT NULL FROM alerting.maintenance_series_occurrence o JOIN alerting.maintenance_window m ON m.window_id=o.window_id WHERE o.series_id=@series ORDER BY o.local_date; SELECT cancelled_from_date FROM alerting.maintenance_series WHERE series_id=@series;");
        check.Parameters.AddWithValue("series",series);
        await using NpgsqlDataReader rows=await check.ExecuteReaderAsync();
        int count=0;
        while(await rows.ReadAsync()) { Assert.Equal(count>0,rows.GetBoolean(1)); count++; }
        Assert.Equal(5,count); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(start.AddDays(1),rows.GetFieldValue<DateOnly>(0));
    }

    [Fact]
    public async Task MaintenanceSeriesFutureUpdatesKeepWindowIdsAndImmutableSourceVersions()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid());Guid series=Guid.NewGuid();await SeedTargetOnlyAsync(database,target.Value);
        await using NpgsqlDataSource source=database.CreateServerDataSource();var repository=new PostgreSqlMaintenanceSeriesRepositoryPort(source);
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var definition=SeriesDefinition(start,start.AddDays(2));
        MaintenanceSeriesWriteReceipt first=await repository.WriteAsync(SeriesRequest(target,series,definition),CancellationToken.None);
        Assert.Equal(3,first.CreatedOccurrences);Assert.Equal(0,first.UpdatedOccurrences);
        MaintenanceSeriesOccurrencePage before=await repository.ListOccurrencesAsync(target,series,50,null,Timeout(),CancellationToken.None);
        var changed=SeriesDefinition(start,start.AddDays(2),new TimeOnly(13,0));
        MaintenanceSeriesWriteReceipt updated=await repository.WriteAsync(SeriesRequest(target,series,changed,1,start),CancellationToken.None);
        Assert.Equal(0,updated.CreatedOccurrences);Assert.Equal(3,updated.UpdatedOccurrences);
        MaintenanceSeriesOccurrencePage after=await repository.ListOccurrencesAsync(target,series,50,null,Timeout(),CancellationToken.None);
        Assert.Equal(before.Items.Select(x=>x.WindowId),after.Items.Select(x=>x.WindowId));
        Assert.All(after.Items,x=>{Assert.Equal(2,x.Revision);Assert.Equal(2,x.SeriesRevision);Assert.Equal(13,x.StartsAtUtc.Hour);Assert.False(x.Exception);});
        await using var versions=database.DataSource.CreateCommand("SELECT count(*),count(DISTINCT starts_at::time) FROM alerting.maintenance_series_materialization WHERE series_id=@series;");
        versions.Parameters.AddWithValue("series",series);await using NpgsqlDataReader rows=await versions.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());Assert.Equal(6L,rows.GetInt64(0));Assert.Equal(2L,rows.GetInt64(1));
    }

    [Fact]
    public async Task MaintenanceSeriesIndividualEditsAndCancellationArePermanentExceptionsWithExactOneOffReplay()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid());Guid series=Guid.NewGuid();await SeedTargetOnlyAsync(database,target.Value);
        await using NpgsqlDataSource source=database.CreateServerDataSource();var repository=new PostgreSqlMaintenanceSeriesRepositoryPort(source);var alerts=new PostgreSqlAlertRepositoryPort(source);
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);var definition=SeriesDefinition(start,start.AddDays(2));
        await repository.WriteAsync(SeriesRequest(target,series,definition),CancellationToken.None);
        MaintenanceSeriesOccurrencePage page=await repository.ListOccurrencesAsync(target,series,50,null,Timeout(),CancellationToken.None);
        MaintenanceSeriesOccurrenceItem edit=page.Items[0],cancel=page.Items[1];
        var editRequest=new MaintenanceWriteRequest(new MaintenanceWindow(edit.WindowId,target,edit.StartsAtUtc.AddHours(2),edit.EndsAtUtc.AddHours(2),"Individual exception"),Guid.NewGuid().ToString("D"),Audit(target,AdministrativeAuditAction.UpdateMaintenanceWindow),Timeout(),edit.Revision);
        AdministrativeAuditReceipt editReceipt=await alerts.UpsertMaintenanceAsync(editRequest,CancellationToken.None);
        var cancelRequest=new MaintenanceCancellationRequest(cancel.WindowId,Guid.NewGuid().ToString("D"),Audit(target,AdministrativeAuditAction.RetireMaintenanceWindow),Timeout(),cancel.Revision);
        AdministrativeAuditReceipt cancelReceipt=await alerts.CancelMaintenanceAsync(cancelRequest,CancellationToken.None);
        MaintenanceSeriesWriteReceipt result=await repository.WriteAsync(SeriesRequest(target,series,SeriesDefinition(start,start.AddDays(3),new TimeOnly(16,0)),1,start),CancellationToken.None);
        Assert.Equal(2,result.PreservedExceptions);Assert.Equal(1,result.UpdatedOccurrences);Assert.Equal(1,result.CreatedOccurrences);
        page=await repository.ListOccurrencesAsync(target,series,50,null,Timeout(),CancellationToken.None);
        MaintenanceSeriesOccurrenceItem edited=page.Items.Single(x=>x.WindowId==edit.WindowId),cancelled=page.Items.Single(x=>x.WindowId==cancel.WindowId);
        Assert.True(edited.Exception);Assert.Equal(edit.StartsAtUtc.AddHours(2),edited.StartsAtUtc);Assert.Equal(1,edited.SeriesRevision);
        Assert.True(cancelled.Exception);Assert.NotNull(cancelled.CancelledAtUtc);
        Assert.Equal(editReceipt.AuditId.Value,(await alerts.UpsertMaintenanceAsync(editRequest,CancellationToken.None)).AuditId.Value);
        Assert.Equal(cancelReceipt.AuditId.Value,(await alerts.CancelMaintenanceAsync(cancelRequest,CancellationToken.None)).AuditId.Value);
        {
            // Simulate wall time moving beyond the edited window without changing its source provenance.
            await using NpgsqlConnection connection=await database.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
            await using var marker=new NpgsqlCommand("INSERT INTO alerting.maintenance_series_mutation_guard VALUES(pg_current_xact_id(),@series,@target,clock_timestamp()-interval '2 days');",connection,transaction);
            marker.Parameters.AddWithValue("series",series);marker.Parameters.AddWithValue("target",target.Value);await marker.ExecuteNonQueryAsync();
            await using var started=new NpgsqlCommand("UPDATE alerting.maintenance_window SET starts_at=clock_timestamp()-interval '1 minute',ends_at=clock_timestamp()+interval '1 hour' WHERE window_id=@window;",connection,transaction);
            started.Parameters.AddWithValue("window",edit.WindowId);await started.ExecuteNonQueryAsync();
            await using var remove=new NpgsqlCommand("DELETE FROM alerting.maintenance_series_mutation_guard WHERE transaction_id=pg_current_xact_id();",connection,transaction);await remove.ExecuteNonQueryAsync();await transaction.CommitAsync();
        }
        Assert.Equal(editReceipt.AuditId.Value,(await alerts.UpsertMaintenanceAsync(editRequest,CancellationToken.None)).AuditId.Value);
        Assert.Equal(409,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>alerts.UpsertMaintenanceAsync(editRequest with{IdempotencyKey=Guid.NewGuid().ToString("D"),ExpectedRevision=edited.Revision},CancellationToken.None).AsTask())).StatusCode);
    }

    [Theory]
    [InlineData(2027,3,13,2,30,"earlier",2)]
    [InlineData(2027,11,6,1,30,"earlier",3)]
    [InlineData(2027,11,6,1,30,"later",3)]
    public async Task MaintenanceSeriesNativeDstRulesMatchServerCanonicalExpansion(int year,int month,int day,int hour,int minute,string fold,int count)
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid());Guid series=Guid.NewGuid();await SeedTargetOnlyAsync(database,target.Value);
        await using NpgsqlDataSource source=database.CreateServerDataSource();var repository=new PostgreSqlMaintenanceSeriesRepositoryPort(source);
        DateOnly start=new(year,month,day);
        var definition=new MaintenanceSeriesDefinition("America/New_York",start,start.AddDays(2),new(hour,minute),60,MaintenanceSeriesFrequency.Daily,1,[],null,"skip",fold=="earlier"?MaintenanceFoldPolicy.Earlier:MaintenanceFoldPolicy.Later,"Synthetic maintenance");
        var request=SeriesRequest(target,series,definition);await repository.WriteAsync(request,CancellationToken.None);
        MaintenanceSeriesOccurrencePage page=await repository.ListOccurrencesAsync(target,series,50,null,Timeout(),CancellationToken.None);
        Assert.Equal(count,page.Items.Count);
        foreach(MaintenanceSeriesOccurrenceItem row in page.Items)
        {MaintenanceSeriesOccurrence expected=request.Occurrences.Single(x=>x.WindowId==row.WindowId);Assert.Equal(expected.StartsAtUtc,row.StartsAtUtc);Assert.Equal(expected.UtcOffsetMinutes,row.UtcOffsetMinutes);Assert.Equal(expected.Ambiguous,row.Ambiguous);}
    }

    [Fact]
    public async Task MaintenanceSeriesWeeklyMonthlyPagingAndForeignAnchorsUseCivilOrdering()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid());Guid weekly=Guid.NewGuid(),monthly=Guid.NewGuid();await SeedTargetOnlyAsync(database,target.Value);
        await using NpgsqlDataSource source=database.CreateServerDataSource();var repository=new PostgreSqlMaintenanceSeriesRepositoryPort(source);
        DateOnly start=new(2028,1,1);
        var definition=new MaintenanceSeriesDefinition("UTC",start,start.AddMonths(3),new(12,0),60,MaintenanceSeriesFrequency.Weekly,2,[0,3],null,"skip",MaintenanceFoldPolicy.Earlier,"Synthetic maintenance");
        await repository.WriteAsync(SeriesRequest(target,weekly,definition),CancellationToken.None);
        var monthlyDefinition=new MaintenanceSeriesDefinition("UTC",start,start.AddMonths(5),new(12,0),60,MaintenanceSeriesFrequency.Monthly,1,[],31,"skip",MaintenanceFoldPolicy.Earlier,"Synthetic maintenance");
        var monthlyRequest=SeriesRequest(target,monthly,monthlyDefinition);await repository.WriteAsync(monthlyRequest,CancellationToken.None);
        var rows=new List<MaintenanceSeriesOccurrenceItem>();Guid? cursor=null;
        do{var page=await repository.ListOccurrencesAsync(target,weekly,2,cursor,Timeout(),CancellationToken.None);rows.AddRange(page.Items);cursor=page.NextBeforeWindowId;}while(cursor is not null);
        Assert.Equal(MaintenanceSeriesExpansion.Expand(weekly,definition).Occurrences.Reverse().Select(x=>x.WindowId),rows.Select(x=>x.WindowId));
        var monthPage=await repository.ListOccurrencesAsync(target,monthly,50,null,Timeout(),CancellationToken.None);Assert.Equal(monthlyRequest.Occurrences.Count,monthPage.Items.Count);Assert.All(monthPage.Items,x=>Assert.Equal(31,x.LocalDate.Day));
        Assert.Equal(404,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>repository.ListOccurrencesAsync(target,monthly,2,rows[0].WindowId,Timeout(),CancellationToken.None).AsTask())).StatusCode);
        var first=await repository.ListAsync(target,1,null,Timeout(),CancellationToken.None);Assert.Single(first.Items);Assert.NotNull(first.NextBeforeSeriesId);
        var second=await repository.ListAsync(target,1,first.NextBeforeSeriesId,Timeout(),CancellationToken.None);Assert.Single(second.Items);Assert.NotEqual(first.Items[0].SeriesId,second.Items[0].SeriesId);Assert.Null(second.NextBeforeSeriesId);
    }

    [Fact]
    public async Task MaintenanceSeriesLifetimeCapacityRejectsExpansionWithoutDroppingHistory()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid());Guid series=Guid.NewGuid();await SeedTargetOnlyAsync(database,target.Value);
        await using NpgsqlDataSource source=database.CreateServerDataSource();var repository=new PostgreSqlMaintenanceSeriesRepositoryPort(source);
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);var original=SeriesDefinition(start,start.AddYears(5));
        await repository.WriteAsync(SeriesRequest(target,series,original),CancellationToken.None);
        DateOnly nextStart=original.EndDate.AddYears(1);var moved=SeriesDefinition(nextStart,nextStart.AddYears(1));
        Assert.Equal(409,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>repository.WriteAsync(SeriesRequest(target,series,moved,1,start),CancellationToken.None).AsTask())).StatusCode);
        MaintenanceSeriesStatus status=(await repository.GetAsync(target,series,Timeout(),CancellationToken.None))!;
        Assert.Equal(1,status.Revision);Assert.Equal(original.DefinitionDigest,status.Definition.DefinitionDigest);Assert.Equal(MaintenanceSeriesExpansion.Expand(series,original).Occurrences.Count,status.OccurrenceCount);
        await using var verify=database.DataSource.CreateCommand("SELECT count(*) FILTER(WHERE cancelled_at IS NOT NULL) FROM alerting.maintenance_window WHERE instance_id=@target;");
        verify.Parameters.AddWithValue("target",target.Value);Assert.Equal(0L,(long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MaintenanceSeriesRolesForcedRlsAndPrivateExpansionRejectUnscopedCallers()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        await using var catalog=database.DataSource.CreateCommand("""
            SELECT bool_and(c.relrowsecurity AND c.relforcerowsecurity) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
             WHERE n.nspname='alerting' AND c.relname IN('maintenance_series','maintenance_series_occurrence','maintenance_series_materialization','maintenance_series_operation','maintenance_series_mutation_guard');
            SELECT bool_and(NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE') AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')),
             NOT EXISTS(SELECT 1 FROM pg_proc f JOIN pg_namespace ns ON ns.oid=f.pronamespace,LATERAL aclexplode(coalesce(f.proacl,acldefault('f',f.proowner))) a
               WHERE ns.nspname='alerting' AND f.proname IN('write_maintenance_series','get_maintenance_series','list_maintenance_series','list_maintenance_series_occurrences','maintenance_series_expand','maintenance_series_definition_digest','maintenance_series_window_id','guard_generated_maintenance_window') AND a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='alerting' AND p.proname LIKE '%maintenance_series%';
            SELECT has_table_privilege('sqlobserver_server','alerting.maintenance_series_mutation_guard','INSERT'),has_function_privilege('sqlobserver_server','alerting.maintenance_series_expand(uuid,jsonb)','EXECUTE'),has_sequence_privilege('sqlobserver_server','alerting.maintenance_series_occurrence_occurrence_sequence_seq','USAGE');
            """);
        await using NpgsqlDataReader rows=await catalog.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.True(rows.GetBoolean(0));
        Assert.True(await rows.NextResultAsync());Assert.True(await rows.ReadAsync());Assert.True(rows.GetBoolean(0));Assert.True(rows.GetBoolean(1));
        Assert.True(await rows.NextResultAsync());Assert.True(await rows.ReadAsync());for(int i=0;i<3;i++)Assert.False(rows.GetBoolean(i));
        await using NpgsqlDataSource source=database.CreateServerDataSource();await using var unscoped=source.CreateCommand("SELECT * FROM alerting.get_maintenance_series(@target,@series);");
        unscoped.Parameters.AddWithValue("target",Guid.NewGuid());unscoped.Parameters.AddWithValue("series",Guid.NewGuid());
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>unscoped.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task MaintenanceSeriesDispatchFenceHalfOpenOverlapAndEvaluationUseAuthoritativeWindows()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var (target,ruleId,alert,_,destination)=await SeedSnoozeEpisodeAsync(database);
        var lease=new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/series-delivery"),new WorkerExecutionId(Guid.NewGuid()),new FencingToken(1));await AcquireLeaseAsync(database,lease);
        Guid deliveryId=Guid.NewGuid();await SeedOutboxAsync(database,target.Value,ruleId,destination,deliveryId,Guid.NewGuid(),alertId:alert);
        await using NpgsqlDataSource source=database.CreateServerDataSource();await using NpgsqlDataSource collectorSource=database.CreateCollectorDataSource();
        var seriesRepository=new PostgreSqlMaintenanceSeriesRepositoryPort(source);var collector=new PostgreSqlAlertRepositoryPort(collectorSource);
        Guid series=Guid.NewGuid();DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var definition=new MaintenanceSeriesDefinition("UTC",start,start.AddDays(1),new(12,0),2880,MaintenanceSeriesFrequency.Daily,1,[],null,"skip",MaintenanceFoldPolicy.Earlier,"Synthetic maintenance");
        AlertDeliveryWork claimed=Assert.Single(await collector.ClaimDueDeliveriesAsync(lease,10,Timeout(),CancellationToken.None));
        IAlertDeliveryDispatchPermit permit=await collector.AcquireDeliveryDispatchPermitAsync(claimed,Timeout(),CancellationToken.None);
        Task<MaintenanceSeriesWriteReceipt> mutation=seriesRepository.WriteAsync(SeriesRequest(target,series,definition),CancellationToken.None).AsTask();
        try{await Task.Delay(150);Assert.False(mutation.IsCompleted);}finally{await permit.DisposeAsync();}
        await mutation.WaitAsync(TimeSpan.FromSeconds(10));
        MaintenanceSeriesOccurrencePage page=await seriesRepository.ListOccurrencesAsync(target,series,50,null,Timeout(),CancellationToken.None);
        MaintenanceSeriesOccurrenceItem first=page.Items[^1],last=page.Items[0];
        Assert.Null(await collector.GetMaintenanceAsync(target,first.StartsAtUtc.AddTicks(-10),Timeout(),CancellationToken.None));
        Assert.NotNull(await collector.GetMaintenanceAsync(target,first.StartsAtUtc,Timeout(),CancellationToken.None));
        Assert.Equal(last.WindowId,(await collector.GetMaintenanceAsync(target,first.EndsAtUtc,Timeout(),CancellationToken.None))!.Id);
        Assert.Null(await collector.GetMaintenanceAsync(target,last.EndsAtUtc,Timeout(),CancellationToken.None));
        DateTimeOffset activeStart=TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1)),activeEnd=activeStart.AddHours(1);
        await using(var connection=await database.DataSource.OpenConnectionAsync())
        {
            await using var transaction=await connection.BeginTransactionAsync();
            await using var shift=new NpgsqlCommand("INSERT INTO alerting.maintenance_series_mutation_guard VALUES(pg_current_xact_id(),@series,@target,@at); UPDATE alerting.maintenance_window SET starts_at=@start,ends_at=@end WHERE window_id=@window; DELETE FROM alerting.maintenance_series_mutation_guard WHERE transaction_id=pg_current_xact_id();",connection,transaction);
            shift.Parameters.AddWithValue("series",series);shift.Parameters.AddWithValue("target",target.Value);shift.Parameters.AddWithValue("at",activeStart.AddDays(-2));shift.Parameters.AddWithValue("start",activeStart);shift.Parameters.AddWithValue("end",activeEnd);shift.Parameters.AddWithValue("window",first.WindowId);await shift.ExecuteNonQueryAsync();await transaction.CommitAsync();
        }
        Assert.Equal(AlertDeliveryReadiness.Maintenance,await collector.RecheckDeliveryAsync(claimed,Timeout(),CancellationToken.None));
        Assert.Equal(AlertDeliveryLeaseOutcome.DeferMaintenance,await collector.RenewDeliveryStateAsync(claimed,lease,Timeout(),CancellationToken.None));
        await seriesRepository.WriteAsync(SeriesRequest(target,series,null,1,start),CancellationToken.None);
        Assert.NotNull(await collector.GetMaintenanceAsync(target,DateTimeOffset.UtcNow,Timeout(),CancellationToken.None));
        await using(var verify=database.DataSource.CreateCommand("SELECT cancelled_at IS NULL,completed_at IS NULL FROM alerting.delivery_outbox WHERE delivery_id=@delivery;"))
        {verify.Parameters.AddWithValue("delivery",deliveryId);await using NpgsqlDataReader rows=await verify.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.True(rows.GetBoolean(0));Assert.True(rows.GetBoolean(1));}
        var evaluationLease=new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/series-evaluation"),new WorkerExecutionId(Guid.NewGuid()),new FencingToken(1));await AcquireLeaseAsync(database,evaluationLease);
        AlertRuleDefinition rule=Assert.Single(await collector.ListRulesAsync(target,Timeout(),CancellationToken.None));
        DateTimeOffset observed=TruncateToMicroseconds(DateTimeOffset.UtcNow);
        AlertEvaluationBatch batch=await ClaimClearConfirmationObservationAsync(database,collector,evaluationLease,rule,target,observed,2);
        AlertRuleState prior=(await collector.GetStateAsync(target,ruleId,Timeout(),CancellationToken.None))!;
        MaintenanceWindow maintenance=(await collector.GetMaintenanceAsync(target,observed,Timeout(),CancellationToken.None))!;
        AlertEvaluationResult evaluated=AlertEvaluator.Evaluate(rule,prior,batch.Observations[0],maintenance);
        batch=batch with{Decisions=[new AlertEvaluationDecision(batch.Observations[0],evaluated.State,evaluated.Event,evaluated.DeliverySuppressed,evaluated.Reason)],Maintenance=maintenance};
        Assert.Equal(1,(await collector.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        AlertRuleState state=(await collector.GetStateAsync(target,ruleId,Timeout(),CancellationToken.None))!;
        Assert.Equal(observed,state.LastObservedUtc);Assert.True(state.DeliverySuppressed);Assert.Equal(AlertState.Firing,state.State);
    }

    [Fact]
    public async Task MaintenanceSeriesSameRevisionRaceActorDivergenceAndPastOnlyCreateAreFenced()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid());Guid series=Guid.NewGuid();await SeedTargetOnlyAsync(database,target.Value);
        await using NpgsqlDataSource source=database.CreateServerDataSource();var repository=new PostgreSqlMaintenanceSeriesRepositoryPort(source);
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);var first=SeriesRequest(target,series,SeriesDefinition(start,start.AddDays(1)));
        await repository.WriteAsync(first,CancellationToken.None);
        async Task<int> UpdateAsync(int hour)
        {try{await repository.WriteAsync(SeriesRequest(target,series,SeriesDefinition(start,start.AddDays(1),new(hour,0)),1,start),CancellationToken.None);return 200;}catch(AlertRepositoryOperationException error){return error.StatusCode;}}
        Assert.Equal(SeriesRaceStatuses,(await Task.WhenAll(UpdateAsync(13),UpdateAsync(14))).Order().ToArray());
        var changedActor=new MaintenanceSeriesWriteRequest(series,null,null,first.Definition,first.Reason,first.IdempotencyKey,new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-19"),new AuditCorrelationId(Guid.NewGuid()),AdministrativeAuditAction.CreateMaintenanceSeries,target),Timeout()){Occurrences=first.Occurrences};
        Assert.Equal(409,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>repository.WriteAsync(changedActor,CancellationToken.None).AsTask())).StatusCode);
        var divergent=SeriesRequest(target,series,SeriesDefinition(start,start.AddDays(2)),operation:first.IdempotencyKey);
        Assert.Equal(409,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>repository.WriteAsync(divergent,CancellationToken.None).AsTask())).StatusCode);
        DateOnly past=start.AddDays(-10);var pastOnly=SeriesRequest(target,Guid.NewGuid(),SeriesDefinition(past,past.AddDays(1)));
        Assert.Equal(400,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>repository.WriteAsync(pastOnly,CancellationToken.None).AsTask())).StatusCode);
        Assert.Equal(2,(await repository.GetAsync(target,series,Timeout(),CancellationToken.None))!.Revision);
    }

    private static MaintenanceSeriesDefinition SeriesDefinition(DateOnly start,DateOnly end,TimeOnly? time=null)=>
        new("UTC",start,end,time??new TimeOnly(12,0),60,MaintenanceSeriesFrequency.Daily,1,[],null,"skip",MaintenanceFoldPolicy.Earlier,"Synthetic maintenance");
    private static MaintenanceSeriesWriteRequest SeriesRequest(MonitoredInstanceId target,Guid series,MaintenanceSeriesDefinition? definition,long? revision=null,DateOnly? cutoff=null,string? operation=null)=>
        new(series,revision,cutoff,definition,"Synthetic maintenance",operation??Guid.NewGuid().ToString("D"),Audit(target,definition is null?AdministrativeAuditAction.CancelMaintenanceSeries:revision is null?AdministrativeAuditAction.CreateMaintenanceSeries:AdministrativeAuditAction.UpdateMaintenanceSeries),Timeout())
        {Occurrences=definition is null?[]:MaintenanceSeriesExpansion.Expand(series,definition).Occurrences};

    private static string SeriesDefinitionJson(DateOnly start,DateOnly end,int durationMinutes=60) =>
        JsonSerializer.Serialize(new{frequency="daily",interval=1,startDate=start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),endDate=end.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),localStartTime="12:00",durationMinutes,timeZoneId="Etc/UTC",weekdays=Array.Empty<int>(),dayOfMonth=(int?)null,gapPolicy="skip",foldPolicy="earlier",reason="Synthetic maintenance"});

    private static Guid SeriesWindowId(Guid series,DateOnly date) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(series.ToString("D")+"|"+date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))).AsSpan(0,16),bigEndian:true);

    private static string SeriesUtcExpansion(Guid series,DateOnly start,DateOnly end,int offsetMinutes=0) =>
        JsonSerializer.Serialize(Enumerable.Range(0,end.DayNumber-start.DayNumber+1).Select(i=>
        {
            DateOnly date=start.AddDays(i); DateTimeOffset at=new(date.ToDateTime(new TimeOnly(12,0),DateTimeKind.Utc));
            return new{windowId=SeriesWindowId(series,date),localDate=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),startsAtUtc=at,endsAtUtc=at.AddHours(1),utcOffsetMinutes=offsetMinutes,ambiguous=false,expansionVersion="civil-v1"};
        }));

    private static async Task<string> NativeSeriesWriteAsync(RepositoryTestDatabase database,Guid target,Guid series,
        long? revision,DateOnly? cutoff,string? definition,string expansion,Guid operation,string actor="S-1-5-18")
    {
        string? definitionDigest=null;
        if(definition is not null)
        {
            using JsonDocument parsed=JsonDocument.Parse(definition); JsonElement d=parsed.RootElement;
            string canonical=string.Join('|',d.GetProperty("frequency").GetString(),d.GetProperty("interval").GetInt32().ToString(CultureInfo.InvariantCulture),
                d.GetProperty("startDate").GetString(),d.GetProperty("endDate").GetString(),d.GetProperty("localStartTime").GetString(),
                d.GetProperty("durationMinutes").GetInt32().ToString(CultureInfo.InvariantCulture),d.GetProperty("timeZoneId").GetString(),"","<null>","skip",d.GetProperty("foldPolicy").GetString(),d.GetProperty("reason").GetString());
            definitionDigest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
        string action=definition is null?"maintenance.series.cancel":revision is null?"maintenance.series.create":"maintenance.series.update";
        string canonicalRequest=string.Join('|',action,target.ToString("D"),series.ToString("D"),revision?.ToString(CultureInfo.InvariantCulture)??"<null>",cutoff?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??"<null>",definitionDigest??"<null>","Synthetic maintenance");
        string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
        await using NpgsqlDataSource server=database.CreateServerDataSource();
        await using NpgsqlConnection connection=await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
        await using var scope=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction);
        scope.Parameters.AddWithValue("target",target.ToString("D")); await scope.ExecuteNonQueryAsync();
        await using var command=new NpgsqlCommand("SELECT row_to_json(receipt)::text FROM alerting.write_maintenance_series(@target,@series,@revision,@cutoff,@definition,@expansion,@reason,@operation,@actor,@correlation,@digest) receipt;",connection,transaction);
        command.Parameters.AddWithValue("target",target); command.Parameters.AddWithValue("series",series);
        command.Parameters.Add("revision",NpgsqlTypes.NpgsqlDbType.Bigint).Value=(object?)revision??DBNull.Value;
        command.Parameters.Add("cutoff",NpgsqlTypes.NpgsqlDbType.Date).Value=(object?)cutoff??DBNull.Value;
        command.Parameters.Add("definition",NpgsqlTypes.NpgsqlDbType.Jsonb).Value=(object?)definition??DBNull.Value;
        command.Parameters.Add("expansion",NpgsqlTypes.NpgsqlDbType.Jsonb).Value=expansion;
        command.Parameters.AddWithValue("reason","Synthetic maintenance"); command.Parameters.AddWithValue("operation",operation.ToString("D"));
        command.Parameters.AddWithValue("actor",actor); command.Parameters.AddWithValue("correlation",Guid.NewGuid()); command.Parameters.AddWithValue("digest",digest);
        string result=(string)(await command.ExecuteScalarAsync())!; await transaction.CommitAsync(); return result;
    }
}
