using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task DeadlockQueuePreservesCanonicalIdentityAndConsumesEachScanOnce()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database, count: 2);
        await using NpgsqlDataSource collector = prepared.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        AlertRuleDefinition rule = await SeedDeadlockQueueRuleAsync(database, prepared.Target);
        WorkerLeaseIdentity lease = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        Assert.Equal(0, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        JsonObject observation = Assert.Single(await ClaimDeadlockQueueAsync(database, prepared.Target, lease));
        var port = new PostgreSqlDeadlockAlertEvidencePort(collector);
        DeadlockAlertEvidence evidence = await port.ReadAsync(prepared.Target, 1, lease, Timeout, CancellationToken.None);
        AlertObservation expected = DeadlockAlertObservationFactory.Create(rule,
            evidence with { ReadAtUtc = observation["ObservedAtUtc"]!.GetValue<DateTimeOffset>() });
        Assert.Equal(2, observation["Value"]!.GetValue<int>());
        Assert.Equal(expected.OperationId, observation["OperationId"]!.GetValue<Guid>());
        Assert.Equal(expected.SampleId, observation["SampleId"]!.GetValue<string>());
        Assert.Equal(expected.EvidenceDigest, observation["EvidenceDigest"]!.GetValue<string>());
        Assert.Equal(expected.Reason, observation["Reason"]!.GetValue<string>());
        await AssertDeadlockQueueClaimAsync(database, prepared.Target, observation, lease);
        Assert.Empty(await ClaimDeadlockQueueAsync(database, prepared.Target, lease));
        await ExpireDeadlockQueueAsync(database, prepared.Target);
        JsonObject reclaimed = Assert.Single(await DeadlockQueueCallAsync(database, prepared.Target, lease,
            "SELECT observations FROM alerting.claim_due_evaluations(@target,@key,@owner,@fence,100);"));
        Assert.Equal(observation.ToJsonString(), reclaimed.ToJsonString());
        Assert.Empty(await ClaimDeadlockQueueAsync(database, prepared.Target, lease));
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        {
            await using NpgsqlTransaction transaction = await DeadlockQueueTransactionAsync(connection, prepared.Target);
            await using var gated = new NpgsqlCommand("SELECT * FROM alerting.evaluate_and_enqueue(@target,@observations,'[]'::jsonb,@key,@owner,@fence);", connection, transaction);
            AddDeadlockQueueParameters(gated, prepared.Target, lease);
            gated.Parameters.AddWithValue("observations", NpgsqlDbType.Jsonb, new JsonArray(observation.DeepClone()).ToJsonString());
            PostgresException failure = await Assert.ThrowsAsync<PostgresException>(async () => await gated.ExecuteNonQueryAsync());
            Assert.Equal("22023", failure.SqlState);
            Assert.Equal("Deadlock evaluation batch bindings are invalid", failure.MessageText);
        }
        // Only this isolated migrator fixture simulates consumption. Normal
        // state/outbox commit is covered separately by the commit/runtime tests.
        await ExecuteAsync(database, "UPDATE alerting.evaluation_queue SET completed_at=clock_timestamp(),claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL WHERE instance_id=@target;", ("target", prepared.Target.Value));
        await Task.Delay(TimeSpan.FromSeconds(15.1));
        Assert.Equal(0, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        await using var captures = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.deadlock_evaluation_capture;");
        Assert.Equal(0L, await captures.ExecuteScalarAsync());
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockQueueSupersedesSourcesAndRevisionsWithoutStealingLiveClaims()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = prepared.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        AlertRuleDefinition rule = await SeedDeadlockQueueRuleAsync(database, prepared.Target);
        WorkerLeaseIdentity lease = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        JsonObject original = Assert.Single(await ClaimDeadlockQueueAsync(database, prepared.Target, lease));
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
        var failed = await PrepareExistingTargetCommitAsync(database, prepared.Target, prepared.Runtime, prepared.Leases,
            prepared.Commit.Payload.Deadlocks.Items[0].OccurredAtUtc, 'c');
        CommitCollectorRunRequest denied = DeadlockEvidenceCommit(failed.Commit, [], CollectorRunOutcome.PermissionDenied);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(denied, CancellationToken.None)).Status);
        Assert.Equal(0, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertDeadlockQueueClaimAsync(database, prepared.Target, original, lease))).SqlState);
        await ExpireDeadlockQueueAsync(database, prepared.Target);
        Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        JsonObject unknown = Assert.Single(await ClaimDeadlockQueueAsync(database, prepared.Target, lease));
        Assert.Null(unknown["Value"]);
        Assert.Contains("reason=source_unavailable", unknown["Reason"]!.GetValue<string>(), StringComparison.Ordinal);
        var forged = (JsonObject)unknown.DeepClone();
        forged["Value"] = 0;
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertDeadlockQueueClaimAsync(database, prepared.Target, forged, lease))).SqlState);
        var wrongOwner = new WorkerLeaseIdentity(lease.Key, new(Guid.NewGuid()), lease.FencingToken);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertDeadlockQueueClaimAsync(database, prepared.Target, unknown, wrongOwner))).SqlState);
        await ExecuteAsync(database, "UPDATE alerting.rule SET revision=revision+1,updated_at=clock_timestamp()-interval '1 minute' WHERE rule_id=@rule;", ("rule", rule.RuleId));
        Assert.Equal(0, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        await ExpireDeadlockQueueAsync(database, prepared.Target);
        Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        JsonObject revised = Assert.Single(await ClaimDeadlockQueueAsync(database, prepared.Target, lease));
        Assert.NotEqual(unknown["OperationId"]!.GetValue<Guid>(), revised["OperationId"]!.GetValue<Guid>());
        await ExecuteAsync(database, "UPDATE alerting.rule SET enabled=false WHERE rule_id=@rule;", ("rule", rule.RuleId));
        await ExpireDeadlockQueueAsync(database, prepared.Target);
        Assert.Equal(0, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        Assert.Empty(await ClaimDeadlockQueueAsync(database, prepared.Target, lease));
        await prepared.Leases.ReleaseAsync(new(failed.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockQueueIsPrivateBoundedAndIndependentOfCatalogActivation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = prepared.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        AlertRuleDefinition rule = await SeedDeadlockQueueRuleAsync(database, prepared.Target);
        Assert.False(AlertCatalog.IsApproved(rule));
        WorkerLeaseIdentity lease = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        int[] results = await Task.WhenAll(ReconcileDeadlockQueueAsync(database, prepared.Target, lease),
            ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        Assert.Equal(1, results.Sum());
        foreach (int limit in new[] { 0, 101 })
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
                ReconcileDeadlockQueueAsync(database, prepared.Target, lease, limit))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            "UPDATE alerting.rule SET threshold=0.5 WHERE rule_id=@rule;", ("rule", rule.RuleId)))).SqlState);
        await using (var permissions = database.DataSource.CreateCommand("""
            SELECT c.relrowsecurity,c.relforcerowsecurity,pg_get_userbyid(c.relowner),
                NOT has_table_privilege('sqlobserver_collector',c.oid,'SELECT,INSERT,UPDATE,DELETE'),
                (SELECT contract_version FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts'),
                (SELECT count(*) FROM pg_proc p WHERE p.pronamespace='alerting'::regnamespace
                    AND p.proname IN ('deadlock_alert_observation','reconcile_deadlock_alert_evaluations',
                        'assert_deadlock_evaluation_claim','claim_deadlock_alert_evaluations')
                    AND pg_get_userbyid(p.proowner)='sqlobserver_migrator'
                    AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                    AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                    AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
                    AND NOT EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE'))
            FROM pg_class c WHERE c.oid='alerting.deadlock_evaluation_capture'::regclass;
            """))
        {
            await using NpgsqlDataReader row = await permissions.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            Assert.True(row.GetBoolean(1));
            Assert.Equal("sqlobserver_migrator", row.GetString(2));
            Assert.True(row.GetBoolean(3));
            Assert.Equal(4, row.GetInt32(4));
            Assert.Equal(4L, row.GetInt64(5));
        }
        await using var denied = collector.CreateCommand("SELECT alerting.reconcile_deadlock_alert_evaluations(@target,@key,@owner,@fence,1);");
        AddDeadlockQueueParameters(denied, prepared.Target, lease);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await denied.ExecuteScalarAsync())).SqlState);
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    private static async Task<AlertRuleDefinition> SeedDeadlockQueueRuleAsync(RepositoryTestDatabase database, MonitoredInstanceId target)
    {
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlock.count", AlertRuleKind.DeadlockCount, null,
            AlertComparison.GreaterThan, 0, 0, 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        await ExecuteAsync(database, """
            INSERT INTO alerting.rule(rule_id,instance_id,name,kind,comparison,threshold,hysteresis,
                confirmation_count,clear_confirmation_count,confirmation_window,evaluation_interval,enabled)
            VALUES(@rule,@target,'deadlock.count',7,1,0,0,1,2,interval '5 minutes',interval '15 seconds',true);
            """, ("rule", rule.RuleId), ("target", target.Value));
        return rule;
    }

    private static async Task<NpgsqlTransaction> DeadlockQueueTransactionAsync(NpgsqlConnection connection, MonitoredInstanceId target)
    {
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator; SELECT set_config('sqlobserver.target_scope',@target,true);", connection, transaction);
        command.Parameters.AddWithValue("target", target.Value.ToString("D"));
        await command.ExecuteNonQueryAsync();
        return transaction;
    }

    private static void AddDeadlockQueueParameters(NpgsqlCommand command, MonitoredInstanceId target, WorkerLeaseIdentity lease, int limit = 100)
    {
        command.Parameters.AddWithValue("target", target.Value);
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        command.Parameters.AddWithValue("limit", limit);
    }

    private static async Task<int> ReconcileDeadlockQueueAsync(RepositoryTestDatabase database, MonitoredInstanceId target, WorkerLeaseIdentity lease, int limit = 100)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await DeadlockQueueTransactionAsync(connection, target);
        await using var command = new NpgsqlCommand("SELECT alerting.reconcile_deadlock_alert_evaluations(@target,@key,@owner,@fence,@limit);", connection, transaction);
        AddDeadlockQueueParameters(command, target, lease, limit);
        int count = (int)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static Task<List<JsonObject>> ClaimDeadlockQueueAsync(RepositoryTestDatabase database, MonitoredInstanceId target, WorkerLeaseIdentity lease) =>
        DeadlockQueueCallAsync(database, target, lease,
            "SELECT observations FROM alerting.claim_deadlock_alert_evaluations(@target,@key,@owner,@fence,@limit);");

    private static async Task<List<JsonObject>> DeadlockQueueCallAsync(RepositoryTestDatabase database, MonitoredInstanceId target, WorkerLeaseIdentity lease, string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await DeadlockQueueTransactionAsync(connection, target);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddDeadlockQueueParameters(command, target, lease);
        var result = new List<JsonObject>();
        await using (NpgsqlDataReader rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) result.Add((JsonObject)JsonNode.Parse(rows.GetString(0))![0]!.DeepClone());
        await transaction.CommitAsync();
        return result;
    }

    private static async Task AssertDeadlockQueueClaimAsync(RepositoryTestDatabase database, MonitoredInstanceId target, JsonObject observation, WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await DeadlockQueueTransactionAsync(connection, target);
        await using var command = new NpgsqlCommand("SELECT alerting.assert_deadlock_evaluation_claim(@target,@observation,@key,@owner,@fence);", connection, transaction);
        AddDeadlockQueueParameters(command, target, lease);
        command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, observation.ToJsonString());
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static Task ExpireDeadlockQueueAsync(RepositoryTestDatabase database, MonitoredInstanceId target) =>
        ExecuteAsync(database, "UPDATE alerting.evaluation_queue SET claimed_until=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND completed_at IS NULL;", ("target", target.Value));
}
