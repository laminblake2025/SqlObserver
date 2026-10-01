using System.Reflection;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task DeadlockTransitionsMatchProductionAcrossCountBoundariesStatesGapsAndMaintenance()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        int combinations = 0;
        foreach (AlertState state in Enum.GetValues<AlertState>())
            foreach (int? count in new int?[] { null, 0, 1, 2, 3, 4, 256 })
                foreach (AlertComparison comparison in new[] { AlertComparison.GreaterThan, AlertComparison.GreaterThanOrEqual })
                    foreach (int hysteresis in new[] { 0, 1 })
                        foreach (int clears in new[] { 1, 2 })
                            foreach (int gapSeconds in new[] { 15, 300, 301 })
                                foreach (bool suppressed in new[] { false, true })
                                {
                                    var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.count", AlertRuleKind.DeadlockCount, null,
                                        comparison, 3, hysteresis, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: clears);
                                    bool active = state is AlertState.Firing or AlertState.Acknowledged, episode = active || state == AlertState.Resolved;
                                    var prior = new AlertRuleState(rule.RuleId, target, state, state == AlertState.Pending ? 1 : active ? 2 : 0,
                                        firstMatchUtc: active || state == AlertState.Pending ? at : null, lastObservedUtc: at, firedUtc: episode ? at : null,
                                        acknowledgedUtc: state == AlertState.Acknowledged ? at : null, alertId: episode ? Guid.NewGuid() : null,
                                        episodeId: episode ? Guid.NewGuid() : null, resolvedUtc: state == AlertState.Resolved ? at : null,
                                        episodeStartedUtc: episode ? at : null, acknowledgedBy: state == AlertState.Acknowledged ? "operator" : null,
                                        revision: 7, lastValue: 4, consecutiveClears: active && clears == 2 ? 1 : 0);
                                    DateTimeOffset now = at.AddSeconds(gapSeconds);
                                    AlertObservation observation = DeadlockTransitionObservation(rule, target, now, count);
                                    MaintenanceWindow? maintenance = suppressed ? new(Guid.NewGuid(), target, at.AddMinutes(-1), now.AddMinutes(1), "planned") : null;
                                    JsonObject expected = DeadlockDecisionWire(observation, AlertEvaluator.Evaluate(rule, prior, observation, maintenance));
                                    JsonObject before = DeadlockDecisionWire(observation, new(prior, null, false, "prior"));
                                    await using var command = database.DataSource.CreateCommand("""
                SELECT alerting.deadlock_alert_transition(@comparison,3,@hysteresis,2,@clears,interval '5 minutes',
                  @prior,@observation,@suppressed)=alerting.canonical_decision_snapshot(@expected);
                """);
                                    command.Parameters.AddWithValue("comparison", (short)comparison);
                                    command.Parameters.AddWithValue("hysteresis", (double)hysteresis);
                                    command.Parameters.AddWithValue("clears", clears);
                                    command.Parameters.AddWithValue("suppressed", suppressed);
                                    command.Parameters.AddWithValue("prior", NpgsqlDbType.Jsonb, before["State"]!.ToJsonString());
                                    command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, expected["Observation"]!.ToJsonString());
                                    command.Parameters.AddWithValue("expected", NpgsqlDbType.Jsonb, expected.ToJsonString());
                                    Assert.True((bool)(await command.ExecuteScalarAsync())!,
                                        $"state={state}, count={count}, comparison={comparison}, hysteresis={hysteresis}, clears={clears}, gap={gapSeconds}, suppressed={suppressed}");
                                    combinations++;
                                }
        Assert.Equal(1680, combinations);
    }

    [Fact]
    public async Task DeadlockTransitionRejectsForgedCountsBindingsRegressedTimeAndRuntimeCallers()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.count", AlertRuleKind.DeadlockCount, null,
            AlertComparison.GreaterThan, 3, 1, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        AlertObservation observation = DeadlockTransitionObservation(rule, target, at, 4);
        JsonObject original = DeadlockDecisionWire(observation, new(new(rule.RuleId, target), null, false, "prior"));
        async Task Invoke(JsonObject wire, double threshold = 3, double hysteresis = 1)
        {
            await using var command = database.DataSource.CreateCommand("""
                SELECT alerting.deadlock_alert_transition(1::smallint,@threshold,@hysteresis,2,2,interval '5 minutes',
                    @prior,@observation,false);
                """);
            command.Parameters.AddWithValue("threshold", threshold);
            command.Parameters.AddWithValue("hysteresis", hysteresis);
            command.Parameters.AddWithValue("prior", NpgsqlDbType.Jsonb, wire["State"]!.ToJsonString());
            command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, wire["Observation"]!.ToJsonString());
            await command.ExecuteScalarAsync();
        }
        await Invoke(original);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Invoke(original, 3.5))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Invoke(original, hysteresis: 0.5))).SqlState);
        foreach (double count in new[] { 0.5, 257d })
        {
            // Recompute the evidence digest: rejection must bind the count
            // contract, rather than merely detect a stale payload checksum.
            var forged = new AlertObservation(target, rule.RuleId, at, count, null, observation.Reason,
                sampleId: observation.SampleId, runId: observation.RunId, sourceKind: observation.SourceKind,
                sourceCollector: observation.SourceCollector, sourceVersion: "1", sourceSchemaVersion: 1,
                sourceDigest: observation.SourceDigest);
            JsonObject wire = DeadlockDecisionWire(forged, new(new(rule.RuleId, target), null, false, "prior"));
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Invoke(wire))).SqlState);
        }
        foreach (string field in new[] { "SourceCollector", "RunId", "OperationId", "Value" })
        {
            var forged = (JsonObject)original.DeepClone();
            forged["Observation"]![field] = field switch
            {
                "SourceCollector" => "blocking.current",
                "OperationId" => JsonValue.Create(Guid.NewGuid()),
                _ => null,
            };
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Invoke(forged))).SqlState);
        }
        var regressed = (JsonObject)original.DeepClone();
        regressed["State"]!["LastObservedUtc"] = JsonValue.Create(at);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Invoke(regressed))).SqlState);
        await using (var privileges = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner),
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='alerting.deadlock_alert_transition(smallint,double precision,double precision,integer,integer,interval,jsonb,jsonb,boolean)'::regprocedure;
            """))
        {
            await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); Assert.Equal("sqlobserver_migrator", row.GetString(0));
            for (int column = 1; column <= 4; column++) Assert.False(row.GetBoolean(column));
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using var denied = collector.CreateCommand("""
            SELECT alerting.deadlock_alert_transition(1::smallint,3,1,2,2,interval '5 minutes','{}','{}',false);
            """);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await denied.ExecuteScalarAsync())).SqlState);
    }

    private static AlertObservation DeadlockTransitionObservation(AlertRuleDefinition rule, MonitoredInstanceId target,
        DateTimeOffset now, int? count) => DeadlockAlertObservationFactory.Create(rule,
        new(target, 1, now, count is null ? null : now.AddMinutes(1), 1, Guid.NewGuid(), now.AddSeconds(-1),
            count is null ? null : now.AddSeconds(-1).AddMinutes(-5), count is null ? null : now.AddSeconds(-1),
            count, count, count is null ? DeadlockAlertEvidenceReason.SourceIncomplete : DeadlockAlertEvidenceReason.Current,
            new string('b', 64), new string('a', 64)));

    private static JsonObject DeadlockDecisionWire(AlertObservation observation, AlertEvaluationResult result)
    {
        var decision = new AlertEvaluationDecision(observation, result.State, result.Event, result.DeliverySuppressed, result.Reason);
        MethodInfo serialize = typeof(PostgreSqlAlertRepositoryPort).GetMethod("SerializeDecisions", BindingFlags.NonPublic | BindingFlags.Static)!;
        return JsonNode.Parse((string)serialize.Invoke(null, [new[] { decision }])!)![0]!.AsObject();
    }
}
