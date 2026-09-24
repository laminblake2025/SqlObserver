using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed partial class PassiveCollectorBundleMigrationTests(PostgreSql18Fixture fixture)
{
    private const string PriorBundle = "86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e";
    private const string UpdatedBundle = "56bef6e01c8d826a120c1e5edd81db6fccf448fd686400322d69240618ae9191";
    private const string PriorReplicationBundle = "8fa9d8d4c8f3a8fdfb17ffe675f7642220ada826719136d5b7338372866c2b9a";
    private const string UpdatedReplicationBundle = "e9d52f49d1c728ed6968867a1caee11d6a5f288c5326da585b68a9bed0060f36";
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(15)]
    public async Task FreshRepositoryAcceptsTheActualUpdatedApplicationCatalog(int count)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(MigrationBatchResult.MaximumResults);
        await using ServiceProvider application = CreateApplication();
        CollectorCatalogEntry[] entries = application.GetRequiredService<CollectorRegistry>().CatalogEntries.Take(count).ToArray();
        Assert.Equal(UpdatedBundle, SqlServerActivityCollectorAssetCatalog.LoadEmbedded().BundleChecksum);
        Assert.All(entries.Skip(3).Take(4), entry => Assert.Equal(UpdatedBundle, entry.AssetBundleDigest.Value));
        if (count == 15)
        {
            Assert.Equal(UpdatedReplicationBundle, SqlServerReplicationAssetCatalog.LoadEmbedded().BundleChecksum);
            Assert.Equal(UpdatedReplicationBundle, entries[14].AssetBundleDigest.Value);
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await AcquireCatalogLeaseAsync(collector);

        CollectorCatalogReconcileResult result = await new PostgreSqlCollectorRuntimeRepositoryPort(collector)
            .ReconcileCatalogAsync(new ReconcileCollectorCatalogRequest(entries, lease, Timeout), CancellationToken.None);

        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(count, result.UnchangedCount);
        await AssertAppendOnlyTriggerAsync(database);
    }

    [Fact]
    public async Task UpgradeFrom79ChangesOnlyFiveBundlePinsAndRejectsThePriorBundles()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(79);
        await SeedHistoricalRunAsync(database);
        string registryBefore = await ReadPreservedRegistryAsync(database);
        string historyBefore = await ReadHistoryAndSchedulesAsync(database);
        Assert.Equal(Enumerable.Repeat(PriorBundle, 4).Append(PriorReplicationBundle), await ReadRepairedBundlesAsync(database));
        await using ServiceProvider application = CreateApplication();
        CollectorCatalogEntry[] entries = application.GetRequiredService<CollectorRegistry>().CatalogEntries.ToArray();
        CollectorCatalogEntry[] frozenM9Entries = WithBackupBundle(entries, PriorBackupBundle);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await AcquireCatalogLeaseAsync(collector);
        PostgresException before = await Assert.ThrowsAsync<PostgresException>(() => ReconcileDirectAsync(collector, frozenM9Entries, lease));
        Assert.Equal("55000", before.SqlState);

        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        Assert.False(upgrade.HasFailures);
        Assert.Equal(80, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(Enumerable.Repeat(UpdatedBundle, 4).Append(UpdatedReplicationBundle), await ReadRepairedBundlesAsync(database));
        Assert.Equal(registryBefore, await ReadPreservedRegistryAsync(database));
        Assert.Equal(historyBefore, await ReadHistoryAndSchedulesAsync(database));
        await AssertAppendOnlyTriggerAsync(database);
        Assert.Equal(15, await ReconcileDirectAsync(collector, frozenM9Entries, lease));
        CollectorCatalogEntry[] staleActivity = frozenM9Entries.Select(entry => entry.ExecutionOrder == 4
            ? new CollectorCatalogEntry(entry.ExecutionOrder, entry.Manifest, entry.ManifestDigest, new CollectorSha256Digest(PriorBundle))
            : entry).ToArray();
        PostgresException staleActivitySql = await Assert.ThrowsAsync<PostgresException>(() => ReconcileDirectAsync(collector, staleActivity, lease));
        Assert.Equal("55000", staleActivitySql.SqlState);
        // Later forward migrations may now exist after this bounded 79->80
        // upgrade. Apply them once without reapplying the bundle repair, then
        // verify that a fully migrated repository has no pending work.
        string registryBeforeRemaining = await ReadPreservedRegistryAsync(database, omitBackupBundle: true);
        Assert.Equal(Enumerable.Repeat(PriorBackupBundle, 4), await ReadBackupBundlesAsync(database));
        MigrationBatchResult remaining = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(remaining.HasFailures);
        Assert.Equal(PostgreSqlMigrationCatalog.LoadEmbedded().Migrations
            .Where(migration => migration.Descriptor.Number.Value > 80)
            .Select(migration => migration.Descriptor.Number.Value),
            remaining.Results.Select(result => result.Migration.Number.Value));
        Assert.Equal(Enumerable.Repeat(UpdatedBundle, 4).Append(UpdatedReplicationBundle), await ReadRepairedBundlesAsync(database));
        Assert.Equal(Enumerable.Repeat(UpdatedBackupBundle, 4), await ReadBackupBundlesAsync(database));
        Assert.Equal(registryBeforeRemaining, await ReadPreservedRegistryAsync(database, omitBackupBundle: true));
        Assert.Equal(historyBefore, await ReadHistoryAndSchedulesAsync(database));
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Assert.Equal(15, (await runtime.ReconcileCatalogAsync(new ReconcileCollectorCatalogRequest(entries, lease, Timeout), CancellationToken.None)).UnchangedCount);
        foreach (int staleOrder in new[] { 4, 15 })
        {
            CollectorCatalogEntry[] stale = entries.Select(entry => entry.ExecutionOrder == staleOrder
                ? new CollectorCatalogEntry(entry.ExecutionOrder, entry.Manifest, entry.ManifestDigest, new CollectorSha256Digest(staleOrder == 15 ? PriorReplicationBundle : PriorBundle))
                : entry).ToArray();
            await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ReconcileCatalogAsync(new ReconcileCollectorCatalogRequest(stale, lease, Timeout), CancellationToken.None).AsTask());
        }
        MigrationBatchResult repeat = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(repeat.HasFailures);
        Assert.Empty(repeat.Results);
    }

    [Theory]
    [InlineData(false, "blocking.current")]
    [InlineData(true, "blocking.current")]
    [InlineData(false, "replication.health")]
    [InlineData(true, "replication.health")]
    public async Task UnexpectedOrMissingPriorRowRollsBackEveryChangeAndRestoresTheTrigger(bool missing, string collectorId)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(79);
        // Deliberately corrupt only this disposable database to exercise the guard.
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            string mutation = missing
                ? "DELETE FROM control.collector_contract WHERE collector_id=@collector AND collector_version=1;"
                : "UPDATE control.collector_contract SET asset_bundle_sha256=decode(repeat('ff',32),'hex') WHERE collector_id=@collector AND collector_version=1;";
            await using var corrupt = new NpgsqlCommand("ALTER TABLE control.collector_contract DISABLE TRIGGER ALL; " + mutation + " ALTER TABLE control.collector_contract ENABLE TRIGGER ALL;", connection, transaction);
            corrupt.Parameters.AddWithValue("collector", collectorId);
            await corrupt.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        string[] before = await ReadRepairedBundlesAsync(database);

        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        MigrationExecutionResult failure = Assert.Single(result.Results);
        Assert.Equal(80, failure.Migration.Number.Value);
        Assert.Equal(MigrationOutcome.Failed, failure.Outcome);
        Assert.Equal("postgres_55000", failure.FailureCode);
        Assert.Equal(before, await ReadRepairedBundlesAsync(database));
        await using var ledger = database.DataSource.CreateCommand("SELECT max(migration_number) FROM system.schema_migration;");
        Assert.Equal(79, Assert.IsType<int>(await ledger.ExecuteScalarAsync()));
        await AssertAppendOnlyTriggerAsync(database);
    }

    private static ServiceProvider CreateApplication()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync(int maximum)
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
                .ApplyPendingAsync(new MigrationApplyRequest(maximum, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            if (maximum is 79 or 84) Assert.Equal(maximum, result.Results.Count);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<WorkerLeaseIdentity> AcquireCatalogLeaseAsync(NpgsqlDataSource collector)
    {
        LeaseAcquisitionResult acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey("collector/catalog/reconcile"), new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout), CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        return Assert.IsType<WorkerLease>(acquired.Lease).Identity;
    }

    private static async Task<int> ReconcileDirectAsync(NpgsqlDataSource collector, CollectorCatalogEntry[] entries, WorkerLeaseIdentity lease)
    {
        await using var command = collector.CreateCommand("SELECT * FROM control.reconcile_collector_catalog_m10(@ids,@versions,@manifests,@bundles,@orders,@work,@owner,@fence);");
        command.Parameters.AddWithValue("ids", entries.Select(entry => entry.Manifest.Id.Value).ToArray());
        command.Parameters.AddWithValue("versions", entries.Select(entry => entry.Manifest.ManifestVersion.Value).ToArray());
        command.Parameters.AddWithValue("manifests", entries.Select(entry => entry.ManifestDigest.ToByteArray()).ToArray());
        command.Parameters.AddWithValue("bundles", entries.Select(entry => entry.AssetBundleDigest.ToByteArray()).ToArray());
        command.Parameters.AddWithValue("orders", entries.Select(entry => entry.ExecutionOrder).ToArray());
        command.Parameters.AddWithValue("work", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        return reader.GetInt32(2);
    }

    private static async Task AssertAppendOnlyTriggerAsync(RepositoryTestDatabase database)
    {
        await using var enabled = database.DataSource.CreateCommand("SELECT tgenabled='O' FROM pg_trigger WHERE tgrelid='control.collector_contract'::regclass AND tgname='collector_contract_append_only';");
        Assert.True(Assert.IsType<bool>(await enabled.ExecuteScalarAsync()));
        await using var forbidden = database.DataSource.CreateCommand("UPDATE control.collector_contract SET asset_bundle_sha256=asset_bundle_sha256 WHERE collector_id='activity.sessions';");
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
        Assert.Equal("55000", rejected.SqlState);
    }

    private static async Task<string[]> ReadRepairedBundlesAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT array_agg(encode(asset_bundle_sha256,'hex') ORDER BY execution_order) FROM control.collector_contract WHERE collector_id IN ('activity.sessions','activity.requests','waits.server','blocking.current','replication.health') AND collector_version=1;");
        return Assert.IsType<string[]>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadPreservedRegistryAsync(RepositoryTestDatabase database, bool omitBackupBundle = false)
    {
        await using var command = database.DataSource.CreateCommand("SELECT jsonb_agg(CASE WHEN c.collector_version=1 AND (c.collector_id IN ('activity.sessions','activity.requests','waits.server','blocking.current','replication.health') OR (@omit_backup AND c.collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health'))) THEN to_jsonb(c)-'asset_bundle_sha256' ELSE to_jsonb(c) END ORDER BY execution_order)::text FROM control.collector_contract c;");
        command.Parameters.AddWithValue("omit_backup", omitBackupBundle);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadHistoryAndSchedulesAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'runs',(SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id) FROM telemetry.collection_run r),
                'outcomes',(SELECT jsonb_agg(to_jsonb(o) ORDER BY run_id) FROM telemetry.collection_run_outcome o),
                'schedules',(SELECT jsonb_agg(to_jsonb(s) ORDER BY instance_id,collector_id) FROM control.collector_schedule s)
            )::text;
            """);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task SeedHistoricalRunAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,'bundle-upgrade','Bundle upgrade fixture','sql01',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,
                target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'blocking.current',1,1,1,1,@work,@owner,1,decode(repeat('aa',32),'hex'),statement_timestamp(),statement_timestamp());
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,
                response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,
                lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@run,'succeeded','completed',1,0,10,0,0,0,0,0,0,0,0,false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),statement_timestamp());
            UPDATE control.collector_schedule SET collection_interval=collection_interval+interval '1 minute',schedule_revision=schedule_revision+1
            WHERE instance_id=@target AND collector_id='blocking.current';
            """);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("work", $"collector/run/blocking.current/{target:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }
}
