using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    [Fact]
    public async Task BlockingCatalogRuntimeRejectsStaleSourceTargetRevisionAndWrongLease()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset at = await ReadRepositoryClockAsync(database); Guid run = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, run, "blocking.current", at, 1);
        await EnsureBlockingPartitionAsync(database, at); await InsertBlockingAlertEdgeAsync(database, target, run, at, 71, 65001);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var admin = new PostgreSqlAlertRepositoryPort(server); var repository = new PostgreSqlAlertRepositoryPort(collector);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "blocking.fenced", AlertRuleKind.BlockingDuration, null,
            AlertComparison.GreaterThan, 60, 10, 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), clearConfirmationCount: 1);
        await admin.UpsertRuleAsync(new(rule, Guid.NewGuid().ToString("D"),
            BlockingRuleAudit(target, AdministrativeAuditAction.CreateAlertRule), Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = await BlockingAlertLeaseAsync(collector);
        AlertEvaluationBatch batch = await BlockingRuntimeBatchAsync(repository, lease,
            await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        string before = await BlockingCommitSnapshotAsync(database, target);
        var wrong = new WorkerLeaseIdentity(lease.Key, new(Guid.NewGuid()), lease.FencingToken);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch with {
                Lease=wrong, ClaimedWork=batch.ClaimedWork!.Select(w => w with { OwnerExecutionId=wrong.Owner }).ToArray()
            }, CancellationToken.None))).SqlState);
        Assert.Equal(before, await BlockingCommitSnapshotAsync(database, target));
        at = await ReadRepositoryClockAsync(database);
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "blocking.current", at, 0);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch, CancellationToken.None))).SqlState);
        Assert.Equal(before, await BlockingCommitSnapshotAsync(database, target));
        await ExpireBlockingQueueClaimsAsync(database, target);
        batch = await BlockingRuntimeBatchAsync(repository, lease,
            await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        Assert.Equal(0, Assert.Single(batch.Observations).Value);
        Assert.Equal(1, (await repository.EvaluateAndPersistAsync(batch, CancellationToken.None)).Evaluated);
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        at = await ReadRepositoryClockAsync(database); run = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, run, "blocking.current", at, 1);
        await InsertBlockingAlertEdgeAsync(database, target, run, at, 72, 65001);
        batch = await BlockingRuntimeBatchAsync(repository, lease,
            await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        before = await BlockingCommitSnapshotAsync(database, target);
        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;", ("target", target.Value));
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch, CancellationToken.None))).SqlState);
        Assert.Equal(before, await BlockingCommitSnapshotAsync(database, target));
    }

    [Fact]
    public async Task BlockingCatalogAdministrationDrivesRuntimeAndFencesAcknowledgementAndRuleChanges()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset at = await ReadRepositoryClockAsync(database); Guid run = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, run, "blocking.current", at, 1);
        await EnsureBlockingPartitionAsync(database, at); await InsertBlockingAlertEdgeAsync(database, target, run, at, 71, 65001);
        await SeedBlockingCommitDestinationAsync(database, target);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var admin = new PostgreSqlAlertRepositoryPort(server); var repository = new PostgreSqlAlertRepositoryPort(collector);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "blocking.custom", AlertRuleKind.BlockingDuration, null,
            AlertComparison.GreaterThanOrEqual, 60, 10, 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), clearConfirmationCount: 1);
        var request = new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"),
            BlockingRuleAudit(target, AdministrativeAuditAction.CreateAlertRule), Timeout);
        AdministrativeAuditReceipt first = await admin.UpsertRuleAsync(request, CancellationToken.None);
        AdministrativeAuditReceipt replay = await admin.UpsertRuleAsync(request, CancellationToken.None);
        Assert.Equal(first.AuditId.Value, replay.AuditId.Value); Assert.Equal(first.RecordedAtUtc, replay.RecordedAtUtc);
        AlertRuleListItem listed = Assert.Single((await admin.ListRulesForAdminAsync(target, 100, null, Timeout, CancellationToken.None)).Items);
        Assert.Equal(rule.Kind, listed.Kind); Assert.Null(listed.MetricId); Assert.Equal(60, listed.Threshold);
        WorkerLeaseIdentity lease = await BlockingAlertLeaseAsync(collector);
        AlertEvaluationBatch batch = await BlockingRuntimeBatchAsync(repository, lease,
            await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        Assert.Equal(1, (await repository.EvaluateAndPersistAsync(batch, CancellationToken.None)).Changed);
        AlertRuleState firing = (await repository.GetStateAsync(target, rule.RuleId, Timeout, CancellationToken.None))!;
        Assert.Equal(AlertState.Firing, firing.State);

        await Task.Delay(TimeSpan.FromSeconds(5.1));
        at = await ReadRepositoryClockAsync(database); run = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, run, "blocking.current", at, 1);
        await InsertBlockingAlertEdgeAsync(database, target, run, at, 72, 65001);
        IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None);
        Assert.Single(work); batch = await BlockingRuntimeBatchAsync(repository, lease, work);
        // Acknowledgement arrives after evaluation read the old state but before commit.
        await using (NpgsqlConnection connection = await server.OpenConnectionAsync())
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using var acknowledge = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT * FROM alerting.acknowledge(@alert,@target,@operation,'S-1-5-21-1-2-3-1234',
                    @correlation,@revision,@episode,NULL);
                """, connection, transaction);
            acknowledge.Parameters.AddWithValue("target", target.Value); acknowledge.Parameters.AddWithValue("alert", firing.AlertId!.Value);
            acknowledge.Parameters.AddWithValue("operation", Guid.NewGuid().ToString("D")); acknowledge.Parameters.AddWithValue("correlation", Guid.NewGuid());
            acknowledge.Parameters.AddWithValue("revision", firing.Revision); acknowledge.Parameters.AddWithValue("episode", firing.EpisodeId!.Value);
            await acknowledge.ExecuteNonQueryAsync(); await transaction.CommitAsync();
        }
        string before = await BlockingCommitSnapshotAsync(database, target);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch, CancellationToken.None))).SqlState);
        Assert.Equal(before, await BlockingCommitSnapshotAsync(database, target));
        batch = await BlockingRuntimeBatchAsync(repository, lease, work);
        Assert.Equal(1, (await repository.EvaluateAndPersistAsync(batch, CancellationToken.None)).Evaluated);
        AlertRuleState acknowledged = (await repository.GetStateAsync(target, rule.RuleId, Timeout, CancellationToken.None))!;
        Assert.Equal(AlertState.Acknowledged, acknowledged.State); Assert.Equal(firing.EpisodeId, acknowledged.EpisodeId);

        var changed = new AlertRuleDefinition(rule.RuleId, "blocking.revised", rule.Kind, null, AlertComparison.GreaterThan,
            65.001, 5.001, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), clearConfirmationCount: 3);
        await admin.UpsertRuleAsync(new(changed, Guid.NewGuid().ToString("D"),
            BlockingRuleAudit(target, AdministrativeAuditAction.UpdateAlertRule), Timeout, 1), CancellationToken.None);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await repository.EvaluateAndPersistAsync(batch, CancellationToken.None))).SqlState);
        var retagged = new AlertRuleDefinition(rule.RuleId, "connections.retagged", AlertRuleKind.MetricThreshold,
            new MetricId("engine.user_connections"), AlertComparison.GreaterThan, 60, 10, 1,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 1);
        Assert.Equal(400, (await Assert.ThrowsAsync<AlertRepositoryOperationException>(async () =>
            await admin.UpsertRuleAsync(new(retagged, Guid.NewGuid().ToString("D"),
                BlockingRuleAudit(target, AdministrativeAuditAction.UpdateAlertRule), Timeout, 2), CancellationToken.None))).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        Assert.Single(await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        var disabled = new AlertRuleDefinition(changed.RuleId, changed.Name, changed.Kind, null, changed.Comparison,
            changed.Threshold, changed.Hysteresis, changed.ConfirmationCount, changed.ConfirmationWindow,
            changed.EvaluationInterval, false, changed.ClearConfirmationCount);
        await admin.UpsertRuleAsync(new(disabled, Guid.NewGuid().ToString("D"),
            BlockingRuleAudit(target, AdministrativeAuditAction.RetireAlertRule), Timeout, 2), CancellationToken.None);
        Assert.Empty(await repository.ClaimDueEvaluationsAsync(lease, 100, Timeout, CancellationToken.None));
        Assert.Equal(AlertState.Resolved, (await repository.GetStateAsync(target, rule.RuleId, Timeout, CancellationToken.None))!.State);
        Assert.Equal(0L, await BlockingQueueCountAsync(database, target,
            "SELECT count(*) FROM alerting.evaluation_queue WHERE instance_id=@target AND completed_at IS NULL;"));
        await using var binding = database.DataSource.CreateCommand("""
            SELECT contract_version=4 AND catalog_digest=@digest FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts';
            """);
        binding.Parameters.AddWithValue("digest", AlertCatalog.Digest); Assert.Equal(true, await binding.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BlockingCatalogSqlRejectsForgedSourceMetricAndDurationSettings()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid()); await InsertActiveTargetAsync(database, target);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var wire = new Dictionary<string, object?> {
            ["Action"]="CreateAlertRule", ["RuleId"]=Guid.NewGuid(), ["TargetId"]=target.Value, ["Name"]="blocking.native",
            ["Kind"]=6, ["MetricId"]=null, ["Comparison"]=2, ["Threshold"]=60, ["Hysteresis"]=10, ["ConfirmationCount"]=2,
            ["ClearConfirmationCount"]=2, ["ConfirmationWindow"]="00:05:00", ["EvaluationInterval"]="00:00:15", ["Enabled"]=true,
            ["SourceCollector"]="blocking.current", ["SourceSchemaVersion"]=1, ["CatalogDigest"]=AlertCatalog.Digest,
        };
        foreach ((string field, object? value) in new (string, object?)[] {
            ("SourceCollector","engine.core"), ("SourceCollector",null), ("SourceSchemaVersion",2),
            ("MetricId","engine.user_connections"), ("MetricId",""), ("Comparison",3), ("Threshold",86401),
            ("Threshold",-1), ("Hysteresis",61), ("Hysteresis",-1), ("EvaluationInterval","00:00:04"),
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
            command.Parameters.AddWithValue("rule", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new Dictionary<string, object?>(wire) { [field]=value }));
            command.Parameters.AddWithValue("operation", Guid.NewGuid().ToString("D"));
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync())).SqlState);
        }
        Assert.Equal(0L, await BlockingQueueCountAsync(database, target, "SELECT count(*) FROM alerting.rule WHERE instance_id=@target;"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BlockingCatalogMigrationRejectsDriftWithoutReplacingAdministration(bool missing)
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrator = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrator.ApplyPendingAsync(new(301, Timeout), CancellationToken.None)).HasFailures);
        await using var read = database.DataSource.CreateCommand("SELECT pg_get_functiondef('alerting.upsert_rule(jsonb,text,text,uuid)'::regprocedure);");
        string original = Assert.IsType<string>(await read.ExecuteScalarAsync());
        await ExecuteAsync(database, missing
            ? "DELETE FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts';"
            : "UPDATE alerting.catalog_registry SET catalog_digest=repeat('f',64) WHERE catalog_id='sqlobserver.m8.alerts';");
        Assert.True((await migrator.ApplyPendingAsync(new(1, Timeout), CancellationToken.None)).HasFailures);
        Assert.Equal(original, await read.ExecuteScalarAsync());
        await using var version = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts' AND contract_version=4;");
        Assert.Equal(0L, await version.ExecuteScalarAsync());
    }

    private static AdministrativeAuditEnvelope BlockingRuleAudit(MonitoredInstanceId target, AdministrativeAuditAction action) =>
        new(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"), new(Guid.NewGuid()), action, target);
}
