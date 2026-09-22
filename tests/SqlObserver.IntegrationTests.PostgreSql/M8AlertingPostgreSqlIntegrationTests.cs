using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>
/// Executable M8 contract coverage against the real PostgreSQL fixture.  These
/// tests intentionally use the migration runner and SQL functions, rather than
/// mocks, so Docker-enabled CI exercises grants, RLS, fencing and replay.
/// </summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M8AlertingPostgreSqlIntegrationTests
{
    private sealed record ReplaySnapshot(string RuleState, string Outbox, string History, string Replay, string EvaluationQueue);

    private readonly PostgreSql18Fixture _fixture;
    public M8AlertingPostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewConnectionRuleDoesNotReplayLoadBeforeItsConfiguration(bool previouslyScopedConnection)
    {
        await using var database = await CreateMigratedDatabaseAsync();
        Guid target=Guid.NewGuid(),rule=Guid.NewGuid(),destination=Guid.NewGuid();
        var lease=new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/activation-test"),new WorkerExecutionId(Guid.NewGuid()),new FencingToken(1));
        await SeedTargetAndLeaseAsync(database,target,lease);
        await SeedRuleAndDestinationAsync(database,target,rule,destination);
        await using var clock=database.DataSource.CreateCommand("SELECT clock_timestamp()");
        var now=new DateTimeOffset((DateTime)(await clock.ExecuteScalarAsync())!);
        Guid? currentOperation=null;
        foreach(var observed in new[] { now.AddMinutes(-5),now })
        {
            string sample=Guid.NewGuid().ToString("D");Guid run=Guid.NewGuid();
            var evidence=new AlertObservation(new MonitoredInstanceId(target),rule,observed,25,null,$"observed|sample={sample}|run={run:D}",sampleId:sample,runId:run,sourceKind:"metric_threshold",metricId:"engine.user_connections",sourceCollector:"engine.core",sourceVersion:"1",sourceSchemaVersion:1,sourceDigest:new string('a',64));
            await SeedEvaluationQueueAsync(database,evidence);
            if(observed==now)currentOperation=evidence.OperationId;
        }
        // Seed source observations only; let the production reconciler admit work.
        await using var clearSeededQueue=database.DataSource.CreateCommand("DELETE FROM alerting.evaluation_queue");
        await clearSeededQueue.ExecuteNonQueryAsync();
        await using (var olderHistory=database.DataSource.CreateCommand("""
            INSERT INTO telemetry.raw_metric_sample(observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
            SELECT old.observed_at,gen_random_uuid(),old.instance_id,old.metric_key,old.metric_value,old.dimensions,old.collected_at,old.collection_run_id
            FROM (SELECT * FROM telemetry.raw_metric_sample WHERE instance_id=@target ORDER BY observed_at LIMIT 1) old
            CROSS JOIN generate_series(1,20000);
            ANALYZE telemetry.raw_metric_sample;
            """))
        {
            olderHistory.Parameters.AddWithValue("target",target);
            await olderHistory.ExecuteNonQueryAsync();
        }
        await using var collector=database.CreateCollectorDataSource(previouslyScopedConnection);
        var work=await new PostgreSqlAlertRepositoryPort(collector).ClaimDueEvaluationsAsync(lease,10,new RepositoryCallTimeout(TimeSpan.FromSeconds(5)),CancellationToken.None);
        Assert.Equal(currentOperation,Assert.Single(work).OperationId);
    }

    [Fact]
    public async Task SustainedFiringResolvesReturnsToNormalAndCreatesANewEpisode()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), ruleId = Guid.NewGuid();
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/lifecycle-test"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        var rule = new AlertRuleDefinition(ruleId,"connections.lifecycle",AlertRuleKind.MetricThreshold,new MetricId("engine.user_connections"),AlertComparison.GreaterThan,20,2,2,TimeSpan.FromSeconds(90),TimeSpan.FromSeconds(15));
        await using var serverSource = database.CreateServerDataSource();
        await new PostgreSqlAlertRepositoryPort(serverSource).UpsertRuleAsync(new AlertRuleWriteRequest(rule,Guid.NewGuid().ToString("D"),Audit(targetId,AdministrativeAuditAction.CreateAlertRule),Timeout()),CancellationToken.None);
        await using var collectorSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorSource);
        var start = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var steps = new[] { (0,10d,AlertState.Normal),(30,25d,AlertState.Pending),(60,25d,AlertState.Firing),(180,25d,AlertState.Firing),(210,10d,AlertState.Resolved),(240,10d,AlertState.Normal),(270,25d,AlertState.Pending),(300,25d,AlertState.Firing) };
        Guid? firstAlert = null;
        foreach (var (seconds,value,expected) in steps)
        {
            string sample = Guid.NewGuid().ToString("D"); Guid run = Guid.NewGuid();
            var observation = new AlertObservation(targetId,ruleId,start.AddSeconds(seconds),value,null,$"observed|sample={sample}|run={run:D}",sampleId:sample,runId:run,sourceKind:"metric_threshold",metricId:"engine.user_connections",sourceCollector:"engine.core",sourceVersion:"1",sourceSchemaVersion:1,sourceDigest:new string('a',64));
            await SeedEvaluationQueueAsync(database,observation);
            var work = Assert.Single(await repository.ClaimDueEvaluationsAsync(lease,10,Timeout(),CancellationToken.None));
            var prior = await repository.GetStateAsync(targetId,ruleId,Timeout(),CancellationToken.None) ?? new AlertRuleState(ruleId,targetId);
            var evaluated = AlertEvaluator.Evaluate(rule,prior,observation);
            Assert.Equal(expected,evaluated.State.State);
            var decision = new AlertEvaluationDecision(observation,evaluated.State,evaluated.Event,evaluated.DeliverySuppressed,evaluated.Reason);
            await repository.EvaluateAndPersistAsync(new AlertEvaluationBatch(new[] { observation },lease,Timeout(),new[] { decision },null,work.DueAtUtc,new[] { work }),CancellationToken.None);
            var persisted = await repository.GetStateAsync(targetId,ruleId,Timeout(),CancellationToken.None);
            Assert.Equal(expected,persisted!.State);
            if(seconds==60) firstAlert=persisted.AlertId;
            if(seconds==240) { Assert.Null(persisted.AlertId); Assert.Null(persisted.EpisodeStartedUtc); }
            if(seconds==300) { Assert.NotNull(persisted.AlertId); Assert.NotEqual(firstAlert,persisted.AlertId); }
        }
    }

    [Fact]
    public async Task DeliverySnapshotsRemainScopedAcrossMultipleClaimedTargets()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await SeedTargetAndLeaseAsync(database, first, lease);
        await SeedTargetOnlyAsync(database, second);
        foreach (Guid target in new[] { first, second })
        {
            Guid rule = Guid.NewGuid(), destination = Guid.NewGuid();
            await SeedRuleAndDestinationAsync(database, target, rule, destination);
            await SeedOutboxAsync(database, target, rule, destination, Guid.NewGuid(), Guid.NewGuid());
        }
        await using var collector = database.CreateCollectorDataSource();
        var work = await new PostgreSqlAlertRepositoryPort(collector).ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None);
        Assert.Equal(2, work.Count);
        Assert.Equal(2, work.Select(item => item.TargetId!.Value).Distinct().Count());
        Assert.All(work, item => Assert.Equal(new string('d', 64), item.ConfigurationDigest));
        await using var privileges = database.DataSource.CreateCommand("SELECT has_column_privilege('sqlobserver_collector','alerting.delivery_outbox','destination_configuration_digest','SELECT'),has_column_privilege('sqlobserver_collector','alerting.delivery_outbox','payload','SELECT'),has_table_privilege('sqlobserver_collector','alerting.delivery_outbox','UPDATE')");
        await using var reader = await privileges.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0)); Assert.False(reader.GetBoolean(1)); Assert.False(reader.GetBoolean(2));
    }

    [Fact]
    public async Task FreshInstallExposesFencedScopedBootstrapAndAtomicDeliveryFunctions()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            SELECT
              to_regprocedure('alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer)') IS NOT NULL,
              to_regprocedure('alerting.renew_delivery_with_outcome(uuid,uuid,text,uuid,bigint)') IS NOT NULL,
              NOT has_function_privilege('sqlobserver_server', 'alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer)', 'EXECUTE'),
              has_function_privilege('sqlobserver_collector', 'alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer)', 'EXECUTE'),
              NOT has_function_privilege('sqlobserver_server', 'alerting.assert_evaluation_claims(uuid,jsonb,text,uuid,bigint)', 'EXECUTE'),
              NOT has_function_privilege('sqlobserver_collector', 'alerting.assert_evaluation_claims(uuid,jsonb,text,uuid,bigint)', 'EXECUTE'),
              NOT has_function_privilege('sqlobserver_auditor', 'alerting.assert_evaluation_claims(uuid,jsonb,text,uuid,bigint)', 'EXECUTE');
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
    }

    [Fact]
    public async Task M8MigrationContainsReplayAndCanonicalHealthBindings()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('alerting.evaluation_replay') IS NOT NULL, to_regclass('telemetry.collection_run_outcome') IS NOT NULL;", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task RestrictedCollectorReadsScopedRulesAndActiveRowsAndCannotMutateM8TablesOrFunctions()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("36363636-3636-4363-8363-363636363636");
        Guid otherTarget = Guid.Parse("37373737-3737-4373-8373-373737373737");
        Guid rule = Guid.Parse("38383838-3838-4383-8383-383838383838");
        Guid otherRule = Guid.Parse("39393939-3939-4393-8393-393939393939");
        Guid destination = Guid.Parse("3a3a3a3a-3a3a-43aa-83aa-3a3a3a3a3a3a");
        Guid otherDestination = Guid.Parse("3b3b3b3b-3b3b-43bb-83bb-3b3b3b3b3b3b");
        await SeedTargetOnlyAsync(database, target);
        await SeedTargetOnlyAsync(database, otherTarget);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var targetRule = new AlertRuleDefinition(rule, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        var otherRuleDefinition = new AlertRuleDefinition(otherRule, targetRule.Name, targetRule.Kind, targetRule.MetricId, targetRule.Comparison, targetRule.Threshold, targetRule.Hysteresis, targetRule.ConfirmationCount, targetRule.ConfirmationWindow, targetRule.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(targetRule, Guid.NewGuid().ToString("D"), Audit(new MonitoredInstanceId(target), AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(otherRuleDefinition, Guid.NewGuid().ToString("D"), Audit(new MonitoredInstanceId(otherTarget), AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destination);
        await SeedApprovedDestinationAsync(database, otherTarget, otherDestination);
        await SeedActiveRuleStateAsync(database, target, rule);
        await SeedActiveRuleStateAsync(database, otherTarget, otherRule);
        await SeedOutboxAsync(database, target, rule, destination, Guid.Parse("3c3c3c3c-3c3c-43cc-83cc-3c3c3c3c3c3c"), Guid.Parse("3d3d3d3d-3d3d-43dd-83dd-3d3d3d3d3d3d"));
        await SeedOutboxAsync(database, otherTarget, otherRule, otherDestination, Guid.Parse("3e3e3e3e-3e3e-43ee-83ee-3e3e3e3e3e3e"), Guid.Parse("3f3f3f3f-3f3f-43ff-83ff-3f3f3f3f3f3f"));

        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        Assert.Equal(rule, Assert.Single(await repository.ListRulesAsync(new MonitoredInstanceId(target), Timeout(), CancellationToken.None)).RuleId);
        Assert.Equal(otherRule, Assert.Single(await repository.ListRulesAsync(new MonitoredInstanceId(otherTarget), Timeout(), CancellationToken.None)).RuleId);
        await using var serverReadDataSource = database.CreateServerDataSource();
        var serverReadRepository = new PostgreSqlAlertRepositoryPort(serverReadDataSource);
        Assert.Equal(rule, Assert.Single(await serverReadRepository.ListActiveAsync(new MonitoredInstanceId(target), 10, Timeout(), CancellationToken.None)).RuleId);

        await using NpgsqlConnection restricted = await collectorDataSource.OpenConnectionAsync();
        await using (var setScope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope', @target::text, false);", restricted))
        {
            setScope.Parameters.AddWithValue("target", target);
            await setScope.ExecuteScalarAsync();
        }
        await using (var grantedRules = new NpgsqlCommand("SELECT count(*) FROM alerting.list_rules(@target);", restricted))
        {
            grantedRules.Parameters.AddWithValue("target", target);
            Assert.Equal(1L, (long)(await grantedRules.ExecuteScalarAsync() ?? -1L));
        }
        await using (var deniedActiveRead = new NpgsqlCommand("SELECT count(*) FROM reporting.list_active_alerts(@target,10,NULL,NULL,NULL);", restricted))
        {
            deniedActiveRead.Parameters.AddWithValue("target", target);
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => deniedActiveRead.ExecuteScalarAsync());
            Assert.Equal("42501", exception.SqlState);
        }
        await using NpgsqlConnection serverRestricted = await serverReadDataSource.OpenConnectionAsync();
        await using (var setScope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope', @target::text, false);", serverRestricted))
        {
            setScope.Parameters.AddWithValue("target", target);
            await setScope.ExecuteScalarAsync();
        }
        await using var serverCrossTenant = new NpgsqlCommand("SELECT count(*) FROM reporting.list_active_alerts(@other,10,NULL,NULL,NULL);", serverRestricted);
        serverCrossTenant.Parameters.AddWithValue("other", otherTarget);
        Assert.Equal(0L, (long)(await serverCrossTenant.ExecuteScalarAsync() ?? -1L));
        await using (var deniedDml = new NpgsqlCommand("INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,confirmation_count,evaluation_interval) VALUES(gen_random_uuid(),@target,'unauthorized',1,'cpu.percent',1,1,1,interval '1 second');", restricted))
        {
            deniedDml.Parameters.AddWithValue("target", target);
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => deniedDml.ExecuteNonQueryAsync());
            Assert.Equal("42501", exception.SqlState);
        }
        await using var deniedFunction = new NpgsqlCommand("SELECT alerting.upsert_rule('{}'::jsonb,@key,'S-1-5-18',@correlation);", restricted);
        deniedFunction.Parameters.AddWithValue("key", Guid.NewGuid().ToString("D"));
        deniedFunction.Parameters.AddWithValue("correlation", Guid.NewGuid());
        PostgresException functionException = await Assert.ThrowsAsync<PostgresException>(() => deniedFunction.ExecuteScalarAsync());
        Assert.Equal("42501", functionException.SqlState);
        await using var deniedClaimHelper = new NpgsqlCommand("SELECT alerting.assert_evaluation_claims(@target,'[]'::jsonb,@work,@owner,1);", restricted);
        deniedClaimHelper.Parameters.AddWithValue("target", target);
        deniedClaimHelper.Parameters.AddWithValue("work", "unauthorized-helper");
        deniedClaimHelper.Parameters.AddWithValue("owner", Guid.NewGuid());
        PostgresException helperException = await Assert.ThrowsAsync<PostgresException>(() => deniedClaimHelper.ExecuteScalarAsync());
        Assert.Equal("42501", helperException.SqlState);
    }

    [Fact]
    public async Task CursorAndEqualTimeIdentityContractsArePresent()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ux_alert_history_replay');", connection);
        Assert.False((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task CanonicalEvidenceAndReplayValidationExecuteAgainstPostgreSql()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        Guid target = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Guid rule = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Guid run = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        await using var command = new NpgsqlCommand("SELECT octet_length(alerting.canonical_evidence_sha256(@target,@rule,'metric_threshold','cpu','engine.core','1',1,repeat('a',64),clock_timestamp(),'sample-1',@run,1.0,NULL,'integration')), alerting.canonical_decision_snapshot(jsonb_build_object('TargetId',@target,'RuleId',@rule,'State',1,'Revision',1));", connection);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("rule", rule); command.Parameters.AddWithValue("run", run);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(32, reader.GetInt32(0));
        Assert.Contains("TargetId", reader.GetString(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshInstallExecutesEmptyEvidenceReconciliationWithoutSemanticSqlErrors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM alerting.reconcile_due_evidence_internal(1);",
            connection);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
    }

    [Fact]
    public async Task DispatchPermitAndMaintenanceFenceSerializeOnTheRealPostgreSqlSession()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection sender = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlConnection maintenance = await database.DataSource.OpenConnectionAsync();
        Guid target = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@target::text,0));", sender))
        {
            acquire.Parameters.AddWithValue("target", target);
            await acquire.ExecuteScalarAsync();
        }
        await using NpgsqlTransaction transaction = await maintenance.BeginTransactionAsync();
        await using (var probe = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended(@target::text,0));", maintenance, transaction))
        {
            probe.Parameters.AddWithValue("target", target);
            Assert.False((bool)(await probe.ExecuteScalarAsync() ?? true));
        }
        await transaction.RollbackAsync();
        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@target::text,0));", sender);
        release.Parameters.AddWithValue("target", target);
        Assert.True((bool)(await release.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task RepositoryExecutesEvidenceQueueEvaluationReplayAndDeliveryLeaseLifecycle()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Guid ruleId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Guid destinationId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
        Guid owner = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/integration"), new WorkerExecutionId(owner), new FencingToken(1));
        DateTimeOffset observed = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedTargetAndLeaseAsync(database, target, lease);

        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var rule = new AlertRuleDefinition(ruleId, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        var server = new PostgreSqlAlertRepositoryPort(database.CreateServerDataSource());
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destinationId);

        var observation = new AlertObservation(targetId, ruleId, observed, 2, null, "observed|sample=11111111-1111-4111-8111-111111111111|run=cccccccc-cccc-4ccc-8ccc-cccccccccccc", null, null, "11111111-1111-4111-8111-111111111111", Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), "metric_threshold", catalog.Metric, catalog.SourceCollector, "1", catalog.SourceSchemaVersion, new string('a', 64));
        await SeedEvaluationQueueAsync(database, observation);

        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(lease, 10, Timeout(), CancellationToken.None);
        AlertEvaluationWork queued = Assert.Single(work);
        Assert.Equal(observation.OperationId, queued.OperationId);
        AlertEvaluationResult evaluated = AlertEvaluator.Evaluate(rule, new AlertRuleState(ruleId, targetId), observation);
        var decision = new AlertEvaluationDecision(observation, evaluated.State, evaluated.Event, evaluated.DeliverySuppressed, evaluated.Reason);
        var batch = new AlertEvaluationBatch(new[] { observation }, lease, Timeout(), new[] { decision }, null, queued.DueAtUtc, new[] { queued });
        AlertEvaluationOutcome outcome = await repository.EvaluateAndPersistAsync(batch, CancellationToken.None);
        Assert.Equal(1, outcome.Evaluated);
        Assert.Equal(AlertState.Firing, (await repository.GetStateAsync(targetId, ruleId, Timeout(), CancellationToken.None))!.State);
        await using (NpgsqlConnection stateConnection = await database.DataSource.OpenConnectionAsync())
        {
            await using var stateCommand = new NpgsqlCommand("SELECT state,acknowledged_at IS NULL,count(*) OVER () FROM alerting.rule_state WHERE instance_id=@target AND rule_id=@rule; SELECT count(*) FROM alerting.state_history WHERE instance_id=@target AND rule_id=@rule AND operation_id=@operation;", stateConnection);
            stateCommand.Parameters.AddWithValue("target", target);
            stateCommand.Parameters.AddWithValue("rule", ruleId);
            stateCommand.Parameters.AddWithValue("operation", observation.OperationId);
            await using NpgsqlDataReader stateReader = await stateCommand.ExecuteReaderAsync();
            Assert.True(await stateReader.ReadAsync());
            Assert.Equal(3, stateReader.GetInt16(0));
            Assert.True(stateReader.GetBoolean(1));
            Assert.True(await stateReader.NextResultAsync());
            Assert.True(await stateReader.ReadAsync());
        Assert.Equal(1L, stateReader.GetInt64(0));
        }

        ReplaySnapshot beforeReplay = await SnapshotReplayRowsAsync(database, target, ruleId);
        AlertEvaluationOutcome replay = await repository.EvaluateAndPersistAsync(batch, CancellationToken.None);
        Assert.Equal(0, replay.Evaluated);
        ReplaySnapshot afterReplay = await SnapshotReplayRowsAsync(database, target, ruleId);
        Assert.Equal(beforeReplay.RuleState, afterReplay.RuleState);
        Assert.Equal(beforeReplay.Outbox, afterReplay.Outbox);
        Assert.Equal(beforeReplay.History, afterReplay.History);
        Assert.Equal(beforeReplay.Replay, afterReplay.Replay);
        Assert.Equal(beforeReplay.EvaluationQueue, afterReplay.EvaluationQueue);
        await using (NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync())
        {
            await using var replayCommand = new NpgsqlCommand("SELECT count(*) FROM alerting.evaluation_replay WHERE operation_id=@operation AND instance_id=@target; SELECT count(*) FROM alerting.state_history WHERE operation_id=@operation AND instance_id=@target;", verify);
            replayCommand.Parameters.AddWithValue("operation", observation.OperationId);
            replayCommand.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader replayReader = await replayCommand.ExecuteReaderAsync();
            Assert.True(await replayReader.ReadAsync());
            Assert.Equal(1L, replayReader.GetInt64(0));
            Assert.True(await replayReader.NextResultAsync());
            Assert.True(await replayReader.ReadAsync());
            Assert.Equal(1L, replayReader.GetInt64(0));
        }

        IReadOnlyList<AlertDeliveryWork> deliveries = await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None);
        AlertDeliveryWork delivery = Assert.Single(deliveries);
        await using (await repository.AcquireDeliveryDispatchPermitAsync(delivery, Timeout(), CancellationToken.None))
        {
            AlertDeliveryResult completed = await repository.CompleteDeliveryAsync(new AlertDeliveryResult(delivery.DeliveryId, true, false, "ok", DateTimeOffset.UtcNow, targetId, 204, 0), lease, Timeout(), CancellationToken.None);
            Assert.True(completed.Succeeded);
        }
        await AssertLeaseClearedAsync(database, delivery.DeliveryId);

        AlertRuleState firingState = (await repository.GetStateAsync(targetId, ruleId, Timeout(), CancellationToken.None))!;
        await server.AcknowledgeAsync(new AlertAcknowledgeRequest(firingState.AlertId!.Value, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.AcknowledgeAlert), Timeout(), firingState.Revision, null), CancellationToken.None);
        await using (NpgsqlConnection acknowledgementConnection = await database.DataSource.OpenConnectionAsync())
        {
            await using var acknowledgementCommand = new NpgsqlCommand("SELECT state,acknowledged_at IS NOT NULL,acknowledged_by FROM alerting.rule_state WHERE instance_id=@target AND rule_id=@rule; SELECT count(*) FROM alerting.state_history WHERE instance_id=@target AND rule_id=@rule AND to_state=4; SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target AND event_kind=3 AND cancelled_at IS NULL;", acknowledgementConnection);
            acknowledgementCommand.Parameters.AddWithValue("target", target);
            acknowledgementCommand.Parameters.AddWithValue("rule", ruleId);
            await using NpgsqlDataReader acknowledgementReader = await acknowledgementCommand.ExecuteReaderAsync();
            Assert.True(await acknowledgementReader.ReadAsync());
            Assert.Equal(4, acknowledgementReader.GetInt16(0));
            Assert.True(acknowledgementReader.GetBoolean(1));
            Assert.Equal("S-1-5-18", acknowledgementReader.GetString(2));
            Assert.True(await acknowledgementReader.NextResultAsync());
            Assert.True(await acknowledgementReader.ReadAsync());
            Assert.Equal(1L, acknowledgementReader.GetInt64(0));
            Assert.True(await acknowledgementReader.NextResultAsync());
            Assert.True(await acknowledgementReader.ReadAsync());
            Assert.Equal(1L, acknowledgementReader.GetInt64(0));
        }
        AlertDeliveryWork acknowledgement = Assert.Single(await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        AlertDeliveryResult acknowledgementResult = await repository.CompleteDeliveryAsync(new AlertDeliveryResult(acknowledgement.DeliveryId, true, false, "ack", DateTimeOffset.UtcNow, targetId, 204, 0), lease, Timeout(), CancellationToken.None);
        Assert.True(acknowledgementResult.Succeeded);
        await AssertLeaseClearedAsync(database, acknowledgement.DeliveryId);

        Guid retryDeliveryId = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        await SeedOutboxAsync(database, target, ruleId, destinationId, retryDeliveryId, Guid.Parse("11111111-1111-4111-8111-111111111111"), 2);
        AlertDeliveryWork retry = Assert.Single(await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        AlertDeliveryResult retryResult = await repository.CompleteDeliveryAsync(new AlertDeliveryResult(retry.DeliveryId, false, false, "timeout", DateTimeOffset.UtcNow, targetId, 504, 12), lease, Timeout(), CancellationToken.None);
        Assert.False(retryResult.Succeeded);
        await using (NpgsqlConnection verifyRetry = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT attempt,completed_at,leased_until,due_at > clock_timestamp() FROM alerting.delivery_outbox WHERE delivery_id=@id;", verifyRetry);
            command.Parameters.AddWithValue("id", retryDeliveryId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.GetBoolean(3));
        }
        WorkerLeaseIdentity stale = new(lease.Key, lease.Owner, new FencingToken(2));
        PostgresException staleException = await Assert.ThrowsAsync<PostgresException>(() => repository.CompleteDeliveryAsync(new AlertDeliveryResult(retry.DeliveryId, true, false, "stale", DateTimeOffset.UtcNow, targetId), stale, Timeout(), CancellationToken.None).AsTask());
        Assert.Equal("55000", staleException.SqlState);

        await ReleaseLeaseAsync(database, lease);
        WorkerLeaseIdentity reclaimedLease = new(lease.Key, new WorkerExecutionId(Guid.Parse("22222222-2222-4222-8222-222222222222")), new FencingToken(2));
        await AcquireLeaseAsync(database, reclaimedLease);
        await SetDueNowAsync(database, retry.DeliveryId);
        AlertDeliveryWork reclaimed = Assert.Single(await repository.ClaimDueDeliveriesAsync(reclaimedLease, 10, Timeout(), CancellationToken.None));
        Assert.Equal(reclaimedLease.Owner.Value, reclaimed.Lease!.Owner.Value);
        Assert.Equal(AlertDeliveryLeaseOutcome.Active, await repository.RenewDeliveryStateAsync(reclaimed, reclaimedLease, Timeout(), CancellationToken.None));
        Assert.True((await repository.CompleteDeliveryAsync(new AlertDeliveryResult(reclaimed.DeliveryId, true, false, "reclaimed", DateTimeOffset.UtcNow, targetId, 204, 0), reclaimedLease, Timeout(), CancellationToken.None)).Succeeded);
        await AssertLeaseClearedAsync(database, reclaimed.DeliveryId);

        Guid driftDeliveryId = Guid.Parse("23232323-2323-4232-8232-232323232323");
        await SeedOutboxAsync(database, target, ruleId, destinationId, driftDeliveryId, Guid.Parse("24242424-2424-4242-8242-242424242424"), 3, Guid.Parse("25252525-2525-4252-8252-252525252525"));
        AlertDeliveryWork drift = Assert.Single(await repository.ClaimDueDeliveriesAsync(reclaimedLease, 10, Timeout(), CancellationToken.None));
        // Mutate only the destination row.  Administrative upsert intentionally
        // auto-cancels pending rows, so it cannot prove that renewal itself
        // detects a claimed destination revision/configuration drift.
        await using (NpgsqlConnection driftMutation = await database.DataSource.OpenConnectionAsync())
        {
            await using var mutate = new NpgsqlCommand("UPDATE alerting.destination SET revision=revision+1,configuration_digest=@digest WHERE destination_id=@destination AND instance_id=@target;", driftMutation);
            mutate.Parameters.AddWithValue("digest", new string('e', 64));
            mutate.Parameters.AddWithValue("destination", destinationId);
            mutate.Parameters.AddWithValue("target", target);
            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());
        }
        Assert.Equal(AlertDeliveryLeaseOutcome.CancelUnapproved, await repository.RenewDeliveryStateAsync(drift, reclaimedLease, Timeout(), CancellationToken.None));
        await AssertLeaseClearedAsync(database, drift.DeliveryId);
        await using (NpgsqlConnection driftConnection = await database.DataSource.OpenConnectionAsync())
        {
            await using var driftCommand = new NpgsqlCommand("SELECT cancel_reason, cancelled_at IS NOT NULL, completed_at IS NOT NULL, leased_until IS NULL, lease_work_key IS NULL, lease_owner_execution_id IS NULL, lease_fencing IS NULL, (SELECT count(*) FROM alerting.delivery_attempt WHERE delivery_id=@id) FROM alerting.delivery_outbox WHERE delivery_id=@id;", driftConnection);
            driftCommand.Parameters.AddWithValue("id", drift.DeliveryId);
            await using NpgsqlDataReader driftReader = await driftCommand.ExecuteReaderAsync();
            Assert.True(await driftReader.ReadAsync());
            Assert.Equal("unapproved", driftReader.GetString(0));
            for (int index = 1; index <= 6; index++) Assert.True(driftReader.GetBoolean(index));
            Assert.Equal(0L, driftReader.GetInt64(7));
        }
    }

    [Fact]
    public async Task EvidenceReconciliationDrainsOldestRowsAcrossMoreThanOneHundredTargets()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid singleTarget = Guid.Parse("7d7d7d7d-7d7d-4d7d-8d7d-7d7d7d7d7d7d");
        Guid singleRule = Guid.Parse("7e7e7e7e-7e7e-4e7e-8e7e-7e7e7e7e7e7e");
        Guid healthRule = Guid.Parse("81818181-8181-4181-8181-818181818181");
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/reconcile-drain"), new WorkerExecutionId(Guid.Parse("7f7f7f7f-7f7f-4f7f-8f7f-7f7f7f7f7f7f")), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, singleTarget, lease);
        DateTimeOffset baseObserved = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-1));
        DateTimeOffset healthCompleted = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddSeconds(-30));
        await using (NpgsqlConnection seed = await database.DataSource.OpenConnectionAsync())
        {
            const string sql = """
                WITH targets AS (
                    SELECT CASE WHEN i=0 THEN @single_target ELSE alerting.sha_uuid('m8-drain-target-' || i::text) END AS target_id,
                           CASE WHEN i=0 THEN @single_rule ELSE alerting.sha_uuid('m8-drain-rule-' || i::text) END AS rule_id
                    FROM generate_series(0,101) i
                )
                INSERT INTO control.observation_target(instance_id,instance_key,display_name,created_at,updated_at,discovery_requested_at)
                SELECT target_id,'m8-drain-' || target_id::text,'M8 drain target',now(),now(),now() FROM targets
                ON CONFLICT(instance_id) DO NOTHING;
                UPDATE control.observation_target
                SET host_name='m8-health.example',instance_name='M8HEALTH',connect_timeout=interval '5 seconds',authentication_mode='windows_integrated_service_identity',transport_security_mode='mandatory_validated',lifecycle_state='active',revision=1
                WHERE instance_id=@single_target;
                WITH targets AS (
                    SELECT CASE WHEN i=0 THEN @single_target ELSE alerting.sha_uuid('m8-drain-target-' || i::text) END AS target_id,
                           CASE WHEN i=0 THEN @single_rule ELSE alerting.sha_uuid('m8-drain-rule-' || i::text) END AS rule_id
                    FROM generate_series(0,101) i
                )
                INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval)
                SELECT rule_id,target_id,'cpu.high',1,'cpu.percent',1,80,5,1,interval '0 seconds',interval '15 seconds' FROM targets
                ON CONFLICT(rule_id) DO NOTHING;
                INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval)
                VALUES(@health_rule,@single_target,'collector.health',2,NULL,1,0,0,1,interval '0 seconds',interval '1 hour')
                ON CONFLICT(rule_id) DO NOTHING;
                UPDATE alerting.rule SET created_at=now()-interval '1 day',updated_at=now()-interval '1 day';
                WITH runs AS (
                    SELECT 0 AS target_index, @single_target AS target_id, @single_run AS run_id
                    UNION ALL
                    SELECT i, alerting.sha_uuid('m8-drain-target-' || i::text), alerting.sha_uuid('m8-drain-run-' || i::text)
                    FROM generate_series(1,101) i
                )
                INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
                SELECT run_id,target_id,'engine.core',1,1,1,1,'m8-drain-source',@owner,1,decode(repeat('a',64),'hex'),@health_completed-interval '5 minutes',@health_completed-interval '1 minute' FROM runs
                ON CONFLICT(run_id) DO NOTHING;
                WITH runs AS (
                    SELECT 0 AS target_index, @single_target AS target_id, @single_run AS run_id
                    UNION ALL
                    SELECT i, alerting.sha_uuid('m8-drain-target-' || i::text), alerting.sha_uuid('m8-drain-run-' || i::text)
                    FROM generate_series(1,101) i
                )
                INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
                SELECT run_id,'succeeded','completed',1,0,1,CASE WHEN target_index=0 THEN 102 ELSE 1 END,CASE WHEN target_index=0 THEN 102 ELSE 1 END,CASE WHEN target_index=0 THEN 102 ELSE 1 END,0,0,0,0,0,false,false,'none',true,0,0,decode(repeat('b',64),'hex'),@health_completed FROM runs
                ON CONFLICT(run_id) DO NOTHING;
                WITH samples AS (
                    SELECT 0 AS target_index, i AS sample_index, @single_target AS target_id, @single_rule AS rule_id, @single_run AS run_id
                    FROM generate_series(0,101) i
                    UNION ALL
                    SELECT i, 0, alerting.sha_uuid('m8-drain-target-' || i::text), alerting.sha_uuid('m8-drain-rule-' || i::text), alerting.sha_uuid('m8-drain-run-' || i::text)
                    FROM generate_series(1,101) i
                )
                INSERT INTO telemetry.raw_metric_sample(observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
                SELECT CASE WHEN target_index=0 THEN @base_observed - (400-sample_index) * interval '1 second' ELSE @base_observed - (200-target_index) * interval '1 second' END,alerting.sha_uuid('m8-drain-sample-' || target_id::text || '-' || sample_index::text),target_id,'cpu.percent',sample_index,'{}'::jsonb,@base_observed,run_id FROM samples;
                INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
                VALUES(@single_target,'engine.core',1,1,1,true,interval '1 hour',@health_completed,'closed',0,@health_completed,@health_completed)
                ON CONFLICT(instance_id,collector_id) DO UPDATE SET target_revision=1,schedule_revision=1,enabled=true,collection_interval=interval '1 hour',next_due_at=EXCLUDED.next_due_at,circuit_state='closed',consecutive_failure_count=0,created_at=EXCLUDED.created_at,updated_at=EXCLUDED.updated_at;
                """;
            await using var command = new NpgsqlCommand(sql, seed);
            command.Parameters.AddWithValue("single_target", singleTarget);
            command.Parameters.AddWithValue("single_rule", singleRule);
            command.Parameters.AddWithValue("health_rule", healthRule);
            command.Parameters.AddWithValue("single_run", Guid.Parse("80808080-8080-4080-8080-808080808080"));
            command.Parameters.AddWithValue("owner", lease.Owner.Value);
            command.Parameters.AddWithValue("base_observed", baseObserved);
            command.Parameters.AddWithValue("health_completed", healthCompleted);
            await command.ExecuteNonQueryAsync();
        }
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        var claimed = new List<AlertEvaluationWork>();
        var batches = new List<IReadOnlyList<AlertEvaluationWork>>();
        for (int pass = 0; pass < 8; pass++)
        {
            IReadOnlyList<AlertEvaluationWork> batch = await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout(), CancellationToken.None);
            batches.Add(batch);
            claimed.AddRange(batch);
            if (batch.Count == 0) break;
        }
        Assert.Equal(204, claimed.Count);
        Assert.Equal(204, claimed.Select(x => x.OperationId).Distinct().Count());
        Assert.Equal(203, claimed.Count(x => x.Observations[0].SourceKind == "metric_threshold"));
        Assert.Equal(1, claimed.Count(x => x.Observations[0].SourceKind == "collector_health"));
        Assert.Equal(102, claimed.Count(x => x.Observations[0].TargetId.Value == singleTarget && x.Observations[0].RuleId == singleRule));
        Assert.Equal(1, claimed.Count(x => x.Observations[0].TargetId.Value == singleTarget && x.Observations[0].RuleId == healthRule && x.Observations[0].SourceKind == "collector_health"));
        Assert.Equal(102, claimed.Select(x => x.Observations[0].TargetId.Value).Distinct().Count());
        var expectedTargets = new HashSet<Guid> { singleTarget };
        for (int index = 1; index <= 101; index++) expectedTargets.Add(DeterministicShaUuid("m8-drain-target-" + index));
        Assert.True(expectedTargets.SetEquals(claimed.Select(x => x.Observations[0].TargetId.Value)));
        Assert.True(batches.Count >= 3);
        Assert.All(batches.Take(2), batch => Assert.Equal(100, batch.Count));
        Assert.Empty(batches[^1]);
        DateTimeOffset[] singleTargetObservations = claimed
            .Where(x => x.Observations[0].TargetId.Value == singleTarget && x.Observations[0].RuleId == singleRule)
            .Select(x => x.Observations[0].ObservedAtUtc)
            .ToArray();
        Assert.Equal(singleTargetObservations.OrderBy(x => x).ToArray(), singleTargetObservations);
        DateTimeOffset[] claimedObservationTimes = claimed.Select(x => x.Observations[0].ObservedAtUtc).ToArray();
        Assert.Equal(claimedObservationTimes.OrderBy(x => x).ToArray(), claimedObservationTimes);
        await using (NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync())
        {
            // The production repository fixes its transaction to UTC. Match that
            // context when reconstructing identities containing timestamptz::text;
            // the administrator connection may inherit a non-UTC server default.
            await using (var utc = new NpgsqlCommand("SET TIME ZONE 'UTC';", verify))
            {
                await utc.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand("SELECT count(*) FROM alerting.evaluation_queue q WHERE q.operation_id=alerting.canonical_operation_id(q.instance_id,q.rule_id,q.observed_at,q.observations->0->>'Reason') AND q.operation_id IN (SELECT unnest(@operations));", verify);
            command.Parameters.AddWithValue("operations", claimed.Select(x => x.OperationId).ToArray());
            Assert.Equal(204L, (long)(await command.ExecuteScalarAsync() ?? 0L));
            await using var expected = new NpgsqlCommand("""
                SELECT alerting.canonical_operation_id(r.instance_id,r.rule_id,m.observed_at,'observed|sample=' || m.sample_id::text || '|run=' || m.collection_run_id::text), 'metric_threshold'::text
                FROM telemetry.raw_metric_sample m
                JOIN alerting.rule r ON r.instance_id=m.instance_id AND r.kind=1 AND r.metric_id=m.metric_key
                WHERE m.collection_run_id=@run OR m.collection_run_id IN (SELECT alerting.sha_uuid('m8-drain-run-' || i::text) FROM generate_series(1,101) i)
                UNION ALL
                SELECT alerting.canonical_operation_id(@target,@health_rule,
                    CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END,
                    'collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'') || '|bucket=' || floor(extract(epoch from h.repository_time)/3600)::bigint), 'collector_health'::text
                FROM reporting.collector_health_projection h
                WHERE h.instance_id=@target AND h.collector_id='engine.core' AND h.run_id=@run;
                """, verify);
            expected.Parameters.AddWithValue("target", singleTarget);
            expected.Parameters.AddWithValue("health_rule", healthRule);
            expected.Parameters.AddWithValue("run", Guid.Parse("80808080-8080-4080-8080-808080808080"));
            var expectedOperations = new HashSet<Guid>();
            var expectedMetricOperations = new HashSet<Guid>();
            var expectedHealthOperations = new HashSet<Guid>();
            await using (NpgsqlDataReader expectedReader = await expected.ExecuteReaderAsync())
            {
                while (await expectedReader.ReadAsync())
                {
                    Guid operation = expectedReader.GetGuid(0);
                    string sourceKind = expectedReader.GetString(1);
                    expectedOperations.Add(operation);
                    (sourceKind == "metric_threshold" ? expectedMetricOperations : expectedHealthOperations).Add(operation);
                }
            }
            Assert.Equal(204, expectedOperations.Count);
            Assert.Equal(203, expectedMetricOperations.Count);
            Assert.Single(expectedHealthOperations);
            Assert.True(expectedMetricOperations.SetEquals(claimed.Where(x => x.Observations[0].SourceKind == "metric_threshold").Select(x => x.OperationId)), "Metric operation identities must match the seeded evidence.");
            Assert.True(expectedHealthOperations.SetEquals(claimed.Where(x => x.Observations[0].SourceKind == "collector_health").Select(x => x.OperationId)), "Collector-health operation identities must match the UTC projection.");
            Assert.True(expectedOperations.SetEquals(claimed.Select(x => x.OperationId)));
            await using var drain = new NpgsqlCommand("SELECT count(*) FROM alerting.evaluation_queue WHERE completed_at IS NULL AND due_at<=clock_timestamp() AND (claimed_until IS NULL OR claimed_until<clock_timestamp());", verify);
            Assert.Equal(0L, (long)(await drain.ExecuteScalarAsync() ?? 0L));
        }
        await ReleaseLeaseAsync(database, lease);
    }

    [Fact]
    public async Task ClaimRecoveryIsIdempotentAndClearsOnlyExactCommittedClaim()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("6b6b6b6b-6b6b-4b6b-8b6b-6b6b6b6b6b6b");
        Guid rule = Guid.Parse("6c6c6c6c-6c6c-4c6c-8c6c-6c6c6c6c6c6c");
        Guid destination = Guid.Parse("6d6d6d6d-6d6d-4d6d-8d6d-6d6d6d6d6d6d");
        Guid delivery = Guid.Parse("6e6e6e6e-6e6e-4e6e-8e6e-6e6e6e6e6e6e");
        Guid owner = Guid.Parse("6f6f6f6f-6f6f-4f6f-8f6f-6f6f6f6f6f6f");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/ambiguous-claim"), new WorkerExecutionId(owner), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        await SeedRuleAndDestinationAsync(database, target, rule, destination);
        await SeedOutboxAsync(database, target, rule, destination, delivery, Guid.Parse("70707070-7070-4070-8070-707070707070"));
        await MarkClaimCommittedAsync(database, delivery, lease);
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        Assert.True(await repository.RecoverDeliveryClaimsAsync(targetId, lease, Timeout(), CancellationToken.None));
        Assert.False(await repository.RecoverDeliveryClaimsAsync(targetId, lease, Timeout(), CancellationToken.None));
        await AssertStaleFenceCleanupAsync(database, delivery, DateTimeOffset.UtcNow);
        await ReleaseLeaseAsync(database, lease);
    }

    [Fact]
    public async Task RepositoryAdminMutationsSuppressCancelAndAcknowledgeWithTenantScope()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("12121212-1212-4121-8121-121212121212");
        Guid otherTarget = Guid.Parse("13131313-1313-4131-8131-131313131313");
        Guid ruleId = Guid.Parse("14141414-1414-4141-8141-141414141414");
        Guid destinationId = Guid.Parse("15151515-1515-4151-8151-151515151515");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/admin-integration"), new WorkerExecutionId(Guid.Parse("16161616-1616-4161-8161-161616161616")), new FencingToken(1));
        await SeedTargetOnlyAsync(database, target);
        await SeedTargetOnlyAsync(database, otherTarget);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var rule = new AlertRuleDefinition(ruleId, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destinationId);
        Guid deliveryId = Guid.Parse("17171717-1717-4171-8171-171717171717");
        await SeedOutboxAsync(database, target, ruleId, destinationId, deliveryId, Guid.Parse("18181818-1818-4181-8181-181818181818"));

        DateTimeOffset now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var maintenance = new MaintenanceWindow(Guid.Parse("19191919-1919-4191-8191-191919191919"), targetId, now.AddMinutes(-1), now.AddMinutes(10), "integration maintenance");
        await server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(maintenance, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateMaintenanceWindow), Timeout()), CancellationToken.None);
        await using (NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT d.due_at >= @ends, d.leased_until IS NULL, EXISTS (SELECT 1 FROM alerting.delivery_suppression_intent i WHERE i.delivery_id=d.delivery_id) FROM alerting.delivery_outbox d WHERE d.delivery_id=@id;", verify);
            command.Parameters.AddWithValue("ends", maintenance.EndsAtUtc);
            command.Parameters.AddWithValue("id", deliveryId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        Assert.Equal(ruleId, Assert.Single(await repository.ListRulesAsync(targetId, Timeout(), CancellationToken.None)).RuleId);
        Assert.Empty(await repository.ListRulesAsync(new MonitoredInstanceId(otherTarget), Timeout(), CancellationToken.None));
        await using (NpgsqlConnection restricted = await collectorDataSource.OpenConnectionAsync())
        {
            await using var denied = new NpgsqlCommand("SELECT count(*) FROM alerting.rule;", restricted);
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteScalarAsync());
            Assert.Equal("42501", exception.SqlState);
        }
        AlertDeliveryWork claimed = new(deliveryId, Guid.NewGuid(), destinationId, "https-webhook", "integration", Array.Empty<byte>(), 0, DateTimeOffset.UtcNow, targetId, lease, lease.Key.Value);
        var cancel = new AlertDeliveryAdminCancellationRequest(claimed.DeliveryId, "operator_cancel", Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CancelAlertDelivery), Timeout());
        await server.CancelDeliveryAdminAsync(cancel, CancellationToken.None);
        await using (NpgsqlConnection verifyCancel = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL, completed_at IS NOT NULL, leased_until IS NULL FROM alerting.delivery_outbox WHERE delivery_id=@id;", verifyCancel);
            command.Parameters.AddWithValue("id", claimed.DeliveryId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        Guid disabledDeliveryId = Guid.Parse("20202020-2020-4020-8020-202020202020");
        await SeedOutboxAsync(database, target, ruleId, destinationId, disabledDeliveryId, Guid.Parse("21212121-2121-4121-8121-212121212121"));
        var disabledRule = new AlertRuleDefinition(ruleId, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval, false);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(disabledRule, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.UpdateAlertRule), Timeout(), 1), CancellationToken.None);
        await using (NpgsqlConnection verifyDisable = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL, cancel_reason, leased_until IS NULL FROM alerting.delivery_outbox WHERE delivery_id=@id;", verifyDisable);
            command.Parameters.AddWithValue("id", disabledDeliveryId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.Equal("rule_disabled", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
        }
    }

    [Fact]
    public async Task MaintenanceCancellationPreservesValidWindowAndIsReplayIdempotent()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("7a7a7a7a-7a7a-4a7a-8a7a-7a7a7a7a7a7a");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/maintenance-cancel-behavior"), new WorkerExecutionId(Guid.Parse("82828282-8282-4282-8282-828282828282")), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        DateTimeOffset now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var future = new MaintenanceWindow(Guid.Parse("7b7b7b7b-7b7b-4b7b-8b7b-7b7b7b7b7b7b"), targetId, now.AddHours(1), now.AddHours(2), "future cancellation");
        await server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(future, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateMaintenanceWindow), Timeout()), CancellationToken.None);
        string idempotency = Guid.NewGuid().ToString("D");
        var request = new MaintenanceCancellationRequest(future.Id, idempotency, Audit(targetId, AdministrativeAuditAction.RetireMaintenanceWindow), Timeout(), 1);
        AdministrativeAuditReceipt first = await server.CancelMaintenanceAsync(request, CancellationToken.None);
        AdministrativeAuditReceipt replay = await server.CancelMaintenanceAsync(request, CancellationToken.None);
        Assert.Equal(first.AuditId, replay.AuditId);

        await using (NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL,ends_at>starts_at,cancelled_actor_sid='S-1-5-18',cancelled_operation_id=@operation,revision,(SELECT alerting.maintenance_active_unscoped(@target,clock_timestamp())) FROM alerting.maintenance_window WHERE window_id=@window AND instance_id=@target;", verify);
            command.Parameters.AddWithValue("operation", Guid.Parse(idempotency));
            command.Parameters.AddWithValue("window", future.Id);
            command.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.Equal(2L, reader.GetInt64(4));
            Assert.False(reader.GetBoolean(5));
        }
        await using (NpgsqlConnection audit = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT count(*),max(actor_identifier),max(subject_kind),max(subject_identifier),max(safe_details->>'operationId'),max(safe_details->>'targetId') FROM audit.activity WHERE activity_id=@audit AND action_name='alert.maintenance.retire' AND authorization_result='allowed' AND outcome='succeeded';", audit);
            command.Parameters.AddWithValue("audit", first.AuditId.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("S-1-5-18", reader.GetString(1));
            Assert.Equal("maintenance_window", reader.GetString(2));
            Assert.Equal(future.Id.ToString(), reader.GetString(3));
            Assert.Equal(idempotency, reader.GetString(4));
            Assert.Equal(target.ToString(), reader.GetString(5));
            await reader.DisposeAsync();
            await using var index = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname='alerting' AND indexname='ix_alert_maintenance_window';", audit);
            string definition = (string)(await index.ExecuteScalarAsync() ?? string.Empty);
            Assert.Contains("instance_id", definition, StringComparison.Ordinal);
            Assert.Contains("starts_at", definition, StringComparison.Ordinal);
            Assert.Contains("ends_at", definition, StringComparison.Ordinal);
            Assert.Contains("cancelled_at IS NULL", definition, StringComparison.OrdinalIgnoreCase);
        }
        var cancelledUpdate = new MaintenanceWindow(future.Id, targetId, now.AddHours(3), now.AddHours(4), "reactivation attempt");
        await Assert.ThrowsAsync<AlertRepositoryOperationException>(() => server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(cancelledUpdate, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.UpdateMaintenanceWindow), Timeout(), 2), CancellationToken.None).AsTask());
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var collector = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        Assert.Null(await collector.GetMaintenanceAsync(targetId, now, Timeout(), CancellationToken.None));
        await using (NpgsqlConnection restricted = await collectorDataSource.OpenConnectionAsync())
        {
            await using var denied = new NpgsqlCommand("SELECT count(*) FROM alerting.maintenance_window;", restricted);
            PostgresException deniedException = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteScalarAsync());
            Assert.Equal("42501", deniedException.SqlState);
        }

        Guid activeRule = Guid.Parse("83838383-8383-4383-8383-838383838383");
        Guid activeDestination = Guid.Parse("84848484-8484-4484-8484-848484848484");
        Guid activeDelivery = Guid.Parse("85858585-8585-4585-8585-858585858585");
        Guid unrelatedRetryDelivery = Guid.Parse("89898989-8989-4989-8989-898989898989");
        await SeedRuleAndDestinationAsync(database, target, activeRule, activeDestination);
        await SeedOutboxAsync(database, target, activeRule, activeDestination, activeDelivery, Guid.Parse("86868686-8686-4686-8686-868686868686"));
        await SeedOutboxAsync(database, target, activeRule, activeDestination, unrelatedRetryDelivery, Guid.Parse("8a8a8a8a-8a8a-4a8a-8a8a-8a8a8a8a8a8a"));
        DateTimeOffset unrelatedRetryDue = now.AddHours(2);
        await using (NpgsqlConnection retry = await database.DataSource.OpenConnectionAsync())
        {
            await using var retryCommand = new NpgsqlCommand("UPDATE alerting.delivery_outbox SET due_at=@due,attempt=2 WHERE delivery_id=@id;", retry);
            retryCommand.Parameters.AddWithValue("due", unrelatedRetryDue);
            retryCommand.Parameters.AddWithValue("id", unrelatedRetryDelivery);
            Assert.Equal(1, await retryCommand.ExecuteNonQueryAsync());
        }
        var active = new MaintenanceWindow(Guid.Parse("7c7c7c7c-7c7c-4c7c-8c7c-7c7c7c7c7c7c"), targetId, now.AddMinutes(-5), now.AddMinutes(30), "active cancellation");
        await server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(active, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateMaintenanceWindow), Timeout()), CancellationToken.None);
        string activeIdempotency = Guid.NewGuid().ToString("D");
        await server.CancelMaintenanceAsync(new MaintenanceCancellationRequest(active.Id, activeIdempotency, Audit(targetId, AdministrativeAuditAction.RetireMaintenanceWindow), Timeout(), 1), CancellationToken.None);
        await using (NpgsqlConnection verifyActive = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL,ends_at>starts_at FROM alerting.maintenance_window WHERE window_id=@window AND instance_id=@target;", verifyActive);
            command.Parameters.AddWithValue("window", active.Id);
            command.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }
        await using (NpgsqlConnection verifyActiveRows = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT d.due_at,d.attempt,EXISTS(SELECT 1 FROM alerting.delivery_suppression_intent i WHERE i.delivery_id=d.delivery_id AND i.reason='maintenance_started:' || @window::text) FROM alerting.delivery_outbox d WHERE d.delivery_id IN (@active,@retry) ORDER BY d.delivery_id;", verifyActiveRows);
            command.Parameters.AddWithValue("window", active.Id);
            command.Parameters.AddWithValue("active", activeDelivery);
            command.Parameters.AddWithValue("retry", unrelatedRetryDelivery);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetFieldValue<DateTimeOffset>(0) <= DateTimeOffset.UtcNow);
            Assert.Equal(0, reader.GetInt32(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(unrelatedRetryDue, reader.GetFieldValue<DateTimeOffset>(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.False(reader.GetBoolean(2));
        }
        var futureWithRetry = new MaintenanceWindow(Guid.Parse("8b8b8b8b-8b8b-4b8b-8b8b-8b8b8b8b8b8b"), targetId, now.AddHours(3), now.AddHours(4), "future retry isolation");
        await server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(futureWithRetry, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateMaintenanceWindow), Timeout()), CancellationToken.None);
        await server.CancelMaintenanceAsync(new MaintenanceCancellationRequest(futureWithRetry.Id, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.RetireMaintenanceWindow), Timeout(), 1), CancellationToken.None);
        await using (NpgsqlConnection verifyFutureRows = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT due_at,attempt,leased_until IS NULL,NOT EXISTS(SELECT 1 FROM alerting.delivery_suppression_intent i WHERE i.delivery_id=d.delivery_id AND i.reason LIKE 'maintenance_started:%') FROM alerting.delivery_outbox d WHERE d.delivery_id=@retry;", verifyFutureRows);
            command.Parameters.AddWithValue("retry", unrelatedRetryDelivery);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(unrelatedRetryDue, reader.GetFieldValue<DateTimeOffset>(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
        }

        IReadOnlyList<AlertDeliveryWork> deliveries = await collector.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None);
        AlertDeliveryWork activeWork = Assert.Single(deliveries);
        Assert.Equal(AlertDeliveryReadiness.Active, await collector.RecheckDeliveryAsync(activeWork, Timeout(), CancellationToken.None));
        Assert.Equal(AlertDeliveryLeaseOutcome.Active, await collector.RenewDeliveryStateAsync(activeWork, lease, Timeout(), CancellationToken.None));

        DateTimeOffset evidenceObserved = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddSeconds(-20));
        var evidence = new AlertObservation(targetId, activeRule, evidenceObserved, 99, null, "observed|sample=22222222-2222-4222-8222-222222222222|run=87878787-8787-4787-8787-878787878787", sampleId: "22222222-2222-4222-8222-222222222222", runId: Guid.Parse("87878787-8787-4787-8787-878787878787"), sourceKind: "metric_threshold", metricId: "engine.user_connections", sourceCollector: "engine.core", sourceVersion: "1", sourceSchemaVersion: 1, sourceDigest: new string('e', 64));
        await SeedEvaluationQueueAsync(database, evidence);
        IReadOnlyList<AlertEvaluationWork> evaluations = await collector.ClaimDueEvaluationsAsync(lease, 10, Timeout(), CancellationToken.None);
        AlertEvaluationWork evaluation = Assert.Single(evaluations);
        AlertRuleDefinition activeDefinition = (await collector.ListRulesAsync(targetId, Timeout(), CancellationToken.None)).Single(x => x.RuleId == activeRule);
        AlertObservation claimedObservation = Assert.Single(evaluation.Observations);
        AlertRuleState priorState = await collector.GetStateAsync(targetId, activeRule, Timeout(), CancellationToken.None) ?? new AlertRuleState(activeRule, targetId);
        AlertEvaluationResult evaluatedDecision = AlertEvaluator.Evaluate(activeDefinition, priorState, claimedObservation);
        var evaluationDecision = new AlertEvaluationDecision(claimedObservation, evaluatedDecision.State, evaluatedDecision.Event, evaluatedDecision.DeliverySuppressed, evaluatedDecision.Reason);
        var evaluationBatch = new AlertEvaluationBatch(new[] { claimedObservation }, lease, Timeout(), new[] { evaluationDecision }, ClaimedWork: new[] { evaluation });
        AlertEvaluationOutcome evaluated = await collector.EvaluateAndPersistAsync(evaluationBatch, CancellationToken.None);
        Assert.Equal(1, evaluated.Evaluated);
        Assert.Equal(0, evaluated.Suppressed);
        await using (NpgsqlConnection evaluatedState = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SELECT s.state,s.last_operation_id,encode(s.evidence_digest,'hex'),(SELECT count(*) FROM alerting.state_history h WHERE h.instance_id=@target AND h.rule_id=@rule AND h.operation_id=@operation),(SELECT count(*) FROM alerting.evaluation_replay r WHERE r.instance_id=@target AND r.rule_id=@rule AND r.operation_id=@operation),(SELECT count(*) FROM alerting.delivery_outbox d WHERE d.instance_id=@target AND d.rule_id=@rule AND d.operation_id=@operation AND d.evidence_digest=decode(@digest,'hex')),(SELECT completed_at IS NOT NULL AND claimed_until IS NULL AND claim_work_key IS NULL AND claim_owner_execution_id IS NULL AND claim_fencing IS NULL FROM alerting.evaluation_queue q WHERE q.operation_id=@operation) FROM alerting.rule_state s WHERE s.instance_id=@target AND s.rule_id=@rule;", evaluatedState);
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("rule", activeRule);
            command.Parameters.AddWithValue("operation", claimedObservation.OperationId);
            command.Parameters.AddWithValue("digest", claimedObservation.EvidenceDigest);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3, reader.GetInt16(0));
            Assert.Equal(claimedObservation.OperationId, reader.GetGuid(1));
            Assert.Equal(claimedObservation.EvidenceDigest, reader.GetString(2));
            Assert.Equal(1L, reader.GetInt64(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.Equal(1L, reader.GetInt64(5));
            Assert.True(reader.GetBoolean(6));
        }
        ReplaySnapshot beforeReplay = await SnapshotReplayRowsAsync(database, target, activeRule);
        AlertEvaluationOutcome evaluationReplay = await collector.EvaluateAndPersistAsync(evaluationBatch, CancellationToken.None);
        Assert.Equal(0, evaluationReplay.Evaluated);
        ReplaySnapshot afterReplay = await SnapshotReplayRowsAsync(database, target, activeRule);
        Assert.Equal(beforeReplay.RuleState, afterReplay.RuleState);
        Assert.Equal(beforeReplay.Outbox, afterReplay.Outbox);
        Assert.Equal(beforeReplay.History, afterReplay.History);
        Assert.Equal(beforeReplay.Replay, afterReplay.Replay);
        Assert.Equal(beforeReplay.EvaluationQueue, afterReplay.EvaluationQueue);
        await ReleaseLeaseAsync(database, lease);
    }

    [Fact]
    public async Task RepositoryDispatchPermitBlocksMaintenanceUntilPermitRelease()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("26262626-2626-4262-8262-262626262626");
        Guid rule = Guid.Parse("27272727-2727-4272-8272-272727272727");
        Guid destination = Guid.Parse("28282828-2828-4282-8282-282828282828");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/permit-integration"), new WorkerExecutionId(Guid.Parse("29292929-2929-4292-8292-292929292929")), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var definition = new AlertRuleDefinition(rule, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(definition, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destination);
        Guid deliveryId = Guid.Parse("30303030-3030-4030-8030-303030303030");
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("31313131-3131-4131-8131-313131313131"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        AlertDeliveryWork claimed = Assert.Single(await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        IAlertDeliveryDispatchPermit permit = await repository.AcquireDeliveryDispatchPermitAsync(claimed, Timeout(), CancellationToken.None);
        try
        {
            DateTimeOffset now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
            var maintenance = new MaintenanceWindow(Guid.Parse("32323232-3232-4232-8232-323232323232"), targetId, now.AddMinutes(-1), now.AddMinutes(10), "permit race");
            Task<AdministrativeAuditReceipt> transition = server.UpsertMaintenanceAsync(new MaintenanceWriteRequest(maintenance, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateMaintenanceWindow), Timeout()), CancellationToken.None).AsTask();
            await Task.Delay(150);
            Assert.False(transition.IsCompleted);
            await permit.DisposeAsync();
            AdministrativeAuditReceipt receipt = await transition;
            Assert.NotEqual(Guid.Empty, receipt.AuditId.Value);
            await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT d.leased_until IS NULL, d.due_at >= @ends, EXISTS (SELECT 1 FROM alerting.delivery_suppression_intent i WHERE i.delivery_id=d.delivery_id) FROM alerting.delivery_outbox d WHERE d.delivery_id=@id;", verify);
            command.Parameters.AddWithValue("ends", maintenance.EndsAtUtc);
            command.Parameters.AddWithValue("id", deliveryId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }
        finally
        {
            await permit.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenewalReturnsTypedMaintenanceSuppressionAndPersistsCleanupIntent()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("40404040-4040-4040-8040-404040404040");
        Guid rule = Guid.Parse("41414141-4141-4141-8141-414141414141");
        Guid destination = Guid.Parse("42424242-4242-4242-8242-424242424242");
        Guid deliveryId = Guid.Parse("43434343-4343-4343-8343-434343434343");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/renew-maintenance"), new WorkerExecutionId(Guid.Parse("44444444-4444-4444-8444-444444444444")), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var definition = new AlertRuleDefinition(rule, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(definition, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destination);
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("45454545-4545-4545-8545-454545454545"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        AlertDeliveryWork claimed = Assert.Single(await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        DateTimeOffset ends = DateTimeOffset.UtcNow.AddMinutes(10);
        Guid maintenanceWindowId = Guid.Parse("46464646-4646-4646-8646-464646464646");
        await using (NpgsqlConnection mutation = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason) VALUES(@id,@target,clock_timestamp()-interval '1 minute',@ends,'renewal suppression');", mutation);
            command.Parameters.AddWithValue("id", maintenanceWindowId);
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("ends", ends);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(AlertDeliveryLeaseOutcome.DeferMaintenance, await repository.RenewDeliveryStateAsync(claimed, lease, Timeout(), CancellationToken.None));
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var verifyCommand = new NpgsqlCommand("SELECT leased_until IS NULL,lease_work_key IS NULL,lease_owner_execution_id IS NULL,lease_fencing IS NULL,due_at>=@ends,EXISTS(SELECT 1 FROM alerting.delivery_suppression_intent WHERE delivery_id=@id AND reason='maintenance_started:' || @window_id::text) FROM alerting.delivery_outbox WHERE delivery_id=@id;", verify);
        verifyCommand.Parameters.AddWithValue("ends", ends);
        verifyCommand.Parameters.AddWithValue("id", deliveryId);
        verifyCommand.Parameters.AddWithValue("window_id", maintenanceWindowId);
        await using NpgsqlDataReader reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 6; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    public async Task RenewalMapsLostFenceToTypedOutcomeAndLeavesNoClaimedWork()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("47474747-4747-4747-8747-474747474747");
        Guid rule = Guid.Parse("48484848-4848-4848-8848-484848484848");
        Guid destination = Guid.Parse("49494949-4949-4949-8949-494949494949");
        Guid deliveryId = Guid.Parse("4a4a4a4a-4a4a-44aa-84aa-4a4a4a4a4a4a");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/renew-lost-fence"), new WorkerExecutionId(Guid.Parse("4b4b4b4b-4b4b-44bb-84bb-4b4b4b4b4b4b")), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var definition = new AlertRuleDefinition(rule, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(definition, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destination);
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("4c4c4c4c-4c4c-44cc-84cc-4c4c4c4c4c4c"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        AlertDeliveryWork claimed = Assert.Single(await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await ReleaseLeaseAsync(database, lease);
        WorkerLeaseIdentity newerLease = new(lease.Key, new WorkerExecutionId(Guid.Parse("6a6a6a6a-6a6a-4a6a-8a6a-6a6a6a6a6a6a")), new FencingToken(2));
        await AcquireLeaseAsync(database, newerLease);
        await MakeClaimReclaimableAsync(database, deliveryId);
        AlertDeliveryWork newerClaim = Assert.Single(await repository.ClaimDueDeliveriesAsync(newerLease, 10, Timeout(), CancellationToken.None));
        int newerAttempt = newerClaim.Attempt;
        AlertDeliveryLeaseOutcome outcome = await repository.RenewDeliveryStateAsync(claimed, lease, Timeout(), CancellationToken.None);
        Assert.Equal(AlertDeliveryLeaseOutcome.LostFence, outcome);
        PostgresException staleCompletion = await Assert.ThrowsAsync<PostgresException>(() => repository.CompleteDeliveryAsync(new AlertDeliveryResult(deliveryId, true, false, "stale", DateTimeOffset.UtcNow, targetId), lease, Timeout(), CancellationToken.None).AsTask());
        Assert.Equal("55000", staleCompletion.SqlState);
        await AssertNewerClaimPreservedAsync(database, deliveryId, newerLease, newerAttempt);
        await ReleaseLeaseAsync(database, newerLease);
    }

    [Fact]
    public async Task RepositoryDispatchPermitBlocksRuleDisableThenCleansClaimedOutbox()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("4c4c4c4c-4c4c-44cc-84cc-4c4c4c4c4c4c");
        Guid rule = Guid.Parse("4d4d4d4d-4d4d-44dd-84dd-4d4d4d4d4d4d");
        Guid destination = Guid.Parse("4e4e4e4e-4e4e-44ee-84ee-4e4e4e4e4e4e");
        Guid deliveryId = Guid.Parse("4f4f4f4f-4f4f-44ff-84ff-4f4f4f4f4f4f");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/permit-rule-disable"), new WorkerExecutionId(Guid.Parse("50505050-5050-4050-8050-505050505050")), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var definition = new AlertRuleDefinition(rule, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(definition, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destination);
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("51515151-5151-4151-8151-515151515151"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        AlertDeliveryWork claimed = Assert.Single(await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await using IAlertDeliveryDispatchPermit permit = await repository.AcquireDeliveryDispatchPermitAsync(claimed, Timeout(), CancellationToken.None);
        var disabled = new AlertRuleDefinition(definition.RuleId, definition.Name, definition.Kind, definition.MetricId, definition.Comparison, definition.Threshold, definition.Hysteresis, definition.ConfirmationCount, definition.ConfirmationWindow, definition.EvaluationInterval, false);
        Task<AdministrativeAuditReceipt> transition = server.UpsertRuleAsync(new AlertRuleWriteRequest(disabled, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.UpdateAlertRule), Timeout(), 1), CancellationToken.None).AsTask();
        await Task.Delay(150);
        Assert.False(transition.IsCompleted);
        await permit.DisposeAsync();
        AdministrativeAuditReceipt receipt = await transition;
        Assert.NotEqual(Guid.Empty, receipt.AuditId.Value);
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL,cancel_reason,completed_at IS NOT NULL,leased_until IS NULL,lease_work_key IS NULL,lease_owner_execution_id IS NULL,lease_fencing IS NULL FROM alerting.delivery_outbox WHERE delivery_id=@id;", verify);
        command.Parameters.AddWithValue("id", deliveryId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("rule_disabled", reader.GetString(1));
        for (int index = 2; index < 7; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    public async Task RepositoryDispatchPermitBlocksDestinationDisableThenCleansClaimedOutbox()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("52525252-5252-4252-8252-525252525252");
        Guid rule = Guid.Parse("53535353-5353-4353-8353-535353535353");
        Guid destination = Guid.Parse("54545454-5454-4454-8454-545454545454");
        Guid deliveryId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/m8/permit-destination-disable"), new WorkerExecutionId(Guid.Parse("56565656-5656-4656-8656-565656565656")), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease);
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var definition = new AlertRuleDefinition(rule, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(definition, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destination);
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("57575757-5757-4757-8757-575757575757"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        AlertDeliveryWork claimed = Assert.Single(await repository.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await using IAlertDeliveryDispatchPermit permit = await repository.AcquireDeliveryDispatchPermitAsync(claimed, Timeout(), CancellationToken.None);
        AlertDestinationWriteRequest disabled = AlertDestinationWriteRequest.Create(destination, "https-webhook", "integration", false, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.RetireAlertDestination), Timeout()) with { ExpectedRevision = 1, RequestDigest = new string('d', 64) };
        Task<AdministrativeAuditReceipt> transition = server.UpsertDestinationAsync(disabled, CancellationToken.None).AsTask();
        await Task.Delay(150);
        Assert.False(transition.IsCompleted);
        await permit.DisposeAsync();
        AdministrativeAuditReceipt receipt = await transition;
        Assert.NotEqual(Guid.Empty, receipt.AuditId.Value);
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL,cancel_reason,completed_at IS NOT NULL,leased_until IS NULL,lease_work_key IS NULL,lease_owner_execution_id IS NULL,lease_fencing IS NULL FROM alerting.delivery_outbox WHERE delivery_id=@id;", verify);
        command.Parameters.AddWithValue("id", deliveryId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("destination_disabled", reader.GetString(1));
        for (int index = 2; index < 7; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    public async Task DeliveryWorkerAdapterFailureUsesExactFenceRetryBeforePermitRelease()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("58585858-5858-4858-8858-585858585858");
        Guid rule = Guid.Parse("59595959-5959-4959-8959-595959595959");
        Guid destination = Guid.Parse("5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a");
        Guid deliveryId = Guid.Parse("5b5b5b5b-5b5b-4b5b-8b5b-5b5b5b5b5b5b");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.Parse("5c5c5c5c-5c5c-4c5c-8c5c-5c5c5c5c5c5c")), new FencingToken(1));
        await SeedTargetOnlyAsync(database, target);
        await SeedRuleAndDestinationAsync(database, target, rule, destination);
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("5d5d5d5d-5d5d-4d5d-8d5d-5d5d5d5d5d5d"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var dispatcher = new ThrowingDestinationPort();
        var diagnosticLogger = new DeliveryDiagnosticLogger();
        var worker = new SqlObserver.Collector.AlertDeliveryWorker(new PostgreSqlAlertRepositoryPort(collectorDataSource), dispatcher, new PostgreSqlWorkerLeasePort(collectorDataSource), lease.Owner, diagnosticLogger);
        await worker.StartAsync(CancellationToken.None);
        try { await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { throw new InvalidOperationException(string.Join("; ", diagnosticLogger.Errors)); }
        await worker.StopAsync(CancellationToken.None);
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT completed_at IS NULL,cancelled_at IS NULL,leased_until IS NULL,lease_work_key IS NULL,lease_owner_execution_id IS NULL,lease_fencing IS NULL,due_at>clock_timestamp(),attempt,last_error_code,(SELECT outcome FROM alerting.delivery_attempt WHERE delivery_id=@id AND attempt=1) FROM alerting.delivery_outbox WHERE delivery_id=@id;", verify);
        command.Parameters.AddWithValue("id", deliveryId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
        Assert.Equal(1, reader.GetInt32(7));
        Assert.Equal("adapter_failed", reader.GetString(8));
        Assert.Equal("retryable_failure", reader.GetString(9));
        Assert.Equal(1, dispatcher.Invocations);
    }

    [Fact]
    public async Task DeliveryWorkerAdapterCancellationUsesExactFenceRetryBeforePermitRelease()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("5e5e5e5e-5e5e-4e5e-8e5e-5e5e5e5e5e5e");
        Guid rule = Guid.Parse("5f5f5f5f-5f5f-4f5f-8f5f-5f5f5f5f5f5f");
        Guid destination = Guid.Parse("60606060-6060-4060-8060-606060606060");
        Guid deliveryId = Guid.Parse("61616161-6161-4161-8161-616161616161");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.Parse("62626262-6262-4262-8262-626262626262")), new FencingToken(1));
        await SeedTargetOnlyAsync(database, target);
        await SeedRuleAndDestinationAsync(database, target, rule, destination);
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("63636363-6363-4363-8363-636363636363"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var dispatcher = new CancellingDestinationPort();
        var diagnosticLogger = new DeliveryDiagnosticLogger();
        var worker = new SqlObserver.Collector.AlertDeliveryWorker(new PostgreSqlAlertRepositoryPort(collectorDataSource), dispatcher, new PostgreSqlWorkerLeasePort(collectorDataSource), lease.Owner, diagnosticLogger);
        await worker.StartAsync(CancellationToken.None);
        try { await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { throw new InvalidOperationException(string.Join("; ", diagnosticLogger.Errors)); }
        await worker.StopAsync(CancellationToken.None);
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT completed_at IS NULL,cancelled_at IS NULL,leased_until IS NULL,lease_work_key IS NULL,lease_owner_execution_id IS NULL,lease_fencing IS NULL,due_at>clock_timestamp(),attempt,last_error_code,(SELECT outcome FROM alerting.delivery_attempt WHERE delivery_id=@id AND attempt=1) FROM alerting.delivery_outbox WHERE delivery_id=@id;", verify);
        command.Parameters.AddWithValue("id", deliveryId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
        Assert.Equal(1, reader.GetInt32(7));
        Assert.Equal("adapter_cancelled", reader.GetString(8));
        Assert.Equal("retryable_failure", reader.GetString(9));
        Assert.Equal(1, dispatcher.Invocations);
    }

    [Fact]
    public async Task DeliveryWorkerDestinationDriftCancelsBeforeAdapterInvocation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.Parse("64646464-6464-4464-8464-646464646464");
        Guid rule = Guid.Parse("65656565-6565-4565-8565-656565656565");
        Guid destination = Guid.Parse("66666666-6666-4666-8666-666666666666");
        Guid deliveryId = Guid.Parse("67676767-6767-4767-8767-676767676767");
        var targetId = new MonitoredInstanceId(target);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/delivery"), new WorkerExecutionId(Guid.Parse("68686868-6868-4868-8868-686868686868")), new FencingToken(1));
        await SeedTargetOnlyAsync(database, target);
        await SeedRuleAndDestinationAsync(database, target, rule, destination);
        await SeedOutboxAsync(database, target, rule, destination, deliveryId, Guid.Parse("69696969-6969-4969-8969-696969696969"));
        await using var collectorDataSource = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collectorDataSource);
        // An administration fence is exclusive; delivery permits now share with
        // their renewal connection and must not be used to block another sender.
        await using var fence = await database.DataSource.OpenConnectionAsync();
        await using var fenceCommand = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@target::text,0));",fence) { CommandTimeout=10 };
        fenceCommand.Parameters.AddWithValue("target",target);
        await fenceCommand.ExecuteScalarAsync();
        var dispatcher = new CountingDestinationPort();
        var worker = new SqlObserver.Collector.AlertDeliveryWorker(repository, dispatcher, new PostgreSqlWorkerLeasePort(collectorDataSource), lease.Owner, NullLogger<SqlObserver.Collector.AlertDeliveryWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await WaitForClaimAsync(database, deliveryId);
        await using (NpgsqlConnection mutation = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("UPDATE alerting.destination SET revision=revision+1,configuration_digest=@digest WHERE destination_id=@destination AND instance_id=@target;", mutation);
            command.Parameters.AddWithValue("digest", new string('f', 64));
            command.Parameters.AddWithValue("destination", destination);
            command.Parameters.AddWithValue("target", target);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        fenceCommand.CommandText="SELECT pg_advisory_unlock(hashtextextended(@target::text,0));";
        await fenceCommand.ExecuteScalarAsync();
        await WaitForCancelledAsync(database, deliveryId, "unapproved");
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, dispatcher.Invocations);
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var verifyCommand = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL,cancel_reason,completed_at IS NOT NULL,leased_until IS NULL,lease_work_key IS NULL,lease_owner_execution_id IS NULL,lease_fencing IS NULL FROM alerting.delivery_outbox WHERE delivery_id=@id;", verify);
        verifyCommand.Parameters.AddWithValue("id", deliveryId);
        await using NpgsqlDataReader reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("unapproved", reader.GetString(1));
        for (int index = 2; index < 7; index++) Assert.True(reader.GetBoolean(index));
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            await using var partitions = database.DataSource.CreateCommand("SELECT control.ensure_daily_metric_partition(current_date-1); SELECT control.ensure_daily_metric_partition(current_date);" );
            await partitions.ExecuteNonQueryAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static RepositoryCallTimeout Timeout() => new(TimeSpan.FromSeconds(10));

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }

    private static Guid DeterministicShaUuid(string seed)
    {
        string hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return Guid.Parse($"{hex[..12]}5{hex[13..16]}8{hex[17..32]}");
    }

    private static AdministrativeAuditEnvelope Audit(MonitoredInstanceId target, AdministrativeAuditAction action) =>
        new(new ActorSecurityIdentifier("S-1-5-18"), new AuditCorrelationId(Guid.NewGuid()), action, target);

    private static async Task SeedTargetAndLeaseAsync(RepositoryTestDatabase database, Guid target, WorkerLeaseIdentity lease)
    {
        await SeedTargetOnlyAsync(database, target);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT acquired FROM control.acquire_worker_lease(@key,@owner,interval '30 seconds');", connection);
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    private static async Task SeedTargetOnlyAsync(RepositoryTestDatabase database, Guid target)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("INSERT INTO control.observation_target(instance_id,instance_key,display_name,created_at,updated_at,discovery_requested_at) VALUES(@id,@key,@name,now(),now(),now()) ON CONFLICT(instance_id) DO NOTHING;", connection);
        command.Parameters.AddWithValue("id", target);
        command.Parameters.AddWithValue("key", "m8-" + target.ToString("N")[..12]);
        command.Parameters.AddWithValue("name", "M8 integration target");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedApprovedDestinationAsync(RepositoryTestDatabase database, Guid target, Guid destination)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,revision,approved,approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope,configuration_digest)
            VALUES(@destination,@target,'https-webhook','integration',true,1,true,'https-webhook','integration',1,encode(sha256(convert_to(@binding,'UTF8')),'hex'),@target,@config)
            ON CONFLICT(destination_id) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("destination", destination);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("binding", $"{target:D}|{destination:D}|https-webhook|integration|1");
        command.Parameters.AddWithValue("config", new string('d', 64));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedActiveRuleStateAsync(RepositoryTestDatabase database, Guid target, Guid rule)
    {
        Guid alert = Guid.NewGuid();
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            INSERT INTO alerting.rule_state(instance_id,rule_id,alert_id,state,consecutive_matches,first_match_at,last_observed_at,fired_at,episode_started_at,last_value,reason,revision)
            VALUES(@target,@rule,@alert,3,1,@observed,@observed,@observed,@observed,99,'seeded-active',1)
            ON CONFLICT(instance_id,rule_id) DO UPDATE SET alert_id=EXCLUDED.alert_id,state=3,consecutive_matches=1,first_match_at=EXCLUDED.first_match_at,last_observed_at=EXCLUDED.last_observed_at,fired_at=EXCLUDED.fired_at,episode_started_at=EXCLUDED.episode_started_at,last_value=EXCLUDED.last_value,reason=EXCLUDED.reason,revision=EXCLUDED.revision;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("rule", rule);
        command.Parameters.AddWithValue("alert", alert);
        command.Parameters.AddWithValue("observed", observed);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedRuleAndDestinationAsync(RepositoryTestDatabase database, Guid target, Guid rule, Guid destination)
    {
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == AlertRuleKind.MetricThreshold);
        var definition = new AlertRuleDefinition(rule, catalog.Name, catalog.Kind, new MetricId(catalog.Metric!), catalog.Comparison, catalog.Threshold, catalog.Hysteresis, catalog.ConfirmationCount, catalog.ConfirmationWindow, catalog.EvaluationInterval);
        await using var serverDataSource = database.CreateServerDataSource();
        var server = new PostgreSqlAlertRepositoryPort(serverDataSource);
        var targetId = new MonitoredInstanceId(target);
        await server.UpsertRuleAsync(new AlertRuleWriteRequest(definition, Guid.NewGuid().ToString("D"), Audit(targetId, AdministrativeAuditAction.CreateAlertRule), Timeout()), CancellationToken.None);
        await SeedApprovedDestinationAsync(database, target, destination);
    }

    private static async Task SeedEvaluationQueueAsync(RepositoryTestDatabase database, AlertObservation observation)
    {
        var observations = new[]
        {
            new
            {
                TargetId = observation.TargetId.Value, observation.RuleId, observation.ObservedAtUtc, observation.Value,
                observation.CollectorHealthy, observation.Reason, observation.OperationId, observation.EvidenceDigest,
                observation.SampleId, observation.RunId, observation.SourceKind, observation.MetricId,
                observation.SourceCollector, observation.SourceVersion, observation.SourceSchemaVersion, observation.SourceDigest
            }
        };
        string json = JsonSerializer.Serialize(observations);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT @run,@target,'engine.core',1,1,revision,1,'m8-test-source',@owner,1,decode(repeat('a',64),'hex'),@observed-interval '1 minute',@observed FROM control.observation_target WHERE instance_id=@target
            ON CONFLICT(run_id) DO NOTHING;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@run,'succeeded','completed',1,0,1,1,1,1,0,0,0,0,0,false,false,'none',true,0,0,decode(@source_digest,'hex'),@observed)
            ON CONFLICT(run_id) DO NOTHING;
            INSERT INTO telemetry.raw_metric_sample(observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
            VALUES(@observed,@sample::uuid,@target,@metric,@value,'{}'::jsonb,@observed,@run)
            ON CONFLICT(observed_at,sample_id) DO NOTHING;
            INSERT INTO alerting.evaluation_queue(operation_id,instance_id,rule_id,observed_at,observations,due_at,rule_revision,evidence_digest,sample_id,run_id)
            VALUES(@operation,@target,@rule,@observed,@observations::jsonb,clock_timestamp()-interval '1 second',1,decode(@digest,'hex'),@sample,@run);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("operation", observation.OperationId);
        command.Parameters.AddWithValue("target", observation.TargetId.Value);
        command.Parameters.AddWithValue("rule", observation.RuleId);
        command.Parameters.AddWithValue("observed", observation.ObservedAtUtc);
        command.Parameters.AddWithValue("metric", observation.MetricId!);
        command.Parameters.AddWithValue("value", observation.Value!.Value);
        command.Parameters.AddWithValue("source_digest", observation.SourceDigest!);
        command.Parameters.AddWithValue("owner", Guid.Parse("99999999-9999-4999-8999-999999999999"));
        command.Parameters.Add(new NpgsqlParameter("observations", NpgsqlDbType.Jsonb) { Value = json });
        command.Parameters.AddWithValue("digest", observation.EvidenceDigest);
        command.Parameters.AddWithValue("sample", observation.SampleId!);
        command.Parameters.AddWithValue("run", observation.RunId!.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedOutboxAsync(RepositoryTestDatabase database, Guid target, Guid rule, Guid destination, Guid delivery, Guid operation, int eventKind = 1, Guid? alertId = null)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            INSERT INTO alerting.delivery_outbox(delivery_id,alert_id,instance_id,rule_id,destination_id,event_kind,payload,due_at,operation_id,destination_revision,destination_kind,destination_configuration_reference,destination_approval_revision,destination_approval_digest,destination_approval_scope,destination_configuration_digest)
            SELECT @delivery,coalesce(CASE WHEN @override_alert THEN @alert END,s.alert_id,@alert),@target,@rule,@destination,@event_kind,'{}'::jsonb,clock_timestamp()-interval '1 second',@operation,d.revision,d.kind,d.configuration_reference,d.approved_revision,d.approval_digest,d.approval_scope,d.configuration_digest
            FROM alerting.destination d LEFT JOIN alerting.rule_state s ON s.instance_id=@target AND s.rule_id=@rule
            WHERE d.destination_id=@destination AND d.instance_id=@target
            ON CONFLICT(delivery_id) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("delivery", delivery);
        command.Parameters.AddWithValue("alert", alertId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("override_alert", alertId.HasValue);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("rule", rule);
        command.Parameters.AddWithValue("destination", destination);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("event_kind", eventKind);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReleaseLeaseAsync(RepositoryTestDatabase database, WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT control.release_worker_lease(@key,@owner,@fence);", connection);
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    private static async Task AcquireLeaseAsync(RepositoryTestDatabase database, WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT acquired FROM control.acquire_worker_lease(@key,@owner,interval '30 seconds');", connection);
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    private static async Task SetDueNowAsync(RepositoryTestDatabase database, Guid delivery)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE alerting.delivery_outbox SET due_at=clock_timestamp() WHERE delivery_id=@id;", connection);
        command.Parameters.AddWithValue("id", delivery);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertLeaseClearedAsync(RepositoryTestDatabase database, Guid delivery)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT completed_at IS NOT NULL, leased_until IS NULL, lease_work_key IS NULL, lease_owner_execution_id IS NULL, lease_fencing IS NULL FROM alerting.delivery_outbox WHERE delivery_id=@id;", connection);
        command.Parameters.AddWithValue("id", delivery);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 5; index++) Assert.True(reader.GetBoolean(index));
    }

    private static async Task AssertStaleFenceCleanupAsync(RepositoryTestDatabase database, Guid delivery, DateTimeOffset originalDueAt)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT completed_at IS NULL,cancelled_at IS NULL,leased_until IS NULL,lease_work_key IS NULL,lease_owner_execution_id IS NULL,lease_fencing IS NULL,due_at<=@due,attempt,last_error_code FROM alerting.delivery_outbox WHERE delivery_id=@id;", connection);
        command.Parameters.AddWithValue("id", delivery);
        command.Parameters.AddWithValue("due", originalDueAt);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
        Assert.Equal(0, reader.GetInt32(7));
        Assert.True(reader.IsDBNull(8));
    }

    private static async Task AssertLeaseStillOwnedAsync(RepositoryTestDatabase database, WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT owner_execution_id=@owner AND fencing_token=@fence AND released_at IS NULL AND expires_at>clock_timestamp() FROM control.worker_lease WHERE work_key=@key;", connection);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        command.Parameters.AddWithValue("key", lease.Key.Value);
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    private static async Task MakeClaimReclaimableAsync(RepositoryTestDatabase database, Guid delivery)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE alerting.delivery_outbox SET leased_until=clock_timestamp()-interval '1 second',due_at=clock_timestamp() WHERE delivery_id=@id;", connection);
        command.Parameters.AddWithValue("id", delivery);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task MarkClaimCommittedAsync(RepositoryTestDatabase database, Guid delivery, WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE alerting.delivery_outbox SET leased_until=clock_timestamp()+interval '5 minutes',lease_work_key=@key,lease_owner_execution_id=@owner,lease_fencing=@fence WHERE delivery_id=@id;", connection);
        command.Parameters.AddWithValue("id", delivery);
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertNewerClaimPreservedAsync(RepositoryTestDatabase database, Guid delivery, WorkerLeaseIdentity lease, int attempt)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT completed_at IS NULL,cancelled_at IS NULL,leased_until>clock_timestamp(),lease_work_key=@key,lease_owner_execution_id=@owner,lease_fencing=@fence,attempt=@attempt,(SELECT count(*) FROM alerting.delivery_attempt WHERE delivery_id=@id) FROM alerting.delivery_outbox WHERE delivery_id=@id;", connection);
        command.Parameters.AddWithValue("id", delivery);
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        command.Parameters.AddWithValue("attempt", attempt);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
        Assert.Equal(0L, reader.GetInt64(7));
    }

    private static async Task WaitForClaimAsync(RepositoryTestDatabase database, Guid delivery)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT leased_until IS NOT NULL FROM alerting.delivery_outbox WHERE delivery_id=@id;", connection);
            command.Parameters.AddWithValue("id", delivery);
            if ((bool)(await command.ExecuteScalarAsync() ?? false)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The delivery worker did not claim the test outbox row.");
    }

    private static async Task WaitForCancelledAsync(RepositoryTestDatabase database, Guid delivery, string reason)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT cancelled_at IS NOT NULL AND cancel_reason=@reason FROM alerting.delivery_outbox WHERE delivery_id=@id;", connection);
            command.Parameters.AddWithValue("id", delivery);
            command.Parameters.AddWithValue("reason", reason);
            if ((bool)(await command.ExecuteScalarAsync() ?? false)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The delivery worker did not durably cancel the drifted outbox row.");
    }

    private static async Task<ReplaySnapshot> SnapshotReplayRowsAsync(RepositoryTestDatabase database, Guid target, Guid rule)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            SELECT coalesce(jsonb_agg(jsonb_build_object(
                'instance_id',instance_id,'rule_id',rule_id,'alert_id',alert_id,'state',state,
                'consecutive_matches',consecutive_matches,'first_match_at',first_match_at,'last_observed_at',last_observed_at,
                'fired_at',fired_at,'acknowledged_at',acknowledged_at,'resolved_at',resolved_at,'episode_started_at',episode_started_at,
                'acknowledged_by',acknowledged_by,'last_value',last_value,'reason',reason,'delivery_suppressed',delivery_suppressed,
                'alert_episode_id',alert_episode_id,'last_operation_id',last_operation_id,'evidence_digest',encode(evidence_digest,'hex'),
                'last_reason',last_reason,'revision',revision) ORDER BY rule_id),'[]'::jsonb)::text
            FROM alerting.rule_state WHERE instance_id=@target AND rule_id=@rule;
            SELECT coalesce(jsonb_agg(jsonb_build_object(
                'delivery_id',delivery_id,'alert_id',alert_id,'instance_id',instance_id,'rule_id',rule_id,'destination_id',destination_id,
                'event_kind',event_kind,'payload',payload,'attempt',attempt,'due_at',due_at,'leased_until',leased_until,
                'lease_fencing',lease_fencing,'lease_work_key',lease_work_key,'lease_owner_execution_id',lease_owner_execution_id,
                'completed_at',completed_at,'last_error_code',last_error_code,'created_at',created_at,'operation_id',operation_id,
                'evidence_digest',encode(evidence_digest,'hex'),'destination_revision',destination_revision,'destination_kind',destination_kind,
                'destination_configuration_reference',destination_configuration_reference,'destination_approval_revision',destination_approval_revision,
                'destination_approval_digest',destination_approval_digest,'destination_approval_scope',destination_approval_scope,
                'destination_configuration_digest',destination_configuration_digest,'cancelled_at',cancelled_at,'cancel_reason',cancel_reason)
                ORDER BY delivery_id),'[]'::jsonb)::text
            FROM alerting.delivery_outbox WHERE instance_id=@target AND rule_id=@rule;
            SELECT coalesce(jsonb_agg(jsonb_build_object(
                'history_id',history_id,'instance_id',instance_id,'rule_id',rule_id,'alert_id',alert_id,'from_state',from_state,
                'to_state',to_state,'observed_at',observed_at,'reason',reason,'delivery_suppressed',delivery_suppressed,
                'operation_id',operation_id,'evidence_digest',encode(evidence_digest,'hex'),'result_digest',encode(result_digest,'hex'))
                ORDER BY history_id),'[]'::jsonb)::text
            FROM alerting.state_history WHERE instance_id=@target AND rule_id=@rule;
            SELECT coalesce(jsonb_agg(jsonb_build_object(
                'operation_id',operation_id,'instance_id',instance_id,'rule_id',rule_id,'rule_revision',rule_revision,
                'target_binding',target_binding,'rule_binding',rule_binding,'evidence_digest',encode(evidence_digest,'hex'),
                'result_digest',encode(result_digest,'hex'),'result',result,'recorded_at',recorded_at)
                ORDER BY operation_id),'[]'::jsonb)::text
            FROM alerting.evaluation_replay WHERE instance_id=@target AND rule_id=@rule;
            SELECT coalesce(jsonb_agg(jsonb_build_object(
                'operation_id',operation_id,'instance_id',instance_id,'rule_id',rule_id,'observed_at',observed_at,
                'observations',observations,'due_at',due_at,'rule_revision',rule_revision,'cancel_reason',cancel_reason,
                'next_due_at',next_due_at,'claimed_until',claimed_until,'claim_work_key',claim_work_key,
                'claim_owner_execution_id',claim_owner_execution_id,'claim_fencing',claim_fencing,'completed_at',completed_at,
                'evidence_digest',encode(evidence_digest,'hex'),'sample_id',sample_id,'run_id',run_id)
                ORDER BY operation_id),'[]'::jsonb)::text
            FROM alerting.evaluation_queue WHERE instance_id=@target AND rule_id=@rule;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("rule", rule);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        string state = reader.GetString(0);
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        string outbox = reader.GetString(0);
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        string history = reader.GetString(0);
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        string replay = reader.GetString(0);
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        return new ReplaySnapshot(state, outbox, history, replay, reader.GetString(0));
    }

    private sealed class ThrowingDestinationPort : IAlertDestinationPort
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Invocations { get; private set; }
        public ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken)
        {
            Invocations++;
            Started.TrySetResult(true);
            throw new InvalidOperationException("adapter failure");
        }
    }

    private sealed class CancellingDestinationPort : IAlertDestinationPort
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Invocations { get; private set; }
        public async ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken)
        {
            Invocations++;
            Started.TrySetResult(true);
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CountingDestinationPort : IAlertDestinationPort
    {
        public int Invocations { get; private set; }
        public ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken)
        {
            Invocations++;
            return ValueTask.FromResult(new AlertDeliveryResult(work.DeliveryId, true, false, "unexpected-send", DateTimeOffset.UtcNow, work.TargetId));
        }
    }
    private sealed class DeliveryDiagnosticLogger : Microsoft.Extensions.Logging.ILogger<SqlObserver.Collector.AlertDeliveryWorker>
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Errors = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) { var cause=exception.GetBaseException(); Errors.Enqueue(cause is PostgresException pg ? pg.SqlState+": "+pg.MessageText : cause.GetType().Name); }
        }
    }
}
