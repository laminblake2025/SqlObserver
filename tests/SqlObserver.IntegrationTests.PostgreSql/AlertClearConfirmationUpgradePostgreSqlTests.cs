using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    [Fact]
    public async Task ClearConfirmationHistoryFallbackRequiresARecordedResultDigest()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/clear-history"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "connections.history", AlertRuleKind.MetricThreshold,
            new MetricId("engine.user_connections"), AlertComparison.GreaterThan, 20, 2, 1, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await new PostgreSqlAlertRepositoryPort(server).UpsertRuleAsync(new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"),
            Audit(target, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        AlertEvaluationBatch batch = await ClaimClearConfirmationObservationAsync(database, repository, lease, rule,
            target, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1)), 25);
        AlertObservation observation = Assert.Single(batch.Observations);
        // Administrative history may omit result_digest. It cannot stand in for
        // an authenticated evaluation result even if an operation ID collides.
        await using (var history = database.DataSource.CreateCommand("""
            INSERT INTO alerting.state_history(instance_id,rule_id,to_state,observed_at,operation_id,evidence_digest)
            VALUES(@target,@rule,3,@observed,@operation,decode(@digest,'hex'));
            """))
        {
            history.Parameters.AddWithValue("target", target.Value);
            history.Parameters.AddWithValue("rule", rule.RuleId);
            history.Parameters.AddWithValue("observed", observation.ObservedAtUtc);
            history.Parameters.AddWithValue("operation", observation.OperationId);
            history.Parameters.AddWithValue("digest", observation.EvidenceDigest);
            await history.ExecuteNonQueryAsync();
        }
        ReplaySnapshot before = await SnapshotReplayRowsAsync(database, target.Value, rule.RuleId);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => repository.EvaluateAndPersistAsync(batch, CancellationToken.None).AsTask())).SqlState);
        Assert.Equal(before, await SnapshotReplayRowsAsync(database, target.Value, rule.RuleId));
    }

    [Fact]
    public async Task ClearConfirmationRequiresAnExactIntegerCounterOnFreshDecisions()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/clear-counter-shape"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "connections.shape", AlertRuleKind.MetricThreshold,
            new MetricId("engine.user_connections"), AlertComparison.GreaterThan, 20, 2, 1, TimeSpan.Zero,
            TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        await new PostgreSqlAlertRepositoryPort(serverSource).UpsertRuleAsync(new AlertRuleWriteRequest(rule,
            Guid.NewGuid().ToString("D"), Audit(target, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        AlertEvaluationBatch batch = await ClaimClearConfirmationObservationAsync(database, repository, lease,
            rule, target, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1)), 25);
        JsonObject original = ClearDecisionJson(Assert.Single(batch.Decisions!));
        ReplaySnapshot before = await SnapshotReplayRowsAsync(database, target.Value, rule.RuleId);
        foreach (string invalid in new[] { "missing", "null", "\"0\"", "0.5", "-1", "101" })
        {
            JsonObject malformed = (JsonObject)original.DeepClone();
            if (invalid == "missing") malformed["State"]!.AsObject().Remove("ConsecutiveClears");
            else malformed["State"]!["ConsecutiveClears"] = JsonNode.Parse(invalid);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => EvaluateClearJsonAsync(collector, target.Value, lease, malformed))).SqlState);
            Assert.Equal(before, await SnapshotReplayRowsAsync(database, target.Value, rule.RuleId));
        }
        Assert.Equal(1, (await repository.EvaluateAndPersistAsync(batch, CancellationToken.None)).Evaluated);
    }

    [Fact]
    public async Task ClearConfirmationAcceptsDistinctEqualTimeClearsInOneSuppressedBatch()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/clear-batch"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "connections.batch", AlertRuleKind.MetricThreshold,
            new MetricId("engine.user_connections"), AlertComparison.GreaterThan, 20, 2, 1, TimeSpan.Zero,
            TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"),
            Audit(target, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target.Value, Guid.NewGuid());
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        DateTimeOffset start = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-3));
        var fired = await PersistClearConfirmationObservationAsync(database, repository, lease, rule, target, start, 25);
        var window = new MaintenanceWindow(Guid.NewGuid(), target, start.AddSeconds(10), start.AddHours(1), "clear-batch-test");
        await server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(window, Guid.NewGuid().ToString("D"),
            Audit(target, AdministrativeAuditAction.CreateMaintenanceWindow), Timeout()), CancellationToken.None);
        for (int i = 0; i < 2; i++)
        {
            string sample = Guid.NewGuid().ToString("D");
            Guid run = Guid.NewGuid();
            await SeedEvaluationQueueAsync(database, new AlertObservation(target, rule.RuleId, start.AddSeconds(30), 10, null,
                $"observed|sample={sample}|run={run:D}", sampleId: sample, runId: run, sourceKind: "metric_threshold",
                metricId: "engine.user_connections", sourceCollector: "engine.core", sourceVersion: "1", sourceSchemaVersion: 1, sourceDigest: new string('a', 64)));
        }
        IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(lease, 10, Timeout(), CancellationToken.None);
        Assert.Equal(2, work.Count);
        var decisions = new List<AlertEvaluationDecision>();
        AlertRuleState state = fired.State;
        foreach (AlertObservation observation in work.SelectMany(item => item.Observations))
        {
            AlertEvaluationResult result = AlertEvaluator.Evaluate(rule, state, observation, window);
            decisions.Add(new AlertEvaluationDecision(observation, result.State, result.Event, result.DeliverySuppressed, result.Reason));
            state = result.State;
        }
        Assert.Equal(1, decisions[0].State.ConsecutiveClears);
        Assert.Null(decisions[0].Event);
        Assert.Equal(AlertState.Resolved, decisions[1].State.State);
        var batch = new AlertEvaluationBatch(decisions.Select(item => item.Observation).ToArray(), lease, Timeout(), decisions,
            window, work.Min(item => item.DueAtUtc), work);
        AlertEvaluationOutcome outcome = await repository.EvaluateAndPersistAsync(batch, CancellationToken.None);
        Assert.Equal(2, outcome.Evaluated);
        Assert.Equal(2, outcome.Suppressed);
        AlertRuleState persisted = (await repository.GetStateAsync(target, rule.RuleId, Timeout(), CancellationToken.None))!;
        Assert.Equal(AlertState.Resolved, persisted.State);
        Assert.Equal(0, persisted.ConsecutiveClears);
        Assert.Equal(fired.State.AlertId, persisted.AlertId);
        await using var delivery = database.DataSource.CreateCommand("SELECT count(*),bool_and((payload->>'suppressed')::boolean),bool_and(due_at=@end) FROM alerting.delivery_outbox WHERE instance_id=@target AND event_kind=2;");
        delivery.Parameters.AddWithValue("target", target.Value);
        delivery.Parameters.AddWithValue("end", window.EndsAtUtc);
        await using NpgsqlDataReader reader = await delivery.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task ClearConfirmationUpgradePreservesLegacyConfigurationAcknowledgementAndExactReplays()
    {
        await using RepositoryTestDatabase database = await CreateBeforeClearConfirmationAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        Guid ruleId = Guid.NewGuid();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/clear-upgrade"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target.Value, lease);
        var rule = new AlertRuleDefinition(ruleId, "connections.upgrade", AlertRuleKind.MetricThreshold,
            new MetricId("engine.user_connections"), AlertComparison.GreaterThan, 20, 2, 1, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        string adminOperation = Guid.NewGuid().ToString("D");
        string legacyRule = JsonSerializer.Serialize(new
        {
            Action = "CreateAlertRule", RuleId = ruleId, TargetId = target.Value, rule.Name,
            Kind = (int)rule.Kind, MetricId = rule.MetricId!.Value, Comparison = (int)rule.Comparison,
            rule.Threshold, rule.Hysteresis, rule.ConfirmationCount, rule.ConfirmationWindow, rule.EvaluationInterval,
            rule.Enabled, SourceCollector = "engine.core", SourceSchemaVersion = 1,
            CatalogDigest = "f28dab1e5bd65bf13f972a887b98742fe15eeea1dd7b13ac7ba0e8b6f0485d91",
            ExpectedRevision = (long?)null, RequestDigest = (string?)null,
        });
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        Guid audit = await WriteLegacyRuleAsync(serverSource, target.Value, legacyRule, adminOperation);
        string sample = Guid.NewGuid().ToString("D");
        Guid run = Guid.NewGuid();
        var observation = new AlertObservation(target, ruleId, TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1)), 25, null,
            $"observed|sample={sample}|run={run:D}", sampleId: sample, runId: run, sourceKind: "metric_threshold",
            metricId: "engine.user_connections", sourceCollector: "engine.core", sourceVersion: "1", sourceSchemaVersion: 1, sourceDigest: new string('a', 64));
        await SeedEvaluationQueueAsync(database, observation);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        AlertEvaluationWork work = Assert.Single(await repository.ClaimDueEvaluationsAsync(lease, 10, Timeout(), CancellationToken.None));
        AlertEvaluationResult evaluated = AlertEvaluator.Evaluate(rule, new AlertRuleState(ruleId, target), observation);
        var decision = new AlertEvaluationDecision(observation, evaluated.State, evaluated.Event, evaluated.DeliverySuppressed, evaluated.Reason);
        JsonObject legacyDecision = ClearDecisionJson(decision);
        legacyDecision["State"]!.AsObject().Remove("ConsecutiveClears");
        Assert.Equal(1, await EvaluateClearJsonAsync(collector, target.Value, lease, legacyDecision));
        var server = new PostgreSqlAlertRepositoryPort(serverSource);
        await server.AcknowledgeAsync(new AlertAcknowledgeRequest(evaluated.State.AlertId!.Value, Guid.NewGuid().ToString("D"),
            Audit(target, AdministrativeAuditAction.AcknowledgeAlert), Timeout(), evaluated.State.Revision, null), CancellationToken.None);
        string oldState = await ClearScalarAsync(database, "SELECT to_jsonb(s)::text FROM alerting.rule_state s;");
        const string historySql = "SELECT jsonb_agg(to_jsonb(h) ORDER BY h.history_id)::text FROM alerting.state_history h;";
        const string replaySql = "SELECT jsonb_agg(to_jsonb(r) ORDER BY r.operation_id)::text FROM alerting.evaluation_replay r;";
        string oldHistory = await ClearScalarAsync(database, historySql);
        string oldReplay = await ClearScalarAsync(database, replaySql);
        string oldRule = await ClearScalarAsync(database, "SELECT to_jsonb(r)::text FROM alerting.rule r;");

        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout()), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(83, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(oldRule, await ClearScalarAsync(database, "SELECT (to_jsonb(r)-'clear_confirmation_count')::text FROM alerting.rule r;"));
        Assert.Equal(oldState, await ClearScalarAsync(database, "SELECT (to_jsonb(s)-'consecutive_clears')::text FROM alerting.rule_state s;"));
        AlertRuleDefinition loadedRule = Assert.Single(await repository.ListRulesAsync(target, Timeout(), CancellationToken.None));
        Assert.Equal(1, loadedRule.ClearConfirmationCount);
        AlertRuleState loadedState = (await repository.GetStateAsync(target, ruleId, Timeout(), CancellationToken.None))!;
        Assert.Equal(AlertState.Acknowledged, loadedState.State);
        Assert.Equal(0, loadedState.ConsecutiveClears);
        Assert.Equal(evaluated.State.AlertId, loadedState.AlertId);
        Assert.Equal("S-1-5-18", loadedState.AcknowledgedBy);

        ReplaySnapshot upgraded = await SnapshotReplayRowsAsync(database, target.Value, ruleId);
        Assert.Equal(oldHistory, await ClearScalarAsync(database, historySql));
        Assert.Equal(oldReplay, await ClearScalarAsync(database, replaySql));
        Assert.Equal(audit, await WriteLegacyRuleAsync(serverSource, target.Value, legacyRule, adminOperation));
        Assert.Equal(0, await EvaluateClearJsonAsync(collector, target.Value, lease, legacyDecision));
        var batch = new AlertEvaluationBatch([observation], lease, Timeout(), [decision], null, work.DueAtUtc, [work]);
        Assert.Equal(0, (await repository.EvaluateAndPersistAsync(batch, CancellationToken.None)).Evaluated);
        Assert.Equal(upgraded, await SnapshotReplayRowsAsync(database, target.Value, ruleId));
        JsonObject tampered = ClearDecisionJson(decision);
        tampered["State"]!["ConsecutiveClears"] = 1;
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => EvaluateClearJsonAsync(collector, target.Value, lease, tampered))).SqlState);
        Assert.Equal(upgraded, await SnapshotReplayRowsAsync(database, target.Value, ruleId));

        var changedRule = new AlertRuleDefinition(ruleId, rule.Name, rule.Kind, rule.MetricId, rule.Comparison,
            rule.Threshold, rule.Hysteresis, rule.ConfirmationCount, rule.ConfirmationWindow, rule.EvaluationInterval, clearConfirmationCount: 2);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(changedRule, Guid.NewGuid().ToString("D"),
            Audit(target, AdministrativeAuditAction.UpdateAlertRule), Timeout(), 1), CancellationToken.None);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => EvaluateClearJsonAsync(collector, target.Value, lease, legacyDecision))).SqlState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearConfirmationUpgradeRejectsCatalogDriftWithoutPartialSchemaChanges(bool missing)
    {
        await using RepositoryTestDatabase database = await CreateBeforeClearConfirmationAsync();
        await using (var mutate = database.DataSource.CreateCommand(missing
            ? "DELETE FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts';"
            : "UPDATE alerting.catalog_registry SET catalog_digest=repeat('f',64) WHERE catalog_id='sqlobserver.m8.alerts';"))
            await mutate.ExecuteNonQueryAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout()), CancellationToken.None);
        Assert.True(result.HasFailures);
        await using var verify = database.DataSource.CreateCommand("""
            SELECT NOT EXISTS(SELECT FROM information_schema.columns WHERE table_schema='alerting' AND column_name IN ('consecutive_clears','clear_confirmation_count')),
              to_regprocedure('alerting.list_rules_v2(uuid)') IS NULL,
              to_regprocedure('alerting.get_rule_state_v2(uuid,uuid)') IS NULL,
              coalesce((SELECT catalog_digest FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts'),'missing');
            """);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int i = 0; i < 3; i++) Assert.True(reader.GetBoolean(i));
        Assert.Equal(missing ? "missing" : new string('f', 64), reader.GetString(3));
    }

    [Fact]
    public async Task ClearConfirmationReadersRetainScopedCollectorOnlyPermissions()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Assert.Equal("fc53c14e0ad3d15364c0c02846cd93d3e020f25dc378b653ffc4c77a393bc0dd", AlertCatalog.Digest);
        Assert.All(AlertCatalog.Entries, entry => Assert.Equal(2, entry.ClearConfirmationCount));
        await using var verify = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator',p.prosecdef,
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              NOT EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE'),
              'search_path=pg_catalog, public, alerting'=ANY(p.proconfig)
            FROM pg_proc p WHERE p.oid IN ('alerting.list_rules_v2(uuid)'::regprocedure,'alerting.get_rule_state_v2(uuid,uuid)'::regprocedure);
            """);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
        int count = 0;
        while (await reader.ReadAsync()) { count++; for (int i = 0; i < 7; i++) Assert.True(reader.GetBoolean(i)); }
        Assert.Equal(2, count);
    }

    private async Task<RepositoryTestDatabase> CreateBeforeClearConfirmationAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new MigrationApplyRequest(82, Timeout()), CancellationToken.None);
            Assert.False(result.HasFailures);
            await using var partitions = database.DataSource.CreateCommand("SELECT control.ensure_daily_metric_partition(current_date-1); SELECT control.ensure_daily_metric_partition(current_date);");
            await partitions.ExecuteNonQueryAsync();
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static JsonObject ClearDecisionJson(AlertEvaluationDecision decision)
    {
        JsonObject observation = JsonSerializer.SerializeToNode(decision.Observation)!.AsObject();
        observation["TargetId"] = decision.Observation.TargetId.Value;
        JsonObject state = JsonSerializer.SerializeToNode(decision.State)!.AsObject();
        state["TargetId"] = decision.State.TargetId.Value;
        return new JsonObject { ["Observation"] = observation, ["State"] = state,
            ["Event"] = decision.Event is null ? null : (int)decision.Event.Value,
            ["DeliverySuppressed"] = decision.DeliverySuppressed, ["Reason"] = decision.Reason };
    }

    private static async Task<int> EvaluateClearJsonAsync(NpgsqlDataSource source, Guid target, WorkerLeaseIdentity lease, JsonObject decision)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true); SET LOCAL TimeZone='UTC';", connection, transaction))
        { scope.Parameters.AddWithValue("scope", target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("SELECT evaluated_count FROM alerting.evaluate_and_enqueue(@target,@observations,@decisions,@key,@owner,@fence);", connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.Add(new NpgsqlParameter("observations", NpgsqlDbType.Jsonb) { Value = new JsonArray(decision["Observation"]!.DeepClone()).ToJsonString() });
        command.Parameters.Add(new NpgsqlParameter("decisions", NpgsqlDbType.Jsonb) { Value = new JsonArray(decision.DeepClone()).ToJsonString() });
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        int count = (int)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static async Task<Guid> WriteLegacyRuleAsync(NpgsqlDataSource source, Guid target, string payload, string operation)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
        { scope.Parameters.AddWithValue("scope", target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("SELECT audit_id FROM alerting.upsert_rule(@rule,@operation,'S-1-5-18',@correlation);", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("rule", NpgsqlDbType.Jsonb) { Value = payload });
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("correlation", Guid.NewGuid());
        Guid audit = (Guid)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return audit;
    }

    private static async Task<string> ClearScalarAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
}
