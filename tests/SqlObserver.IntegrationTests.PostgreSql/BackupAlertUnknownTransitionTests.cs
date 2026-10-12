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

public sealed partial class BackupEvidenceReadTests
{
    [Fact]
    public async Task UnknownBackupSqlTransitionMatchesTheProductionWireForEveryKindStateAndMaintenanceCase()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        foreach (AlertRuleKind kind in new[] { AlertRuleKind.BackupFullPolicy, AlertRuleKind.BackupLogPolicy, AlertRuleKind.BackupObservedFaults })
        foreach (AlertState state in Enum.GetValues<AlertState>())
        foreach (bool suppressed in new[] { false, true })
        {
            var rule = new AlertRuleDefinition(Guid.NewGuid(), "backup.transition", kind, null,
                AlertComparison.GreaterThan, 0, 0, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
            AlertRuleState prior = BackupPrior(rule.RuleId, target, state, at);
            AlertObservation observation = BackupAlertObservationFactory.Create(rule, new(target, 1, at.AddMinutes(1), null, null));
            MaintenanceWindow? maintenance = suppressed ? new(Guid.NewGuid(), target, at, at.AddHours(1), "planned") : null;
            AlertEvaluationResult evaluated = AlertEvaluator.Evaluate(rule, prior, observation, maintenance);
            JsonObject expected = BackupDecisionWire(observation, evaluated);
            JsonObject before = BackupDecisionWire(observation, new(prior, null, prior.DeliverySuppressed, "prior"));
            await using var command = database.DataSource.CreateCommand("""
                SELECT alerting.canonical_decision_snapshot(@expected) =
                    alerting.backup_unknown_transition(@kind,@prior,@observation,@suppressed);
                """);
            command.Parameters.AddWithValue("kind", (short)kind);
            command.Parameters.AddWithValue("prior", NpgsqlDbType.Jsonb, before["State"]!.ToJsonString());
            command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, expected["Observation"]!.ToJsonString());
            command.Parameters.AddWithValue("expected", NpgsqlDbType.Jsonb, expected.ToJsonString());
            command.Parameters.AddWithValue("suppressed", suppressed);
            Assert.Equal(true, await command.ExecuteScalarAsync());
            Assert.Null(evaluated.Event);
            Assert.Equal(0, evaluated.State.ConsecutiveMatches); Assert.Equal(0, evaluated.State.ConsecutiveClears);
        }
        await using var privilege = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner),p.prosecdef,
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='alerting.backup_unknown_transition(smallint,jsonb,jsonb,boolean)'::regprocedure;
            """);
        await using NpgsqlDataReader row = await privilege.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal("sqlobserver_migrator", row.GetString(0)); Assert.True(row.GetBoolean(1));
        Assert.False(row.GetBoolean(2)); Assert.False(row.GetBoolean(3)); Assert.False(row.GetBoolean(4)); Assert.False(row.GetBoolean(5));
    }

    [Fact]
    public async Task UnknownBackupSqlTransitionRejectsMalformedBindingsKnownValuesAndRegressedTimes()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "backup.transition", AlertRuleKind.BackupFullPolicy, null,
            AlertComparison.GreaterThan, 0, 0, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        AlertRuleState prior = BackupPrior(rule.RuleId, target, AlertState.Acknowledged, at);
        AlertObservation observation = BackupAlertObservationFactory.Create(rule, new(target, 1, at.AddMinutes(1), null, null));
        JsonObject wire = BackupDecisionWire(observation, AlertEvaluator.Evaluate(rule, prior, observation));
        JsonObject original = BackupDecisionWire(observation, new(prior, null, false, "prior"));
        for (int defect = 0; defect < 10; defect++)
        {
            JsonObject body = wire["Observation"]!.DeepClone().AsObject();
            JsonObject state = original["State"]!.DeepClone().AsObject();
            switch (defect)
            {
                case 0: body["Value"] = 0; break;
                case 1: body["SourceKind"] = "collector_health"; break;
                case 2: body["TargetId"] = Guid.NewGuid().ToString("D"); break;
                case 3: body["Reason"] = "changed without digest"; break;
                case 4: body.AsObject().Remove("SampleId"); break;
                case 5: body["extra"] = true; break;
                case 6: state["LastObservedUtc"] = observation.ObservedAtUtc.ToString("O"); break;
                case 7: body["ObservedAtUtc"] = "2026-09-30T12:01:00.0000001Z"; break;
                case 8: body["TargetId"] = null; state["TargetId"] = null; break;
                case 9: state["ConsecutiveClears"] = -1; break;
            }
            await using var command = database.DataSource.CreateCommand(
                "SELECT alerting.backup_unknown_transition(3::smallint,@prior,@observation,false);");
            command.Parameters.AddWithValue("prior", NpgsqlDbType.Jsonb, state.ToJsonString());
            command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, body.ToJsonString());
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteScalarAsync())).SqlState);
        }
    }

    private static JsonObject BackupDecisionWire(AlertObservation observation, AlertEvaluationResult result)
    {
        // Use the actual production serializer, including every nullable field,
        // rather than maintaining a second hand-written approximation in the test.
        var decision = new AlertEvaluationDecision(observation, result.State, result.Event, result.DeliverySuppressed, result.Reason);
        MethodInfo serialize = typeof(PostgreSqlAlertRepositoryPort).GetMethod("SerializeDecisions", BindingFlags.NonPublic | BindingFlags.Static)!;
        string json = (string)serialize.Invoke(null, [new[] { decision }])!;
        return JsonNode.Parse(json)![0]!.AsObject();
    }

    private static AlertRuleState BackupPrior(Guid rule, MonitoredInstanceId target, AlertState state, DateTimeOffset at)
    {
        bool episode = state is AlertState.Firing or AlertState.Acknowledged or AlertState.Resolved;
        return new(rule, target, state, state is AlertState.Pending ? 1 : state is AlertState.Firing or AlertState.Acknowledged ? 2 : 0,
            firstMatchUtc: state is AlertState.Pending or AlertState.Firing or AlertState.Acknowledged ? at : null,
            lastObservedUtc: at, firedUtc: episode ? at : null,
            acknowledgedUtc: state == AlertState.Acknowledged ? at : null,
            alertId: episode ? Guid.NewGuid() : null, episodeId: episode ? Guid.NewGuid() : null,
            resolvedUtc: state == AlertState.Resolved ? at : null,
            episodeStartedUtc: episode ? at : null, acknowledgedBy: state == AlertState.Acknowledged ? "S-1-5-21-1" : null,
            revision: 7, lastValue: 1, consecutiveClears: state is AlertState.Firing or AlertState.Acknowledged ? 1 : 0);
    }
}
