using Npgsql;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    private static readonly int[] SnoozeRaceOutcomes = [200, 409];
    [Fact]
    public async Task SnoozeReplayAfterExpiryAndUnsnoozeReturnsImmutableReceiptWithoutRestoringSuppression()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, alert, episode, _) = await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(serverSource);
        DateTimeOffset ends = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddSeconds(5));
        AlertSnoozeWriteRequest snooze = SnoozeRequest(target, alert, episode, 1, ends);
        AlertSnoozeWriteReceipt original = await repository.SetSnoozeAsync(snooze, CancellationToken.None);
        Assert.Equal(2, original.Revision);
        Assert.Equal(ends, original.EndsAtUtc);
        AlertSnoozeStatus status = (await repository.GetSnoozeAsync(target, alert, Timeout(), CancellationToken.None))!;
        Assert.True(status.Snoozed);
        Assert.True(status.DeliverySuppressed);
        Assert.Equal(AlertState.Firing, status.State);
        AlertActiveDto targetRow = Assert.Single((await repository.ListActivePageAsync(target, 10, null, Timeout(), CancellationToken.None)).Items);
        Assert.Equal(episode, targetRow.EpisodeId);
        Assert.Equal(2, targetRow.Revision);
        Assert.True(targetRow.Snoozed);
        AlertActiveDto fleetRow = Assert.Single((await repository.ListFleetActivePageAsync(TargetAuthorizationScope.ForTargets([target]), 10, null, Timeout(), CancellationToken.None)).Items).Alert;
        Assert.True(fleetRow.DeliverySuppressed);
        Assert.Equal(ends, fleetRow.SnoozedUntilUtc);
        await repository.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 2, null), CancellationToken.None);
        await using (var wait = database.DataSource.CreateCommand("SELECT pg_sleep(greatest(0,extract(epoch FROM @ends-clock_timestamp()))+0.02);"))
        {
            wait.Parameters.AddWithValue("ends", ends);
            await wait.ExecuteNonQueryAsync();
        }
        AlertSnoozeWriteReceipt replay = await repository.SetSnoozeAsync(snooze, CancellationToken.None);
        AssertSnoozeReceiptEqual(original, replay);
        status = (await repository.GetSnoozeAsync(target, alert, Timeout(), CancellationToken.None))!;
        Assert.False(status.Snoozed);
        Assert.False(status.DeliverySuppressed);
        Assert.Null(status.SnoozedUntilUtc);
        Assert.Null(status.SnoozeReason);
        Assert.Null(status.SnoozedBy);
        Assert.Null(status.SnoozedAtUtc);
        Assert.Equal(3, status.Revision);
        await using var verify = database.DataSource.CreateCommand("SELECT count(*),bool_and(safe_details->>'reason'='investigating' AND subject_kind='alerting' AND subject_identifier=@target::text AND safe_details ? 'operationId') FROM audit.activity WHERE action_name IN('alert.snooze','alert.unsnooze'); SELECT state,delivery_suppressed,evidence_digest IS NULL,revision FROM alerting.rule_state WHERE instance_id=@target AND rule_id=@rule;");
        verify.Parameters.AddWithValue("target", target.Value);
        verify.Parameters.AddWithValue("rule", rule);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt16(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(3, reader.GetInt64(3));
    }

    [Fact]
    public async Task SnoozeRejectsChangedOperationPayloadActorActionAndStaleEpisodeRevision()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, _, alert, episode, _) = await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(serverSource);
        DateTimeOffset ends = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1));
        AlertSnoozeWriteRequest first = SnoozeRequest(target, alert, episode, 1, ends);
        await repository.SetSnoozeAsync(first, CancellationToken.None);
        foreach (AlertSnoozeWriteRequest conflict in new[]
        {
            SnoozeRequest(target, alert, episode, 1, ends, first.IdempotencyKey, "different reason"),
            SnoozeRequest(target, alert, episode, 1, ends, first.IdempotencyKey, actor: "S-1-5-19"),
            SnoozeRequest(target, alert, episode, 1, null, first.IdempotencyKey),
            SnoozeRequest(target, alert, Guid.NewGuid(), 2, ends),
            SnoozeRequest(target, alert, episode, 1, ends),
        })
        {
            AlertRepositoryOperationException exception = await Assert.ThrowsAsync<AlertRepositoryOperationException>(() => repository.SetSnoozeAsync(conflict, CancellationToken.None).AsTask());
            Assert.Equal(409, exception.StatusCode);
        }
        foreach (DateTimeOffset invalidEnd in new[] { DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddHours(25) })
        {
            AlertRepositoryOperationException exception = await Assert.ThrowsAsync<AlertRepositoryOperationException>(() => repository.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 2, TruncateToMicroseconds(invalidEnd)), CancellationToken.None).AsTask());
            Assert.Equal(400, exception.StatusCode);
        }
        Assert.Null(await repository.GetSnoozeAsync(new MonitoredInstanceId(Guid.NewGuid()), alert, Timeout(), CancellationToken.None));
    }

    [Fact]
    public async Task SnoozeSameRevisionConcurrentCommandsHaveOneWinner()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, _, alert, episode, _) = await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(serverSource);
        DateTimeOffset ends = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1));
        async Task<int> ApplyAsync(string reason)
        {
            try { await repository.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 1, ends, reason: reason), CancellationToken.None); return 200; }
            catch (AlertRepositoryOperationException exception) { return exception.StatusCode; }
        }
        int[] outcomes = await Task.WhenAll(ApplyAsync("first"), ApplyAsync("second"));
        Assert.Equal(SnoozeRaceOutcomes, outcomes.Order().ToArray());
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM audit.activity WHERE action_name='alert.snooze';");
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SnoozeDefersClaimedDeliveryWithoutDroppingQueueOrRetryDeadlineAndUnsnoozeResumesIt()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, alert, episode, destination) = await SeedSnoozeEpisodeAsync(database);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await AcquireLeaseAsync(database, lease);
        Guid delivery = Guid.NewGuid();
        await SeedOutboxAsync(database, target.Value, rule, destination, delivery, Guid.NewGuid(), alertId: alert);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorSource);
        var server = new PostgreSqlAlertRepositoryPort(serverSource);
        AlertDeliveryWork claimed = Assert.Single(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await server.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 1, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1))), CancellationToken.None);
        Assert.Equal(AlertDeliveryReadiness.Snoozed, await collector.RecheckDeliveryAsync(claimed, Timeout(), CancellationToken.None));
        Assert.False(await collector.RenewDeliveryAsync(claimed.DeliveryId, target, lease, Timeout(), CancellationToken.None));
        Assert.Equal(AlertDeliveryLeaseOutcome.DeferSnooze, await collector.RenewDeliveryStateAsync(claimed, lease, Timeout(), CancellationToken.None));
        Assert.Empty(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await using (var verify = database.DataSource.CreateCommand("SELECT completed_at IS NULL,cancelled_at IS NULL,leased_until IS NULL,due_at=@due,attempt FROM alerting.delivery_outbox WHERE delivery_id=@delivery;"))
        {
            verify.Parameters.AddWithValue("delivery", delivery); verify.Parameters.AddWithValue("due", claimed.DueAtUtc);
            await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (int i=0;i<4;i++) Assert.True(reader.GetBoolean(i));
            Assert.Equal(0, reader.GetInt32(4));
        }
        DateTimeOffset retryAt = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1));
        await using (var retry = database.DataSource.CreateCommand("UPDATE alerting.delivery_outbox SET due_at=@retry WHERE delivery_id=@delivery;"))
        { retry.Parameters.AddWithValue("retry", retryAt); retry.Parameters.AddWithValue("delivery", delivery); Assert.Equal(1, await retry.ExecuteNonQueryAsync()); }
        await server.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 2, null), CancellationToken.None);
        Assert.Empty(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await using (var verifyRetry = database.DataSource.CreateCommand("SELECT due_at FROM alerting.delivery_outbox WHERE delivery_id=@delivery;"))
        { verifyRetry.Parameters.AddWithValue("delivery", delivery); Assert.Equal(retryAt, PostgreSqlRuntimeSupport.ConvertUtcTimestamp((await verifyRetry.ExecuteScalarAsync())!)); }
        await SetDueNowAsync(database, delivery);
        Assert.Equal(delivery, Assert.Single(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None)).DeliveryId);
    }

    [Fact]
    public async Task SnoozeExpiryAndNewEpisodeIgnoreOldMetadataWithoutChangingQueuedRows()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, alert, episode, destination) = await SeedSnoozeEpisodeAsync(database);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await AcquireLeaseAsync(database, lease);
        Guid delivery = Guid.NewGuid();
        await SeedOutboxAsync(database, target.Value, rule, destination, delivery, Guid.NewGuid(), alertId: alert);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorSource);
        var server = new PostgreSqlAlertRepositoryPort(serverSource);
        await server.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 1, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1))), CancellationToken.None);
        Assert.Empty(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await using (var expire = database.DataSource.CreateCommand("UPDATE alerting.episode_snooze SET ends_at=clock_timestamp()-interval '1 microsecond' WHERE instance_id=@target;"))
        { expire.Parameters.AddWithValue("target", target.Value); Assert.Equal(1, await expire.ExecuteNonQueryAsync()); }
        Assert.False((await server.GetSnoozeAsync(target, alert, Timeout(), CancellationToken.None))!.Snoozed);
        AlertDeliveryWork expiredClaim = Assert.Single(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await collector.RecoverDeliveryClaimsAsync(target, lease, Timeout(), CancellationToken.None);
        await server.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 2, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1))), CancellationToken.None);
        Guid nextAlert = Guid.NewGuid(), nextEpisode = Guid.NewGuid();
        await using (var change = database.DataSource.CreateCommand("UPDATE alerting.rule_state SET alert_id=@alert,alert_episode_id=@episode,revision=revision+1 WHERE instance_id=@target;"))
        { change.Parameters.AddWithValue("target", target.Value); change.Parameters.AddWithValue("alert", nextAlert); change.Parameters.AddWithValue("episode", nextEpisode); Assert.Equal(1, await change.ExecuteNonQueryAsync()); }
        AlertSnoozeStatus current = (await server.GetSnoozeAsync(target, nextAlert, Timeout(), CancellationToken.None))!;
        Assert.False(current.Snoozed); Assert.Null(current.SnoozedUntilUtc);
        Assert.Equal(AlertDeliveryReadiness.Active, await collector.RecheckDeliveryAsync(expiredClaim, Timeout(), CancellationToken.None));
        Assert.Equal(delivery, Assert.Single(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None)).DeliveryId);
    }

    [Fact]
    public async Task SnoozeDispatchFenceWaitsForPermittedSendThenStopsClaimedNextSend()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, alert, episode, destination) = await SeedSnoozeEpisodeAsync(database);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await AcquireLeaseAsync(database, lease);
        await SeedOutboxAsync(database, target.Value, rule, destination, Guid.NewGuid(), Guid.NewGuid(), alertId: alert);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorSource);
        var server = new PostgreSqlAlertRepositoryPort(serverSource);
        AlertDeliveryWork claimed = Assert.Single(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        IAlertDeliveryDispatchPermit permit = await collector.AcquireDeliveryDispatchPermitAsync(claimed, Timeout(), CancellationToken.None);
        Task<AlertSnoozeWriteReceipt> mutation = server.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 1, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1))), CancellationToken.None).AsTask();
        try { await Task.Delay(150); Assert.False(mutation.IsCompleted); }
        finally { await permit.DisposeAsync(); }
        await mutation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AlertDeliveryLeaseOutcome.DeferSnooze, await collector.RenewDeliveryStateAsync(claimed, lease, Timeout(), CancellationToken.None));
    }

    [Fact]
    public async Task SnoozeUnsnoozeLeavesMaintenanceSuppressionAndRetryTimingIntact()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, alert, episode, destination) = await SeedSnoozeEpisodeAsync(database);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await AcquireLeaseAsync(database, lease);
        Guid delivery = Guid.NewGuid();
        await SeedOutboxAsync(database, target.Value, rule, destination, delivery, Guid.NewGuid(), alertId: alert);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorSource);
        var server = new PostgreSqlAlertRepositoryPort(serverSource);
        AlertDeliveryWork claimed = Assert.Single(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        DateTimeOffset now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        await server.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 1, now.AddHours(1)), CancellationToken.None);
        var window = new MaintenanceWindow(Guid.NewGuid(), target, now.AddMinutes(-1), now.AddMinutes(30), "planned maintenance");
        await server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(window, Guid.NewGuid().ToString("D"), Audit(target, AdministrativeAuditAction.CreateMaintenanceWindow), Timeout()), CancellationToken.None);
        await server.SetSnoozeAsync(SnoozeRequest(target, alert, episode, 2, null), CancellationToken.None);
        Assert.Equal(AlertDeliveryReadiness.Maintenance, await collector.RecheckDeliveryAsync(claimed, Timeout(), CancellationToken.None));
        Assert.Empty(await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await using var verify = database.DataSource.CreateCommand("SELECT completed_at IS NULL,cancelled_at IS NULL,due_at>=@ends,(SELECT count(*) FROM alerting.delivery_suppression_intent WHERE delivery_id=@delivery) FROM alerting.delivery_outbox WHERE delivery_id=@delivery;");
        verify.Parameters.AddWithValue("delivery", delivery); verify.Parameters.AddWithValue("ends", window.EndsAtUtc);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); for (int i=0;i<3;i++) Assert.True(reader.GetBoolean(i));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public async Task SnoozeCollectorSampleAndHistoricalEvaluatorReplayPreserveHumanRevisionAndMetadata()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid()); Guid ruleId=Guid.NewGuid(), destination=Guid.NewGuid();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/snooze-evaluation"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        await SeedRuleAndDestinationAsync(database, target.Value, ruleId, destination);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var rule = new AlertRuleDefinition(ruleId,catalog.Name,catalog.Kind,new MetricId(catalog.Metric!),catalog.Comparison,catalog.Threshold,catalog.Hysteresis,catalog.ConfirmationCount,catalog.ConfirmationWindow,catalog.EvaluationInterval);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var collector=new PostgreSqlAlertRepositoryPort(collectorSource); var server=new PostgreSqlAlertRepositoryPort(serverSource);
        DateTimeOffset observed=TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-2));
        var (batch,state)=await PersistClearConfirmationObservationAsync(database,collector,lease,rule,target,observed,2);
        Assert.Equal(AlertState.Firing,state.State);
        AlertSnoozeWriteReceipt receipt=await server.SetSnoozeAsync(SnoozeRequest(target,state.AlertId!.Value,state.EpisodeId!.Value,state.Revision,TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1))),CancellationToken.None);
        ReplaySnapshot before=await SnapshotReplayRowsAsync(database,target.Value,ruleId);
        Assert.Equal(0,(await collector.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        ReplaySnapshot after=await SnapshotReplayRowsAsync(database,target.Value,ruleId);
        Assert.Equal(before,after);
        Assert.Equal(receipt.Revision,(await collector.GetStateAsync(target,ruleId,Timeout(),CancellationToken.None))!.Revision);
        var (_,sampleState)=await PersistClearConfirmationObservationAsync(database,collector,lease,rule,target,observed.AddSeconds(30),2);
        Assert.False(sampleState.DeliverySuppressed);
        Assert.True((await server.GetSnoozeAsync(target,state.AlertId.Value,Timeout(),CancellationToken.None))!.Snoozed);
    }

    [Fact]
    public async Task SnoozeTablesAndFunctionsEnforceRolesForcedRlsAndExplicitTargetScope()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, _, alert, _, _) = await SeedSnoozeEpisodeAsync(database);
        await using var catalog = database.DataSource.CreateCommand("""
            SELECT bool_and(c.relrowsecurity AND c.relforcerowsecurity)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='alerting' AND c.relname IN('episode_snooze','snooze_operation');
            SELECT has_function_privilege('sqlobserver_server','alerting.get_episode_snooze(uuid,uuid)','EXECUTE'),
             has_function_privilege('sqlobserver_collector','alerting.get_episode_snooze(uuid,uuid)','EXECUTE'),
             has_function_privilege('sqlobserver_server','alerting.episode_snoozed_internal(uuid,uuid,timestamp with time zone)','EXECUTE'),
             has_table_privilege('sqlobserver_server','alerting.episode_snooze','SELECT');
            """);
        await using (NpgsqlDataReader reader=await catalog.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0)); for(int i=1;i<4;i++) Assert.False(reader.GetBoolean(i));
        }
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        await using var unscoped=serverSource.CreateCommand("SELECT * FROM alerting.get_episode_snooze(@target,@alert);");
        unscoped.Parameters.AddWithValue("target",target.Value); unscoped.Parameters.AddWithValue("alert",alert);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>unscoped.ExecuteNonQueryAsync())).SqlState);
        await using NpgsqlDataSource collectorSource=database.CreateCollectorDataSource();
        await using var denied=collectorSource.CreateCommand("SELECT * FROM alerting.get_episode_snooze(@target,@alert);");
        denied.Parameters.AddWithValue("target",target.Value); denied.Parameters.AddWithValue("alert",alert);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task SnoozeNativeCommandsRejectZeroIdentitiesUnsafeReasonsAndForgedDigest()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, _, alert, episode, _) = await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        foreach ((Guid rawAlert,Guid rawEpisode,Guid correlation,string reason) in new[]
        {
            (Guid.Empty,episode,Guid.NewGuid(),"investigate"),
            (alert,Guid.Empty,Guid.NewGuid(),"investigate"),
            (alert,episode,Guid.Empty,"investigate"),
            (alert,episode,Guid.NewGuid(),"unsafe\nreason"),
            (alert,episode,Guid.NewGuid(),new string('é',257)),
            (alert,episode,Guid.NewGuid(),"forged digest"),
        })
        {
            await using NpgsqlConnection connection=await serverSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
            await using var scope=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction);
            scope.Parameters.AddWithValue("target",target.Value.ToString("D")); await scope.ExecuteNonQueryAsync();
            await using var command=new NpgsqlCommand("SELECT * FROM alerting.set_episode_snooze(@target,@alert,@episode,1,clock_timestamp()+interval '1 hour',@reason,@operation,'S-1-5-18',@correlation,repeat('a',64));",connection,transaction);
            command.Parameters.AddWithValue("target",target.Value); command.Parameters.AddWithValue("alert",rawAlert);
            command.Parameters.AddWithValue("episode",rawEpisode); command.Parameters.AddWithValue("reason",reason);
            command.Parameters.AddWithValue("operation",Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("correlation",correlation);
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var count=database.DataSource.CreateCommand("SELECT count(*) FROM alerting.snooze_operation;");
        Assert.Equal(0L,(long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SnoozeAcknowledgedEpisodePreservesStateAndRejectsNewCommandsAfterResolution()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, _, alert, episode, _) = await SeedSnoozeEpisodeAsync(database);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        await server.AcknowledgeAsync(new AlertAcknowledgeRequest(alert,Guid.NewGuid().ToString("D"),
            Audit(target,AdministrativeAuditAction.AcknowledgeAlert),Timeout(),1,null,episode),CancellationToken.None);
        AlertSnoozeWriteRequest request=SnoozeRequest(target,alert,episode,2,TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1)));
        AlertSnoozeWriteReceipt receipt=await server.SetSnoozeAsync(request,CancellationToken.None);
        AlertSnoozeStatus acknowledged=(await server.GetSnoozeAsync(target,alert,Timeout(),CancellationToken.None))!;
        Assert.Equal(AlertState.Acknowledged,acknowledged.State); Assert.True(acknowledged.Snoozed);
        await using (var resolve=database.DataSource.CreateCommand("UPDATE alerting.rule_state SET state=5,resolved_at=clock_timestamp(),revision=revision+1 WHERE instance_id=@target;"))
        { resolve.Parameters.AddWithValue("target",target.Value); Assert.Equal(1,await resolve.ExecuteNonQueryAsync()); }
        AssertSnoozeReceiptEqual(receipt,await server.SetSnoozeAsync(request,CancellationToken.None));
        AlertRepositoryOperationException conflict=await Assert.ThrowsAsync<AlertRepositoryOperationException>(()=>server.SetSnoozeAsync(SnoozeRequest(target,alert,episode,4,null),CancellationToken.None).AsTask());
        Assert.Equal(409,conflict.StatusCode);
        AlertSnoozeStatus resolved=(await server.GetSnoozeAsync(target,alert,Timeout(),CancellationToken.None))!;
        Assert.Equal(AlertState.Resolved,resolved.State); Assert.False(resolved.Snoozed); Assert.Equal(4,resolved.Revision);
        Assert.Empty((await server.ListActivePageAsync(target,10,null,Timeout(),CancellationToken.None)).Items);
    }

    [Fact]
    public async Task SnoozeLegacyInboxRowsKeepBothEpisodeAndRevisionPreconditionsAbsent()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target=new MonitoredInstanceId(Guid.NewGuid()); Guid rule=Guid.NewGuid();
        await SeedTargetOnlyAsync(database,target.Value);
        await SeedRuleAndDestinationAsync(database,target.Value,rule,Guid.NewGuid());
        await SeedActiveRuleStateAsync(database,target.Value,rule);
        await using NpgsqlDataSource serverSource=database.CreateServerDataSource();
        var server=new PostgreSqlAlertRepositoryPort(serverSource);
        AlertActiveDto local=Assert.Single((await server.ListActivePageAsync(target,10,null,Timeout(),CancellationToken.None)).Items);
        Assert.Null(local.EpisodeId); Assert.Null(local.Revision); Assert.False(local.Snoozed);
        AlertActiveDto fleet=Assert.Single((await server.ListFleetActivePageAsync(TargetAuthorizationScope.ForTargets([target]),10,null,Timeout(),CancellationToken.None)).Items).Alert;
        Assert.Null(fleet.EpisodeId); Assert.Null(fleet.Revision); Assert.False(fleet.Snoozed);
        Assert.Null(await server.GetSnoozeAsync(target,local.AlertId,Timeout(),CancellationToken.None));
    }

    private static void AssertSnoozeReceiptEqual(AlertSnoozeWriteReceipt expected, AlertSnoozeWriteReceipt actual)
    {
        Assert.Equal(expected.Audit.AuditId.Value, actual.Audit.AuditId.Value);
        Assert.Equal(expected.Audit.RecordedAtUtc, actual.Audit.RecordedAtUtc);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.EndsAtUtc, actual.EndsAtUtc);
        Assert.Equal(expected.AppliedAtUtc, actual.AppliedAtUtc);
    }

    private static AlertSnoozeWriteRequest SnoozeRequest(MonitoredInstanceId target,Guid alert,Guid episode,long revision,
        DateTimeOffset? ends,string? operation=null,string reason="investigating",string actor="S-1-5-18") =>
        new(alert,episode,revision,ends,reason,operation??Guid.NewGuid().ToString("D"),
            new AdministrativeAuditEnvelope(new ActorSecurityIdentifier(actor),new AuditCorrelationId(Guid.NewGuid()),
                ends is null?AdministrativeAuditAction.UnsnoozeAlert:AdministrativeAuditAction.SnoozeAlert,target),Timeout());

    private static async Task<(MonitoredInstanceId Target,Guid Rule,Guid Alert,Guid Episode,Guid Destination)> SeedSnoozeEpisodeAsync(RepositoryTestDatabase database)
    {
        var target=new MonitoredInstanceId(Guid.NewGuid()); Guid rule=Guid.NewGuid(),alert=Guid.NewGuid(),episode=Guid.NewGuid(),destination=Guid.NewGuid();
        await SeedTargetOnlyAsync(database,target.Value);
        await SeedRuleAndDestinationAsync(database,target.Value,rule,destination);
        await using var seed=database.DataSource.CreateCommand("""
            INSERT INTO alerting.rule_state(instance_id,rule_id,alert_id,alert_episode_id,state,consecutive_matches,first_match_at,last_observed_at,fired_at,episode_started_at,revision)
            VALUES(@target,@rule,@alert,@episode,3,1,clock_timestamp(),clock_timestamp(),clock_timestamp(),clock_timestamp(),1);
            """);
        seed.Parameters.AddWithValue("target",target.Value); seed.Parameters.AddWithValue("rule",rule);
        seed.Parameters.AddWithValue("alert",alert); seed.Parameters.AddWithValue("episode",episode);
        await seed.ExecuteNonQueryAsync();
        return(target,rule,alert,episode,destination);
    }
}
