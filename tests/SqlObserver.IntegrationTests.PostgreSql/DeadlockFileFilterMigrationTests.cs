using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PassiveCollectorBundleMigrationTests
{
    [Fact]
    public async Task DeadlockFileFilterUpgradePreservesHistorySchedulesAndOtherContractFields()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(308);
        await SeedHistoricalRunAsync(database);
        string preserved = await ReadHistoryAndSchedulesAsync(database);
        await using var contracts = database.DataSource.CreateCommand("""
            SELECT jsonb_agg(CASE WHEN collector_id='deadlocks.system-health' AND collector_version=1
                THEN to_jsonb(c)-'asset_bundle_sha256' ELSE to_jsonb(c) END
                ORDER BY collector_id,collector_version)::text FROM control.collector_contract c;
            """);
        string before = Assert.IsType<string>(await contracts.ExecuteScalarAsync());
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new(1, Timeout), CancellationToken.None);
        Assert.False(result.HasFailures);
        Assert.Equal(309, Assert.Single(result.Results).Migration.Number.Value);
        Assert.Equal(before, await contracts.ExecuteScalarAsync());
        Assert.Equal(preserved, await ReadHistoryAndSchedulesAsync(database));
        await using var bundle = database.DataSource.CreateCommand("""
            SELECT encode(asset_bundle_sha256,'hex') FROM control.collector_contract
            WHERE collector_id='deadlocks.system-health' AND collector_version=1;
            """);
        Assert.Equal(SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded().BundleChecksum, await bundle.ExecuteScalarAsync());
        await AssertAppendOnlyTriggerAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlockFileFilterUpgradeRejectsMissingOrDriftedContractAndRestoresTrigger(bool missing)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(308);
        await using (var corrupt = database.DataSource.CreateCommand(
            "ALTER TABLE control.collector_contract DISABLE TRIGGER ALL; " +
            (missing ? "DELETE FROM control.collector_contract WHERE collector_id='deadlocks.system-health'; " :
                "UPDATE control.collector_contract SET asset_bundle_sha256=decode(repeat('ff',32),'hex') WHERE collector_id='deadlocks.system-health'; ") +
            "ALTER TABLE control.collector_contract ENABLE TRIGGER ALL;"))
            await corrupt.ExecuteNonQueryAsync();
        await using var read = database.DataSource.CreateCommand("SELECT jsonb_agg(to_jsonb(c) ORDER BY collector_id,collector_version)::text FROM control.collector_contract c;");
        string before = Assert.IsType<string>(await read.ExecuteScalarAsync());
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new(1, Timeout), CancellationToken.None);
        MigrationExecutionResult failure = Assert.Single(result.Results);
        Assert.Equal(309, failure.Migration.Number.Value);
        Assert.Equal(MigrationOutcome.Failed, failure.Outcome);
        Assert.Equal("postgres_55000", failure.FailureCode);
        Assert.Equal(before, await read.ExecuteScalarAsync());
        await AssertAppendOnlyTriggerAsync(database);
    }
}
