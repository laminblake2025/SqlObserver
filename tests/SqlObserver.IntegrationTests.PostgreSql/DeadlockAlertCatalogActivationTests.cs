using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task DeadlockCatalogAdministrationCreatesUpdatesAndDisablesRulesThroughNormalRuntime()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = prepared.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        await SeedDeadlockCommitDestinationAsync(database, prepared.Target);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var admin = new PostgreSqlAlertRepositoryPort(server); var repository = new PostgreSqlAlertRepositoryPort(collector);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.custom", AlertRuleKind.DeadlockCount, null,
            AlertComparison.GreaterThan, 0, 0, 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        var request = new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"),
            DeadlockRuleAudit(prepared.Target, AdministrativeAuditAction.CreateAlertRule), Timeout);
        AdministrativeAuditReceipt first = await admin.UpsertRuleAsync(request, CancellationToken.None);
        AdministrativeAuditReceipt replay = await admin.UpsertRuleAsync(request, CancellationToken.None);
        Assert.Equal(first.AuditId.Value, replay.AuditId.Value); Assert.Equal(first.RecordedAtUtc, replay.RecordedAtUtc);
        AlertRuleDefinition listed = Assert.Single(await repository.ListRulesAsync(prepared.Target, Timeout, CancellationToken.None));
        Assert.Equal(rule.Kind, listed.Kind); Assert.Null(listed.MetricId); Assert.Equal(2, listed.ClearConfirmationCount);
        WorkerLeaseIdentity lease = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None);
        Assert.Single(work);
        AlertEvaluationBatch batch = await DeadlockRuntimeBatchAsync(repository, lease, work);
        Assert.Equal(1, (await repository.EvaluateAndPersistAsync(batch, CancellationToken.None)).Evaluated);
        Assert.Equal(AlertState.Firing, (await repository.GetStateAsync(prepared.Target, rule.RuleId, Timeout, CancellationToken.None))!.State);
        var changed = new AlertRuleDefinition(rule.RuleId, "deadlocks.revised", rule.Kind, null, AlertComparison.GreaterThanOrEqual,
            2, 1, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 3);
        await admin.UpsertRuleAsync(new(changed, Guid.NewGuid().ToString("D"),
            DeadlockRuleAudit(prepared.Target, AdministrativeAuditAction.UpdateAlertRule), Timeout, 1), CancellationToken.None);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch, CancellationToken.None))).SqlState);
        var retagged = new AlertRuleDefinition(rule.RuleId, "connections.retagged", AlertRuleKind.MetricThreshold,
            new MetricId("engine.user_connections"), AlertComparison.GreaterThan, 60, 10, 1,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 1);
        Assert.Equal(400, (await Assert.ThrowsAsync<AlertRepositoryOperationException>(async () =>
            await admin.UpsertRuleAsync(new(retagged, Guid.NewGuid().ToString("D"),
                DeadlockRuleAudit(prepared.Target, AdministrativeAuditAction.UpdateAlertRule), Timeout, 2), CancellationToken.None))).StatusCode);
        TimeSpan remaining = batch.Observations[0].ObservedAtUtc.AddSeconds(15.05) - await ReadClockAsync(database);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        Assert.Single(await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        var disabled = new AlertRuleDefinition(changed.RuleId, changed.Name, changed.Kind, null, changed.Comparison,
            changed.Threshold, changed.Hysteresis, changed.ConfirmationCount, changed.ConfirmationWindow,
            changed.EvaluationInterval, false, changed.ClearConfirmationCount);
        await admin.UpsertRuleAsync(new(disabled, Guid.NewGuid().ToString("D"),
            DeadlockRuleAudit(prepared.Target, AdministrativeAuditAction.RetireAlertRule), Timeout, 2), CancellationToken.None);
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        Assert.Equal(AlertState.Resolved, (await repository.GetStateAsync(prepared.Target, rule.RuleId, Timeout, CancellationToken.None))!.State);
        await using var binding = database.DataSource.CreateCommand("""
            SELECT contract_version=5 AND catalog_digest=@digest,
              (SELECT count(*)=0 FROM alerting.evaluation_queue WHERE instance_id=@target AND completed_at IS NULL),
              (SELECT bool_and(cancelled_at IS NOT NULL) FROM alerting.delivery_outbox WHERE instance_id=@target)
            FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts';
            """);
        binding.Parameters.AddWithValue("digest", AlertCatalog.Digest); binding.Parameters.AddWithValue("target", prepared.Target.Value);
        await using (NpgsqlDataReader row = await binding.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            for (int column = 0; column < 3; column++) Assert.True(row.GetBoolean(column));
        }
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockCatalogSqlRejectsForgedSourceMetricFractionalCountsAndTiming()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid()); await SeedDeadlockRuntimeTargetAsync(database, target);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var wire = new Dictionary<string, object?>
        {
            ["Action"] = "CreateAlertRule",
            ["RuleId"] = Guid.NewGuid(),
            ["TargetId"] = target.Value,
            ["Name"] = "deadlocks.native",
            ["Kind"] = 7,
            ["MetricId"] = null,
            ["Comparison"] = 1,
            ["Threshold"] = 0,
            ["Hysteresis"] = 0,
            ["ConfirmationCount"] = 2,
            ["ClearConfirmationCount"] = 2,
            ["ConfirmationWindow"] = "00:05:00",
            ["EvaluationInterval"] = "00:00:15",
            ["Enabled"] = true,
            ["SourceCollector"] = "deadlocks.system-health",
            ["SourceSchemaVersion"] = 1,
            ["CatalogDigest"] = AlertCatalog.Digest,
        };
        foreach ((string field, object? value) in new (string, object?)[] {
            ("SourceCollector","blocking.current"), ("SourceCollector",null), ("SourceSchemaVersion",2),
            ("MetricId","engine.user_connections"), ("MetricId",""), ("Comparison",3), ("Threshold",257),
            ("Threshold",-1), ("Threshold",0.5), ("Hysteresis",0.5), ("Hysteresis",1), ("EvaluationInterval","00:00:14"),
            ("EvaluationInterval","00:01:01"), ("ConfirmationWindow","00:00:00"), ("ClearConfirmationCount",101),
        })
        {
            await using NpgsqlConnection connection = await server.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT * FROM alerting.upsert_rule(@rule,@operation,'S-1-5-21-1-2-3-1234',gen_random_uuid());
                """, connection, transaction);
            command.Parameters.AddWithValue("target", target.Value);
            command.Parameters.AddWithValue("rule", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new Dictionary<string, object?>(wire) { [field] = value }));
            command.Parameters.AddWithValue("operation", Guid.NewGuid().ToString("D"));
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.rule WHERE instance_id=@target;");
        count.Parameters.AddWithValue("target", target.Value); Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlockCatalogMigrationRejectsDriftWithoutReplacingAdministration(bool missing)
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrator = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrator.ApplyPendingAsync(new(307, Timeout), CancellationToken.None)).HasFailures);
        await using var read = database.DataSource.CreateCommand("SELECT pg_get_functiondef('alerting.upsert_rule(jsonb,text,text,uuid)'::regprocedure);");
        string original = Assert.IsType<string>(await read.ExecuteScalarAsync());
        await ExecuteAsync(database, missing
            ? "DELETE FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts';"
            : "UPDATE alerting.catalog_registry SET catalog_digest=repeat('f',64) WHERE catalog_id='sqlobserver.m8.alerts';");
        Assert.True((await migrator.ApplyPendingAsync(new(1, Timeout), CancellationToken.None)).HasFailures);
        Assert.Equal(original, await read.ExecuteScalarAsync());
        await using var version = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts' AND contract_version=5;");
        Assert.Equal(0L, await version.ExecuteScalarAsync());
    }

    private static AdministrativeAuditEnvelope DeadlockRuleAudit(MonitoredInstanceId target, AdministrativeAuditAction action) =>
        new(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"), new(Guid.NewGuid()), action, target);
}
