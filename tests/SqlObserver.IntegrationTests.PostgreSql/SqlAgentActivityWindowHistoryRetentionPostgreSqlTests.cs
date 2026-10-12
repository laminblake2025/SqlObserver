using System.Globalization;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;
using static SqlObserver.IntegrationTests.PostgreSql.SqlAgentActivityPersistenceTests;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(IsolatedClockPostgreSqlCollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentActivityWindowHistoryRetentionPostgreSqlTests(IsolatedClockPostgreSqlFixture fixture)
{
    private const string Actor = "S-1-5-21-1-2-3-1001";
    private const string DataClass = "sql_agent_activity_observations";
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly AuthorizationContext Administrator = new(new ActorSecurityIdentifier(Actor),
        AuthorizationPrincipalState.Active, [ApplicationRole.SecurityAdministrator], TargetAuthorizationScope.ForAllTargets());

    [Fact]
    public async Task LeasedReceiptRetirementChangesHistoryEpochAndKeepsMissingCanonicalAttemptsExplicit()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var applied = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new(MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(applied.HasFailures, string.Join(",", applied.Results.Where(row => row.Outcome == MigrationOutcome.Failed)
            .Select(row => $"{row.Migration.Number.Value}:{row.FailureCode}")));
        await SqlVolumeWindowHistoryFixture.CutoverCanonicalForeignKeysAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var target = await SeedAsync(database);
        var window = await SqlAgentActivityHistoryFixture.WindowAsync(database);
        var run = await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        await SqlAgentActivityHistoryFixture.CommitAsync(database,target);
        var reader = new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(server);
        var admin = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[IdentityFingerprintKey.RequiredLength]));
        var enabled = new List<string>();
        await using (var inventory = database.DataSource.CreateCommand("SELECT data_class FROM system.retention_policy WHERE enabled ORDER BY data_class"))
        await using (var rows = await inventory.ExecuteReaderAsync())
            while (await rows.ReadAsync()) enabled.Add(rows.GetString(0));
        foreach (string dataClass in enabled)
        {
            var current = await admin.GetPolicyAsync(dataClass, CancellationToken.None);
            await admin.UpdatePolicyAsync(new(Administrator, current.Policy with { Enabled = false }, current.Revision,
                "Isolate retained Agent source qualification", Timeout), CancellationToken.None);
        }
        var policy = await admin.GetPolicyAsync(DataClass, CancellationToken.None);
        Assert.False(policy.Policy.Enabled);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await admin.UpdatePolicyAsync(new(
            Administrator, new RetentionPolicy(DataClass, true, TimeSpan.FromDays(89), 3), policy.Revision,
            "Reject insufficient retained source lifetime", Timeout), CancellationToken.None))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await admin.UpdatePolicyAsync(new(
            Administrator, new RetentionPolicy(DataClass, true, TimeSpan.FromDays(90), 2), policy.Revision,
            "Reject insufficient retained partition floor", Timeout), CancellationToken.None))).SqlState);
        await admin.UpdatePolicyAsync(new(Administrator, new RetentionPolicy(DataClass, true, TimeSpan.FromDays(90), 3),
            policy.Revision, "Reviewed isolated Agent source receipt lifetime", Timeout), CancellationToken.None);
        await fixture.AdvanceAsync(TimeSpan.FromDays(92));
        var catalog = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("collector/catalog/reconcile"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var catalogLease = Assert.IsType<WorkerLease>(catalog.Lease).Identity;
        await using (var partitions = collector.CreateCommand("SELECT control.ensure_sql_agent_activity_partition_set(@owner,@fence)"))
        {
            partitions.Parameters.AddWithValue("owner", catalogLease.Owner.Value);
            partitions.Parameters.AddWithValue("fence", catalogLease.FencingToken.Value);
            Assert.Equal(3, await partitions.ExecuteScalarAsync());
        }
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        var lease = await MaintenanceLeaseAsync(collector);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(lease, Timeout, CancellationToken.None));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
        await using var backup = await database.CreateBackupAsync();
        await using (var recovered = await backup.RestoreAsync())
            Assert.Equal(1L, await CountAsync(recovered, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
        DateTimeOffset expiry = (await NowAsync(database)).AddDays(400);
        Guid operation = Guid.NewGuid(), correlation = Guid.NewGuid();
        const string reason = "Verified native PostgreSQL archive restores immutable Agent source receipts";
        var body = JsonSerializer.SerializeToElement(new
        {
            attestedBy = Actor,
            backupSetReference = backup.Reference,
            expiresAtUtc = expiry.ToString("O", CultureInfo.InvariantCulture),
            digest = backup.Digest,
        });
        var attestation = new AttestationMutationRequest(body, operation, Actor, correlation, reason,
            MutationDigestV1.Attestation(operation, backup.Digest, backup.Reference, Actor, expiry, Actor, "global", correlation, reason));
        Assert.Equal("committed", (await admin.RecordAttestationAsync(attestation, CancellationToken.None)).State);
        // Read a fresh historical cursor after the clock advance, so the following
        // conflict proves receipt/cohort changes rather than cursor age alone.
        var before = Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(
            SqlAgentActivityHistoryFixture.List(target,window,1),CancellationToken.None));
        var cursor = Assert.IsType<SqlAgentActivityWindowHistoryCursor>(before.NextAnchor);
        Assert.Equal(2,before.Coverage.RetainedCaptureCount);
        Assert.Contains(SystemRetentionStepOutcome.Detached, await DrainAsync(maintenance, lease));
        var changed = await Assert.ThrowsAsync<SqlAgentActivityWindowHistoryReadException>(async()=>await reader.ListAsync(
            SqlAgentActivityHistoryFixture.List(target,window,1,cursor),CancellationToken.None));
        Assert.Equal(409,changed.StatusCode);
        var after = Assert.IsType<SqlAgentActivityWindowHistoryPage>(await reader.ListAsync(SqlAgentActivityHistoryFixture.List(target,window),CancellationToken.None));
        Assert.NotEqual(before.ReadEpoch,after.ReadEpoch);Assert.Equal(2,after.Coverage.RunCount);
        Assert.Equal(0,after.Coverage.RetainedReceiptCount);Assert.Equal(0,after.Coverage.RetainedCaptureCount);
        Assert.Equal(0,after.Coverage.FailedRunCount);Assert.Equal(2,after.Coverage.MissingPayloadRunCount);
        Assert.All(after.Items,item=>{Assert.Equal("payload_missing",item.Reason);Assert.False(item.ReceiptRetained);Assert.Null(item.SourceRowsRead);});
        var exact = Assert.IsType<SqlAgentActivityWindowHistoryExactPage>(await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,run.Id.Value),CancellationToken.None));
        Assert.NotNull(exact.Receipt);Assert.Null(exact.Capture);Assert.Equal("payload_missing",exact.Reason);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run", ("run", run.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run", ("run", run.Id.Value)));
        await fixture.AdvanceAsync(TimeSpan.FromDays(1));
        lease = await MaintenanceLeaseAsync(collector);
        Assert.Contains(SystemRetentionStepOutcome.Dropped, await DrainAsync(maintenance, lease));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run", ("run", run.Id.Value)));
        await using var restored = await backup.RestoreAsync();
        Assert.Equal(1L, await CountAsync(restored, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
        await using var restoredServer = restored.CreateServerDataSource();
        Assert.NotNull((await new PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(restoredServer).GetAsync(
            SqlAgentActivityHistoryFixture.Exact(target,window,run.Id.Value),CancellationToken.None))!.Capture);
        Assert.Null((await reader.GetAsync(SqlAgentActivityHistoryFixture.Exact(target,window,run.Id.Value),CancellationToken.None))!.Capture);
    }

    private static async Task<WorkerLeaseIdentity> MaintenanceLeaseAsync(NpgsqlDataSource collector)
    {
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("retention/maintenance"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        return Assert.IsType<WorkerLease>(acquired.Lease).Identity;
    }

    private static async Task<List<SystemRetentionStepOutcome>> DrainAsync(PostgreSqlPartitionMaintenancePort maintenance,
        WorkerLeaseIdentity lease)
    {
        var outcomes = new List<SystemRetentionStepOutcome>();
        for (int index = 0; index < 32; index++)
        {
            var outcome = await maintenance.RunSystemRetentionStepAsync(lease, Timeout, CancellationToken.None);
            outcomes.Add(outcome);
            if (outcome == SystemRetentionStepOutcome.Idle) return outcomes;
            Assert.True(outcome is SystemRetentionStepOutcome.Detached or SystemRetentionStepOutcome.Dropped);
        }
        throw new InvalidOperationException("Bounded Agent receipt retirement did not become idle.");
    }
}
