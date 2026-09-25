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
    [Fact]
    public async Task CorrectedBackupPermissionProbeRebindsFourCollectorsWithoutChangingHistory()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(113);
        await SeedHistoricalRunAsync(database);
        string registryBefore = await ReadBackupBundleRegistryAsync(database, omitBundle: true);
        string historyBefore = await ReadHistoryAndSchedulesAsync(database);
        Assert.Equal(Enumerable.Repeat("29e5a566ecc9b3b7fedecbd72574371d1e31195726482a6b83099f08202d26a0", 4),
            await ReadBackupBundlesAsync(database));

        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.False(result.HasFailures);
        Assert.Equal(114, Assert.Single(result.Results).Migration.Number.Value);
        Assert.Equal(Enumerable.Repeat(SqlServerOperationalHealthAssetCatalog.LoadEmbedded().BundleChecksum, 4),
            await ReadBackupBundlesAsync(database));
        Assert.Equal(registryBefore, await ReadBackupBundleRegistryAsync(database, omitBundle: true));
        Assert.Equal(historyBefore, await ReadHistoryAndSchedulesAsync(database));
        await AssertAppendOnlyTriggerAsync(database);

        MigrationBatchResult remaining = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(remaining.HasFailures);

        await using ServiceProvider application = CreateApplication();
        CollectorCatalogEntry[] entries = application.GetRequiredService<CollectorRegistry>().CatalogEntries.Take(15).ToArray();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var lease = await AcquireCatalogLeaseAsync(collector);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Assert.Equal(15, (await runtime.ReconcileCatalogAsync(
            new ReconcileCollectorCatalogRequest(entries, lease, Timeout), CancellationToken.None)).UnchangedCount);
    }

    [Fact]
    public async Task CorrectedBackupPermissionProbeRejectsStaleBundleAtomically()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(113);
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
        Assert.Equal(114, failure.Migration.Number.Value);
        Assert.Equal(MigrationOutcome.Failed, failure.Outcome);
        Assert.Equal("postgres_55000", failure.FailureCode);
        Assert.Equal(before, await ReadBackupBundlesAsync(database));
        await AssertAppendOnlyTriggerAsync(database);
    }
}
