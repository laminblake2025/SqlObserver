using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PassiveCollectorBundleMigrationTests
{
    private const string PriorBackupBundle = "5697aaf35aee3f30f339de5fd973041978b6a0d767759e30cd223eb829e74484";
    private const string UpdatedBackupBundle = "5cead2f81a535b7726c5eb6c6b4abac35cbc8cd85301b60e1867ba694e4a3e8e";

    [Fact]
    public async Task BackupBundleUpgradeFrom84ChangesOnlyFourPinsAndPreservesHistory()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(84);
        await SeedHistoricalRunAsync(database);
        await SeedHistoricalBackupForBundleAsync(database);
        string registryBefore = await ReadBackupBundleRegistryAsync(database, omitBundle: true);
        string historyBefore = await ReadHistoryAndSchedulesAsync(database);
        string backupsBefore = await ReadHistoricalBackupsForBundleAsync(database);
        Assert.Equal(Enumerable.Repeat(PriorBackupBundle, 4), await ReadBackupBundlesAsync(database));
        await using ServiceProvider application = CreateApplication();
        CollectorCatalogEntry[] entries = application.GetRequiredService<CollectorRegistry>().CatalogEntries.ToArray();
        string currentBundle = SqlServerOperationalHealthAssetCatalog.LoadEmbedded().BundleChecksum;
        Assert.All(entries.Skip(9).Take(4), entry => Assert.Equal(currentBundle, entry.AssetBundleDigest.Value));
        CollectorCatalogEntry[] m85Entries = WithActivityBundle(entries, M80Bundle);
        CollectorCatalogEntry[] backupEntries = WithBackupBundle(m85Entries, UpdatedBackupBundle);
        CollectorCatalogEntry[] priorEntries = WithBackupBundle(m85Entries, PriorBackupBundle);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var lease = await AcquireCatalogLeaseAsync(collector);
        Assert.Equal(15, await ReconcileDirectAsync(collector, priorEntries, lease));
        PostgresException beforeUpgrade = await Assert.ThrowsAsync<PostgresException>(() => ReconcileDirectAsync(collector, backupEntries, lease));
        Assert.Equal("55000", beforeUpgrade.SqlState);

        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        Assert.False(upgrade.HasFailures);
        Assert.Equal(85, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(Enumerable.Repeat(UpdatedBackupBundle, 4), await ReadBackupBundlesAsync(database));
        Assert.Equal(registryBefore, await ReadBackupBundleRegistryAsync(database, omitBundle: true));
        Assert.Equal(historyBefore, await ReadHistoryAndSchedulesAsync(database));
        Assert.Equal(backupsBefore, await ReadHistoricalBackupsForBundleAsync(database));
        await AssertAppendOnlyTriggerAsync(database);
        Assert.Equal(15, await ReconcileDirectAsync(collector, backupEntries, lease));
        PostgresException stale = await Assert.ThrowsAsync<PostgresException>(() => ReconcileDirectAsync(collector, priorEntries, lease));
        Assert.Equal("55000", stale.SqlState);

        // The Agent source-time migration advances the same four pins later.
        // Keep the 84->85 assertion frozen, then validate the current runtime.
        MigrationBatchResult remaining = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(remaining.HasFailures);
        Assert.Equal(Enumerable.Repeat(currentBundle, 4), await ReadBackupBundlesAsync(database));
        Assert.Equal(registryBefore, await ReadBackupBundleRegistryAsync(database, omitBundle: true));
        Assert.Equal(historyBefore, await ReadHistoryAndSchedulesAsync(database));
        Assert.Equal(backupsBefore, await ReadHistoricalBackupsForBundleAsync(database));
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Assert.Equal(15, (await runtime.ReconcileCatalogAsync(new ReconcileCollectorCatalogRequest(entries, lease, Timeout), CancellationToken.None)).UnchangedCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ReconcileCatalogAsync(new ReconcileCollectorCatalogRequest(priorEntries, lease, Timeout), CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(false, "backups.status")]
    [InlineData(true, "availability-groups.health")]
    public async Task BackupBundleDriftOrMissingRowRollsBackAllPinsAndRestoresAppendOnly(bool missing, string collectorId)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(84);
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            // Deliberately corrupt only the disposable fixture to exercise the
            // migration's exact four-row guard and transactional trigger reset.
            string mutation = missing
                ? "DELETE FROM control.collector_contract WHERE collector_id=@collector AND collector_version=1;"
                : "UPDATE control.collector_contract SET asset_bundle_sha256=decode(repeat('ff',32),'hex') WHERE collector_id=@collector AND collector_version=1;";
            await using var corrupt = new NpgsqlCommand("ALTER TABLE control.collector_contract DISABLE TRIGGER ALL; " + mutation + " ALTER TABLE control.collector_contract ENABLE TRIGGER ALL;", connection, transaction);
            corrupt.Parameters.AddWithValue("collector", collectorId);
            await corrupt.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        string registryBefore = await ReadBackupBundleRegistryAsync(database, omitBundle: false);
        string historyBefore = await ReadHistoryAndSchedulesAsync(database);

        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        MigrationExecutionResult failed = Assert.Single(result.Results);
        Assert.Equal(85, failed.Migration.Number.Value);
        Assert.Equal(MigrationOutcome.Failed, failed.Outcome);
        Assert.Equal("postgres_55000", failed.FailureCode);
        Assert.Equal(registryBefore, await ReadBackupBundleRegistryAsync(database, omitBundle: false));
        Assert.Equal(historyBefore, await ReadHistoryAndSchedulesAsync(database));
        await using var ledger = database.DataSource.CreateCommand("SELECT max(migration_number) FROM system.schema_migration");
        Assert.Equal(84, Assert.IsType<int>(await ledger.ExecuteScalarAsync()));
        await AssertAppendOnlyTriggerAsync(database);
    }

    [Fact]
    public async Task CurrentOperationalBundleUpgradePreservesHistoricalRunsAndSchedules()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(108);
        await SeedHistoricalRunAsync(database);
        string registryBefore = await ReadBackupBundleRegistryAsync(database, omitBundle: true);
        string historyBefore = await ReadHistoryAndSchedulesAsync(database);
        Assert.Equal(Enumerable.Repeat("8fa22b58b193640b94e1290fc48820f3d68afdfda9076809f4e9b4fc3b2fa593", 4), await ReadBackupBundlesAsync(database));

        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        Assert.False(upgrade.HasFailures);
        Assert.Equal(109, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(Enumerable.Repeat(SqlServerOperationalHealthAssetCatalog.LoadEmbedded().BundleChecksum, 4), await ReadBackupBundlesAsync(database));
        Assert.Equal(registryBefore, await ReadBackupBundleRegistryAsync(database, omitBundle: true));
        Assert.Equal(historyBefore, await ReadHistoryAndSchedulesAsync(database));
        await AssertAppendOnlyTriggerAsync(database);
    }

    [Fact]
    public async Task CurrentOperationalBundleUpgradeRejectsUnexpectedPriorPinAtomically()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(108);
        await using (var corrupt = database.DataSource.CreateCommand("""
            ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
            UPDATE control.collector_contract SET asset_bundle_sha256=decode(repeat('ff',32),'hex')
            WHERE collector_id='availability-groups.health' AND collector_version=1;
            ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
            """)) await corrupt.ExecuteNonQueryAsync();
        string[] before = await ReadBackupBundlesAsync(database);

        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        MigrationExecutionResult failure = Assert.Single(result.Results);
        Assert.Equal(109, failure.Migration.Number.Value);
        Assert.Equal(MigrationOutcome.Failed, failure.Outcome);
        Assert.Equal("postgres_55000", failure.FailureCode);
        Assert.Equal(before, await ReadBackupBundlesAsync(database));
        await using var ledger = database.DataSource.CreateCommand("SELECT max(migration_number) FROM system.schema_migration;");
        Assert.Equal(108, Assert.IsType<int>(await ledger.ExecuteScalarAsync()));
        await AssertAppendOnlyTriggerAsync(database);
    }

    private static CollectorCatalogEntry[] WithBackupBundle(CollectorCatalogEntry[] entries, string bundle) =>
        entries.Select(entry => entry.Manifest.Id.Value is "backups.status" or "sql-agent.failures" or "tempdb.health" or "availability-groups.health"
            ? new CollectorCatalogEntry(entry.ExecutionOrder, entry.Manifest, entry.ManifestDigest, new CollectorSha256Digest(bundle))
            : entry).ToArray();

    private static async Task<string[]> ReadBackupBundlesAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT array_agg(encode(asset_bundle_sha256,'hex') ORDER BY execution_order) FROM control.collector_contract WHERE collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health') AND collector_version=1");
        return Assert.IsType<string[]>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadBackupBundleRegistryAsync(RepositoryTestDatabase database, bool omitBundle)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_agg(CASE WHEN @omit_bundle AND collector_version=1
                AND collector_id IN ('backups.status','sql-agent.failures','tempdb.health',
                    'availability-groups.health','activity.sessions','activity.requests',
                    'waits.server','blocking.current')
                THEN to_jsonb(c)-'asset_bundle_sha256' ELSE to_jsonb(c) END
                ORDER BY collector_id,collector_version)::text
            FROM control.collector_contract c;
            """);
        command.Parameters.AddWithValue("omit_bundle", omitBundle);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadHistoricalBackupsForBundleAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT jsonb_agg(to_jsonb(b) ORDER BY instance_id,run_id,observed_at,database_fingerprint,backup_kind)::text FROM telemetry.backup_status_snapshot b");
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task SeedHistoricalBackupForBundleAsync(RepositoryTestDatabase database)
    {
        Guid runId = Guid.NewGuid();
        await using var command = database.DataSource.CreateCommand("""
            SELECT control.ensure_m9_daily_partitions(current_date);
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,
                target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT @run,instance_id,'backups.status',1,1,1,1,'collector/run/backups.status/'||replace(instance_id::text,'-',''),
                owner_execution_id,1,decode(repeat('cc',32),'hex'),statement_timestamp(),statement_timestamp()
            FROM telemetry.collection_run WHERE collector_id='blocking.current' LIMIT 1;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,
                response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,
                lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@run,'succeeded','completed',1,0,10,1,1,1,0,0,160,160,160,false,false,'none',true,0,0,decode(repeat('dd',32),'hex'),statement_timestamp());
            INSERT INTO telemetry.backup_status_snapshot(instance_id,target_revision,run_id,observed_at,database_fingerprint,
                backup_kind,backup_set_id,last_finish_utc,source_local_finish,source_time_unknown,size_bytes,copy_only,has_checksum,is_damaged,coverage)
            SELECT instance_id,1,@run,statement_timestamp(),decode(repeat('ee',32),'hex'),1,9,NULL,
                timestamp '2026-08-25 12:00:00',true,1024,true,true,false,1
            FROM telemetry.collection_run WHERE run_id=@run;
            UPDATE control.collector_schedule SET collection_interval=collection_interval+interval '1 minute',schedule_revision=schedule_revision+1
            WHERE instance_id=(SELECT instance_id FROM telemetry.collection_run WHERE run_id=@run) AND collector_id='backups.status';
            """);
        command.Parameters.AddWithValue("run", runId);
        await command.ExecuteNonQueryAsync();
    }
}
