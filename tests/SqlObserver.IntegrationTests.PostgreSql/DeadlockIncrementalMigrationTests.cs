using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PassiveCollectorBundleMigrationTests
{
    [Fact]
    public async Task IncrementalDeadlockMigrationAppendsPinnedV2WithoutChangingV1HistoryOrSchedules()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(314);
        await SeedHistoricalRunAsync(database);
        string history = await ReadHistoryAndSchedulesAsync(database);
        await using var prior = database.DataSource.CreateCommand("SELECT jsonb_agg(to_jsonb(c) ORDER BY collector_id,collector_version)::text FROM control.collector_contract c WHERE collector_id<>'deadlocks.system-health' OR collector_version=1;");
        string contracts = Assert.IsType<string>(await prior.ExecuteScalarAsync());
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(1, Timeout), default);
        Assert.False(result.HasFailures); Assert.Equal(315, Assert.Single(result.Results).Migration.Number.Value);
        Assert.Equal(contracts, await prior.ExecuteScalarAsync());
        Assert.Equal(history, await ReadHistoryAndSchedulesAsync(database));
        var assets = SqlServerIncrementalDeadlockAssetCatalog.LoadEmbedded();
        await using var v2 = database.DataSource.CreateCommand("""
            SELECT encode(manifest_sha256,'hex'),encode(asset_bundle_sha256,'hex'),maximum_rows,
              (SELECT NOT active FROM control.deadlocks_v2_activation WHERE singleton),
              (SELECT count(*) FROM control.deadlocks_v2_worker_capability)
            FROM control.collector_contract WHERE collector_id='deadlocks.system-health' AND collector_version=2;
            """);
        await using NpgsqlDataReader row = await v2.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync()); Assert.Equal(assets.ManifestChecksum, row.GetString(0));
        Assert.Equal(SqlServerIncrementalDeadlockAssetCatalog.BundleChecksum, row.GetString(1));
        Assert.Equal(8193, row.GetInt32(2)); Assert.True(row.GetBoolean(3)); Assert.Equal(0L, row.GetInt64(4));
        await row.DisposeAsync();
        await AssertAppendOnlyTriggerAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncrementalDeadlockMigrationRejectsMissingOrDriftedPredecessorAtomically(bool missing)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(314);
        await using (var corrupt = database.DataSource.CreateCommand(
            "BEGIN; ALTER TABLE control.collector_contract DISABLE TRIGGER ALL; " +
            (missing ? "DELETE FROM control.collector_contract WHERE collector_id='deadlocks.system-health'; " :
              "UPDATE control.collector_contract SET asset_bundle_sha256=decode(repeat('ff',32),'hex') WHERE collector_id='deadlocks.system-health'; ") +
            "ALTER TABLE control.collector_contract ENABLE TRIGGER ALL; COMMIT;"))
            await corrupt.ExecuteNonQueryAsync();
        await using var before = database.DataSource.CreateCommand("SELECT jsonb_agg(to_jsonb(c) ORDER BY collector_id,collector_version)::text FROM control.collector_contract c;");
        string contracts = Assert.IsType<string>(await before.ExecuteScalarAsync());
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(1, Timeout), default);
        MigrationExecutionResult failure = Assert.Single(result.Results);
        Assert.Equal(315, failure.Migration.Number.Value); Assert.Equal(MigrationOutcome.Failed, failure.Outcome);
        Assert.Equal("postgres_55000", failure.FailureCode); Assert.Equal(contracts, await before.ExecuteScalarAsync());
        await using var gate = database.DataSource.CreateCommand("SELECT to_regclass('control.deadlocks_v2_activation') IS NULL;");
        Assert.True(Assert.IsType<bool>(await gate.ExecuteScalarAsync()));
        await AssertAppendOnlyTriggerAsync(database);
    }
}
