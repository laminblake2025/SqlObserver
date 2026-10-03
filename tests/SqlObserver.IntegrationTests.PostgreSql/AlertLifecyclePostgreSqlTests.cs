using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
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
    [Fact]
    public async Task LifecycleNativeWritersRetainAllVerbsSeparateRefiresAndDoNotDuplicateReplaysOrAudits()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid()); Guid ruleId = Guid.NewGuid();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/lifecycle"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease); await SeedRuleAndDestinationAsync(database, target.Value, ruleId, Guid.NewGuid());
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(item => item.Kind == AlertRuleKind.MetricThreshold);
        var rule = new AlertRuleDefinition(ruleId, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison,
            catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource(), serverSource = database.CreateServerDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorSource); var writer = new PostgreSqlAlertRepositoryPort(serverSource);
        var history = new PostgreSqlAlertLifecycleRepositoryPort(serverSource);
        DateTimeOffset start = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-5));
        var (firstBatch, fired) = await PersistClearConfirmationObservationAsync(database, collector, lease, rule, target, start, 2);
        Assert.Equal(AlertState.Firing, fired.State);
        Guid alert = fired.AlertId!.Value, episode = fired.EpisodeId!.Value;
        await writer.AcknowledgeAsync(new(alert, Guid.NewGuid().ToString("D"), Audit(target, AdministrativeAuditAction.AcknowledgeAlert), Timeout(), fired.Revision, null, episode), CancellationToken.None);
        AlertRuleState acknowledged = (await collector.GetStateAsync(target, ruleId, Timeout(), CancellationToken.None))!;
        AlertSnoozeWriteRequest snooze = SnoozeRequest(target, alert, episode, acknowledged.Revision, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(1)));
        AlertSnoozeWriteReceipt snoozed = await writer.SetSnoozeAsync(snooze, CancellationToken.None);
        AlertSnoozeWriteReceipt cleared = await writer.SetSnoozeAsync(SnoozeRequest(target, alert, episode, snoozed.Revision, null), CancellationToken.None);
        AlertAssignmentWriteRequest assignment = AssignmentRequest(target, alert, episode, cleared.Revision, AssignmentOwnerA, "Responder A");
        AlertAssignmentWriteReceipt assigned = await writer.SetAssignmentAsync(assignment, CancellationToken.None);
        AlertAssignmentWriteReceipt reassigned = await writer.SetAssignmentAsync(AssignmentRequest(target, alert, episode, assigned.Revision, AssignmentOwnerB, "Responder B"), CancellationToken.None);
        await writer.SetAssignmentAsync(AssignmentRequest(target, alert, episode, reassigned.Revision, null, null), CancellationToken.None);
        ReplaySnapshot before = await SnapshotReplayRowsAsync(database, target.Value, ruleId);
        Assert.Equal(0, (await collector.EvaluateAndPersistAsync(firstBatch, CancellationToken.None)).Evaluated);
        Assert.Equal(before, await SnapshotReplayRowsAsync(database, target.Value, ruleId));
        AssertSnoozeReceiptEqual(snoozed, await writer.SetSnoozeAsync(snooze, CancellationToken.None));
        AssertAssignmentReceiptEqual(assigned, await writer.SetAssignmentAsync(assignment, CancellationToken.None));
        var (_, unchanged) = await PersistClearConfirmationObservationAsync(database, collector, lease, rule, target, start.AddSeconds(30), 2);
        Assert.Equal(AlertState.Acknowledged, unchanged.State);
        var (_, resolved) = await PersistClearConfirmationObservationAsync(database, collector, lease, rule, target, start.AddSeconds(60), 0);
        Assert.Equal(AlertState.Resolved, resolved.State);
        var (_, refired) = await PersistClearConfirmationObservationAsync(database, collector, lease, rule, target, start.AddSeconds(90), 2);
        Assert.Equal(AlertState.Firing, refired.State); Assert.NotEqual(alert, refired.AlertId); Assert.NotEqual(episode, refired.EpisodeId);
        AlertLifecyclePage page = (await history.ListAsync(new(target.Value, 1, start.AddSeconds(-1), TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(1)), 100, null, Timeout()), CancellationToken.None))!;
        Assert.Equal(9, page.Coverage.EventCount); Assert.Equal(9, page.Coverage.CompleteEventCount); Assert.Equal(2, page.Coverage.CycleCount);
        Assert.Equal(2, page.Items.Count(item => item.Kind == "fired"));
        foreach (string verb in new[] { "acknowledged", "resolved", "snoozed", "unsnoozed", "assigned", "reassigned", "unassigned" })
            Assert.Single(page.Items, item => item.Kind == verb);
        AlertLifecycleEvent original = page.Items.Single(item => item.Kind == "fired" && item.AlertId == alert);
        Assert.Equal(start, original.EventAtUtc); Assert.Equal("observed", original.TimeBasis); Assert.Equal(episode, original.EpisodeId);
        Assert.All(page.Items.Where(item => item.Source != "evaluation"), item => Assert.Equal("applied", item.TimeBasis));
        Assert.All(page.Items.Where(item => item.Source == "evaluation"), item => Assert.Null(item.ActorSid));
        Assert.Equal("S-1-5-18", page.Items.Single(item => item.Kind == "acknowledged").ActorSid);
        Assert.Null(page.Coverage.RetentionDays); Assert.NotNull(page.Coverage.ProvenanceBoundaryUtc);
        Assert.Equal(page.Items.Count, page.Items.Select(item => item.EventId).Distinct().Count());
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.lifecycle_provenance WHERE instance_id=@target; SELECT count(*) FROM audit.activity WHERE action_name IN('alert.snooze','alert.unsnooze','alert.assign','alert.unassign','alert.acknowledge');");
        count.Parameters.AddWithValue("target", target.Value); await using NpgsqlDataReader rows = await count.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync()); Assert.Equal(9L, rows.GetInt64(0)); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(6L, rows.GetInt64(0));
    }

    [Fact]
    public async Task LifecycleRuleDisableCapturesPostMutationRevisionAndAppliedNameWithoutChangingAuditOrReplay()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid()); Guid ruleId = Guid.NewGuid();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/lifecycle-disable"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        await SeedRuleAndDestinationAsync(database, target.Value, ruleId, Guid.NewGuid());
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(item => item.Kind == AlertRuleKind.MetricThreshold);
        var rule = new AlertRuleDefinition(ruleId, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison,
            catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource(), serverSource = database.CreateServerDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorSource); var writer = new PostgreSqlAlertRepositoryPort(serverSource);
        var history = new PostgreSqlAlertLifecycleRepositoryPort(serverSource);
        DateTimeOffset start = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-5));
        var (_, fired) = await PersistClearConfirmationObservationAsync(database, collector, lease, rule, target, start, 2);
        var disabled = new AlertRuleDefinition(ruleId, "disabled.at.apply", rule.Kind, rule.MetricId, rule.Comparison,
            rule.Threshold, rule.Hysteresis, rule.ConfirmationCount, rule.ConfirmationWindow, rule.EvaluationInterval, enabled: false);
        var command = new AlertRuleWriteRequest(disabled, Guid.NewGuid().ToString("D"), Audit(target, AdministrativeAuditAction.RetireAlertRule), Timeout(), ExpectedRevision: 1);
        AdministrativeAuditReceipt receipt = await writer.UpsertRuleAsync(command, CancellationToken.None);
        var request = new AlertLifecycleListRequest(target.Value, 1, start.AddSeconds(-1), TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(1)), 100, null, Timeout());
        AlertLifecyclePage first = (await history.ListAsync(request, CancellationToken.None))!;
        Assert.Equal(2, first.Coverage.EventCount); Assert.Equal(2, first.Coverage.CompleteEventCount);
        AlertLifecycleEvent resolved = first.Items.Single(item => item.Kind == "resolved");
        Assert.Equal("administrative", resolved.Source); Assert.Equal("applied", resolved.TimeBasis);
        Assert.Equal("rule_disabled", resolved.Reason); Assert.Equal(fired.AlertId, resolved.AlertId);
        Assert.Equal(fired.EpisodeId, resolved.EpisodeId); Assert.Equal(fired.Revision + 1, resolved.StateRevision);
        Assert.Equal("disabled.at.apply", resolved.RuleName); Assert.Equal(receipt.RecordedAtUtc, resolved.RecordedAtUtc);
        Assert.Equal("S-1-5-18", resolved.ActorSid); Assert.Equal("firing", resolved.FromState); Assert.Equal("resolved", resolved.ToState);
        AlertRuleState state = (await collector.GetStateAsync(target, ruleId, Timeout(), CancellationToken.None))!;
        Assert.Equal(AlertState.Resolved, state.State); Assert.Equal(resolved.StateRevision, state.Revision);
        AdministrativeAuditReceipt replay = await writer.UpsertRuleAsync(command, CancellationToken.None);
        Assert.Equal(receipt.AuditId.Value, replay.AuditId.Value); Assert.Equal(receipt.RecordedAtUtc, replay.RecordedAtUtc);
        var renamed = new AlertRuleDefinition(ruleId, "renamed.after.capture", disabled.Kind, disabled.MetricId, disabled.Comparison,
            disabled.Threshold, disabled.Hysteresis, disabled.ConfirmationCount, disabled.ConfirmationWindow, disabled.EvaluationInterval, enabled: false);
        await writer.UpsertRuleAsync(new(renamed, Guid.NewGuid().ToString("D"), Audit(target, AdministrativeAuditAction.RetireAlertRule), Timeout(), ExpectedRevision: 2), CancellationToken.None);
        AlertLifecyclePage after = (await history.ListAsync(request, CancellationToken.None))!;
        Assert.Equal(first.Items.ToArray(), after.Items.ToArray());
        Assert.Equal(rule.Name, after.Items.Single(item => item.Kind == "fired").RuleName);
        await using var counts = database.DataSource.CreateCommand("SELECT count(*) FROM audit.activity WHERE action_name='alert.rule.retire';");
        Assert.Equal(2L, (long)(await counts.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task LifecycleAdminOperationReusedAfterSourceHistoryRetirementDoesNotBorrowEvaluationAttribution()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid()); Guid ruleId = Guid.NewGuid();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/lifecycle-ledgers"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        await SeedRuleAndDestinationAsync(database, target.Value, ruleId, Guid.NewGuid());
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(item => item.Kind == AlertRuleKind.MetricThreshold);
        var rule = new AlertRuleDefinition(ruleId, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison,
            catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource(), serverSource = database.CreateServerDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorSource); var writer = new PostgreSqlAlertRepositoryPort(serverSource);
        DateTimeOffset start = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-5));
        var (batch, fired) = await PersistClearConfirmationObservationAsync(database, collector, lease, rule, target, start, 2);
        // Retire only the legacy source row in this disposable database. Its immutable
        // forward capture and evaluation replay remain, so the two ledgers can reuse
        // the operation UUID without violating the source-history unique index.
        await using (var retire = database.DataSource.CreateCommand("ALTER TABLE alerting.state_history DISABLE TRIGGER trg_state_history_append_only; DELETE FROM alerting.state_history WHERE instance_id=@target AND operation_id=@operation; ALTER TABLE alerting.state_history ENABLE TRIGGER trg_state_history_append_only;"))
        { retire.Parameters.AddWithValue("target", target.Value); retire.Parameters.AddWithValue("operation", batch.OperationId); await retire.ExecuteNonQueryAsync(); }
        AdministrativeAuditReceipt receipt = await writer.AcknowledgeAsync(new(fired.AlertId!.Value, batch.OperationId.ToString("D"), Audit(target, AdministrativeAuditAction.AcknowledgeAlert), Timeout(), fired.Revision, null, fired.EpisodeId), CancellationToken.None);
        var history = new PostgreSqlAlertLifecycleRepositoryPort(serverSource);
        AlertLifecyclePage page = (await history.ListAsync(new(target.Value, 1, start.AddSeconds(-1), TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(1)), 100, null, Timeout()), CancellationToken.None))!;
        Assert.Equal(2, page.Coverage.EventCount); Assert.Equal(2, page.Coverage.CompleteEventCount);
        AlertLifecycleEvent acknowledged = page.Items.Single(item => item.Kind == "acknowledged");
        Assert.Equal("administrative", acknowledged.Source); Assert.Equal("applied", acknowledged.TimeBasis);
        Assert.Equal(receipt.RecordedAtUtc, acknowledged.RecordedAtUtc); Assert.Equal(receipt.RecordedAtUtc, acknowledged.EventAtUtc);
        Assert.Equal("S-1-5-18", acknowledged.ActorSid); Assert.Equal(fired.EpisodeId, acknowledged.EpisodeId);
        Assert.Null(page.Items.Single(item => item.Kind == "fired").ActorSid);
    }

    [Fact]
    public async Task LifecycleLegacyIdentityPagingAndIndependentExactSelectionNeverBorrowCurrentMetadata()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, _, _, _) = await SeedSnoozeEpisodeAsync(database); Guid alert = Guid.NewGuid();
        DateTimeOffset at = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-30));
        await SeedLegacyLifecycleAsync(database, target.Value, rule, alert, at, 130);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlAlertLifecycleRepositoryPort(server);
        var request = new AlertLifecycleListRequest(target.Value, 1, at.AddSeconds(-1), at.AddMinutes(5), 100, null, Timeout());
        AlertLifecyclePage first = (await repository.ListAsync(request, CancellationToken.None))!;
        Assert.Equal(130, first.Coverage.EventCount); Assert.Equal("partial", first.State); Assert.Equal(130, first.Coverage.LegacyPartialEventCount);
        Assert.All(first.Items, item => { Assert.Null(item.EpisodeId); Assert.Null(item.RecordedAtUtc); Assert.Null(item.RuleName); Assert.Null(item.Value); Assert.Null(item.StateRevision); Assert.Null(item.SourceTargetRevision); });
        AlertLifecyclePage tail = (await repository.ListAsync(request with { Cursor = first.NextAnchor }, CancellationToken.None))!;
        Assert.Equal(30, tail.Items.Count); Assert.Null(tail.NextAnchor);
        AlertLifecycleEvent selected = tail.Items[^1];
        AlertLifecycleExactPage exact = (await repository.GetAsync(new(target.Value, 1, request.FromUtc, request.ToUtc, selected.EventId, alert, null, selected.EventAtUtc, Timeout()), CancellationToken.None))!;
        Assert.Equal(selected, exact.Event);
        Assert.Null((await repository.GetAsync(new(target.Value, 1, request.FromUtc, request.ToUtc, selected.EventId, Guid.NewGuid(), null, selected.EventAtUtc, Timeout()), CancellationToken.None))!.Event);
        Assert.Null((await repository.GetAsync(new(target.Value, 1, request.FromUtc, request.ToUtc, selected.EventId, alert, Guid.NewGuid(), selected.EventAtUtc, Timeout()), CancellationToken.None))!.Event);
        Assert.Null((await repository.GetAsync(new(target.Value, 1, request.FromUtc, request.ToUtc, selected.EventId, alert, null, selected.EventAtUtc.AddTicks(10), Timeout()), CancellationToken.None))!.Event);
        Assert.Equal(130, first.Items.Concat(tail.Items).Select(item => item.EventId).Distinct().Count());
    }

    [Fact]
    public async Task LifecycleManifestRejectsLateCommitRetentionExpiryAndRevisionAndPreservesSameTimeIdentity()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, _, _, _) = await SeedSnoozeEpisodeAsync(database); Guid alert = Guid.NewGuid();
        DateTimeOffset at = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedLegacyLifecycleAsync(database, target.Value, rule, alert, at, 3, sameTime: true);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlAlertLifecycleRepositoryPort(server);
        var request = new AlertLifecycleListRequest(target.Value, 1, at.AddMinutes(-1), at.AddMinutes(1), 1, null, Timeout());
        AlertLifecyclePage first = (await repository.ListAsync(request, CancellationToken.None))!;
        Assert.Equal(3, first.Coverage.EventCount);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListAsync(request with { Cursor = first.NextAnchor, Limit = 2 }, CancellationToken.None).AsTask());
        DateTimeOffset expiredAt = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-16));
        AlertLifecycleCursor expired = first.NextAnchor! with { IssuedAtUtc = expiredAt, CutoffUtc = expiredAt };
        Assert.Equal(409, (await Assert.ThrowsAsync<AlertLifecycleReadException>(() => repository.ListAsync(request with { Cursor = expired }, CancellationToken.None).AsTask())).StatusCode);
        await SeedLegacyLifecycleAsync(database, target.Value, rule, alert, at, 1, sameTime: true);
        Assert.Equal(409, (await Assert.ThrowsAsync<AlertLifecycleReadException>(() => repository.ListAsync(request with { Cursor = first.NextAnchor }, CancellationToken.None).AsTask())).StatusCode);
        AlertLifecyclePage fresh = (await repository.ListAsync(request, CancellationToken.None))!;
        await using (var retire = database.DataSource.CreateCommand("ALTER TABLE alerting.state_history DISABLE TRIGGER trg_state_history_append_only; DELETE FROM alerting.state_history WHERE history_id=(SELECT min(history_id) FROM alerting.state_history WHERE instance_id=@target); ALTER TABLE alerting.state_history ENABLE TRIGGER trg_state_history_append_only;"))
        { retire.Parameters.AddWithValue("target", target.Value); await retire.ExecuteNonQueryAsync(); }
        Assert.Equal(409, (await Assert.ThrowsAsync<AlertLifecycleReadException>(() => repository.ListAsync(request with { Cursor = fresh.NextAnchor }, CancellationToken.None).AsTask())).StatusCode);
        await using (var update = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=revision+1,updated_at=clock_timestamp() WHERE instance_id=@target;"))
        { update.Parameters.AddWithValue("target", target.Value); await update.ExecuteNonQueryAsync(); }
        Assert.Equal(409, (await Assert.ThrowsAsync<AlertLifecycleReadException>(() => repository.ListAsync(request, CancellationToken.None).AsTask())).StatusCode);
    }

    [Theory]
    [InlineData(10001, false)]
    [InlineData(1001, true)]
    public async Task LifecycleOverflowRefusesListButExactEventRemainsIndependent(int count, bool distinctCycles)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, _, _, _) = await SeedSnoozeEpisodeAsync(database); Guid alert = Guid.NewGuid();
        DateTimeOffset at = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-30));
        await SeedLegacyLifecycleAsync(database, target.Value, rule, alert, at, count, sameTime: true, distinctCycles: distinctCycles);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlAlertLifecycleRepositoryPort(server);
        var request = new AlertLifecycleListRequest(target.Value, 1, at.AddSeconds(-1), at.AddSeconds(1), 100, null, Timeout());
        AlertLifecycleReadException error = await Assert.ThrowsAsync<AlertLifecycleReadException>(() => repository.ListAsync(request, CancellationToken.None).AsTask());
        Assert.True(error.StatusCode == 422, $"Expected lifecycle overflow; received {error.Code}: {error.InnerException}");
        await using var id = database.DataSource.CreateCommand("SELECT alerting.lifecycle_event_uuid('history:'||history_id::text),alert_id FROM alerting.state_history WHERE instance_id=@target ORDER BY history_id DESC LIMIT 1;");
        id.Parameters.AddWithValue("target", target.Value); await using NpgsqlDataReader rows = await id.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync());
        Guid eventId = rows.GetGuid(0), selectedAlert = rows.GetGuid(1); await rows.DisposeAsync();
        AlertLifecycleExactPage exact = (await repository.GetAsync(new(target.Value, 1, request.FromUtc, request.ToUtc, eventId, selectedAlert, null, at, Timeout()), CancellationToken.None))!;
        Assert.Equal(eventId, exact.Event!.EventId);
    }

    [Fact]
    public async Task LifecycleNativePermissionsAndScopeForbidDirectTablesHelpersAndForeignTargetReads()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, _, _, _, _) = await SeedSnoozeEpisodeAsync(database);
        await using var grants = database.DataSource.CreateCommand("""
            SELECT NOT has_table_privilege('sqlobserver_server','alerting.lifecycle_provenance','SELECT'),
              NOT has_function_privilege('sqlobserver_collector','reporting.read_alert_lifecycle(uuid,bigint,timestamptz,timestamptz,integer,jsonb,uuid,uuid,uuid,timestamptz)','EXECUTE'),
              NOT has_function_privilege('sqlobserver_server','alerting.lifecycle_history_payload(bigint,boolean)','EXECUTE'),
              bool_and(relrowsecurity AND relforcerowsecurity),
              NOT EXISTS(SELECT 1 FROM pg_proc f CROSS JOIN LATERAL aclexplode(coalesce(f.proacl,acldefault('f',f.proowner))) a
               WHERE f.oid='reporting.read_alert_lifecycle(uuid,bigint,timestamptz,timestamptz,integer,jsonb,uuid,uuid,uuid,timestamptz)'::regprocedure
                AND a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_class WHERE oid IN('alerting.lifecycle_provenance'::regclass,'alerting.lifecycle_capture_boundary'::regclass);
            """);
        await using NpgsqlDataReader row = await grants.ExecuteReaderAsync(); Assert.True(await row.ReadAsync()); for (int i = 0; i < 5; i++) Assert.True(row.GetBoolean(i)); await row.DisposeAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var native = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@other,true); SELECT reporting.read_alert_lifecycle(@target,1,clock_timestamp()-interval '1 day',clock_timestamp(),100,NULL);", connection, transaction);
        native.Parameters.AddWithValue("other", Guid.NewGuid().ToString("D")); native.Parameters.AddWithValue("target", target.Value);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => native.ExecuteNonQueryAsync())).SqlState);
    }

    internal static async Task SeedLegacyLifecycleAsync(RepositoryTestDatabase database, Guid target, Guid rule, Guid alert, DateTimeOffset at,
        int count, bool sameTime = false, bool distinctCycles = false)
    {
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,operation_id)
            SELECT @target,@rule,CASE WHEN @distinct THEN gen_random_uuid() ELSE @alert END,2,3,
             @at+CASE WHEN @same THEN interval '0 seconds' ELSE i*interval '1 second' END,'legacy-fired',gen_random_uuid()
            FROM generate_series(0,@count-1) i;
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("rule", rule); seed.Parameters.AddWithValue("alert", alert);
        seed.Parameters.AddWithValue("at", at); seed.Parameters.AddWithValue("count", count); seed.Parameters.AddWithValue("same", sameTime); seed.Parameters.AddWithValue("distinct", distinctCycles);
        await seed.ExecuteNonQueryAsync();
    }
}
