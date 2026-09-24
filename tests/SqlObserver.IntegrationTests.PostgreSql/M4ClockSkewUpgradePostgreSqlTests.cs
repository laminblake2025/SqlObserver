using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M4CollectorPersistenceIntegrationTests
{
    [Fact]
    public async Task ClockSkewUpgradePreservesCommittedHistoryAndOnlyChangesFutureRuns()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new MigrationApplyRequest(116, DefaultTimeout),
            CancellationToken.None)).HasFailures);
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, target, 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items);
        await CommitSuccessAsync(runtime, leases, work, CreateEnginePayload(work));
        string historical = await ResourceRunSummaryAsync(database, target);

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures,
            upgrade.Results.FirstOrDefault(item => item.Outcome == MigrationOutcome.Failed)?.FailureCode);
        Assert.Equal(117, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(historical, await ResourceRunSummaryAsync(database, target));
    }

    [Fact]
    public async Task InventoryAndFileClockSkewRejectRowsWithoutStrandingEitherRun()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);

        var inventoryTarget = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, inventoryTarget, 1);
        await CommitSuccessAsync(runtime, leases, await DueAsync(inventoryTarget, "engine.core"),
            CreateEnginePayload(inventoryTarget, MicrosecondNow()));
        CollectorDueWorkItem inventory = await DueAsync(inventoryTarget, "database.inventory");
        var inventoryPayload = new CollectorPayload(databases: new DatabaseObservationBatch([
            new DatabaseObservation(inventory.TargetId, inventory.TargetRevision, 5,
                new SqlServerObjectName("database_5"), DatabaseOperationalState.Online,
                DatabaseRecoveryModel.Full, DatabaseUserAccess.MultiUser, false, 160,
                MicrosecondNow().AddMinutes(-10))
        ]));
        CollectorRunId inventoryRun = await CommitSkewedAsync(inventory, inventoryPayload);

        var fileTarget = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, fileTarget, 1);
        await CommitSuccessAsync(runtime, leases, await DueAsync(fileTarget, "engine.core"),
            CreateEnginePayload(fileTarget, MicrosecondNow()));
        CollectorDueWorkItem validInventory = await DueAsync(fileTarget, "database.inventory");
        await CommitSuccessAsync(runtime, leases, validInventory, CreateDatabasePayload(validInventory, 5));
        CollectorDueWorkItem files = await DueAsync(fileTarget, "database.files");
        CollectorRunId fileRun = await CommitSkewedAsync(files,
            CreateFileStallPayload(files, 26, 7, 19, MicrosecondNow().AddMinutes(10)));

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.visibility_gap
                    WHERE run_id IN (@inventory_run, @file_run)
                      AND reason_code = 'target_clock_skew'),
                   (SELECT count(*) FROM telemetry.database_inventory_snapshot
                    WHERE collection_run_id = @inventory_run),
                   (SELECT count(*) FROM telemetry.database_file_snapshot
                    WHERE collection_run_id = @file_run)
            """, connection);
        command.Parameters.AddWithValue("inventory_run", inventoryRun.Value);
        command.Parameters.AddWithValue("file_run", fileRun.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));

        async Task<CollectorDueWorkItem> DueAsync(MonitoredInstanceId target, string collectorId) =>
            Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout),
                CancellationToken.None)).Items,
                item => item.TargetId == target && item.CollectorId.Value == collectorId);

        async Task<CollectorRunId> CommitSkewedAsync(CollectorDueWorkItem work, CollectorPayload payload)
        {
            WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
            var runId = new CollectorRunId(Guid.NewGuid());
            Assert.Equal(CollectorRunStartStatus.Started,
                (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout),
                    CancellationToken.None)).Status);
            CommitCollectorRunRequest commit = CreateSuccessCommit(work, runId, lease, payload);
            CollectorRunCommitResult saved = await runtime.CommitRunAsync(commit, CancellationToken.None);
            Assert.Equal(CollectorRunCommitStatus.Committed, saved.Status);
            Assert.Equal(0, saved.InsertedCount);
            Assert.Equal(payload.ItemCount, saved.RejectedCount);
            Assert.Equal(CollectorRunCommitStatus.Replayed,
                (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
            return runId;
        }
    }
}
