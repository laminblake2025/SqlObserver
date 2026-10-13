using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlServer2014CollectorContractPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromMinutes(2));

    [Fact]
    public async Task ForwardBindingMatchesEmbeddedVariantsPreservesSchedulesAndReconcilesProductionCatalog()
    {
        await using var database = await BeforeUpgradeAsync();
        await ServerLifecycleAdmissionPostgreSqlTests.TargetAsync(database);
        string schedules = await ScheduleDigestAsync(database);
        string contracts = await ContractSettingsDigestAsync(database);
        var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(1, Timeout), default);
        Assert.False(result.HasFailures);
        Assert.Equal(373, Assert.Single(result.Results).Migration.Number.Value);
        Assert.Equal(schedules, await ScheduleDigestAsync(database));
        Assert.Equal(contracts, await ContractSettingsDigestAsync(database));

        var core = SqlServerCollectorAssetCatalog.LoadWithSqlServer2014Support();
        var inventory = SqlServerCollectorAssetCatalog.LoadVersion2WithSqlServer2014Support();
        var activity = SqlServerActivityCollectorAssetCatalog.LoadWithSqlServer2014Support();
        var assets = core.Collectors.Select(item => (Asset: item, Bundle: core.BundleChecksum))
            .Concat(inventory.Collectors.Where(item => item.Manifest.ManifestVersion.Value == 2)
                .Select(item => (Asset: item, Bundle: inventory.BundleChecksum)))
            .Concat(activity.Collectors.Select(item => (Asset: item, Bundle: activity.BundleChecksum))).ToArray();
        Assert.Equal(8, assets.Length);
        foreach (var item in assets)
        {
            await using var command = database.DataSource.CreateCommand("""
                SELECT encode(manifest_sha256,'hex'),encode(asset_bundle_sha256,'hex')
                FROM control.collector_contract WHERE collector_id=@id AND collector_version=@version
                """);
            command.Parameters.AddWithValue("id", item.Asset.Manifest.Id.Value);
            command.Parameters.AddWithValue("version", item.Asset.Manifest.ManifestVersion.Value);
            await using var row = await command.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(item.Asset.ManifestChecksum, row.GetString(0));
            Assert.Equal(item.Bundle, row.GetString(1));
        }
        // The production composition has moved forward; qualify reconciliation
        // after applying its additive contract without changing the 2014 receipt above.
        var current = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(1, Timeout), default);
        Assert.False(current.HasFailures);
        Assert.Equal(374, Assert.Single(current.Results).Migration.Number.Value);
        await ServerLifecycleAdmissionPostgreSqlTests.ReconcileAsync(database);
        Assert.True(await AppendOnlyTriggerEnabledAsync(database));
    }

    [Theory]
    [InlineData("manifest_sha256")]
    [InlineData("asset_bundle_sha256")]
    public async Task PriorDriftRollsBackEveryBindingAndDoesNotAdvanceLedger(string column)
    {
        await using var database = await BeforeUpgradeAsync();
        // Deliberate fixture corruption is restricted to these two named columns.
        Assert.True(column is "manifest_sha256" or "asset_bundle_sha256");
        await using (var corrupt = database.DataSource.CreateCommand($"""
            BEGIN;
            SET LOCAL ROLE sqlobserver_migrator;
            ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
            UPDATE control.collector_contract SET {column}=decode(repeat('00',32),'hex')
            WHERE collector_id='blocking.current' AND collector_version=1;
            ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
            COMMIT;
            """)) await corrupt.ExecuteNonQueryAsync();
        string before = await ContractDigestAsync(database);
        var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(1, Timeout), default);
        var failed = Assert.Single(result.Results);
        Assert.Equal(MigrationOutcome.Failed, failed.Outcome);
        Assert.Equal(373, failed.Migration.Number.Value);
        Assert.Equal(before, await ContractDigestAsync(database));
        Assert.True(await AppendOnlyTriggerEnabledAsync(database));
        await using var history = database.DataSource.CreateCommand("SELECT max(migration_number) FROM system.schema_migration");
        Assert.Equal(372, Convert.ToInt32(await history.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<RepositoryTestDatabase> BeforeUpgradeAsync()
    {
        var database = await fixture.CreateDatabaseAsync();
        try
        {
            var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(372, Timeout), default);
            Assert.False(result.HasFailures, string.Join(",", result.Results.Where(item => item.Outcome == MigrationOutcome.Failed)
                .Select(item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
            Assert.Equal(372, result.Results.Count);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static Task<string> ScheduleDigestAsync(RepositoryTestDatabase database) => ScalarAsync(database,
        "SELECT md5(string_agg(row_to_json(s)::text,',' ORDER BY instance_id,collector_id)) FROM control.collector_schedule s");
    private static Task<string> ContractSettingsDigestAsync(RepositoryTestDatabase database) => ScalarAsync(database,
        "SELECT md5(string_agg((to_jsonb(c)-'manifest_sha256'-'asset_bundle_sha256')::text,',' ORDER BY collector_id,collector_version)) FROM control.collector_contract c");
    private static Task<string> ContractDigestAsync(RepositoryTestDatabase database) => ScalarAsync(database,
        "SELECT md5(string_agg(row_to_json(c)::text,',' ORDER BY collector_id,collector_version)) FROM control.collector_contract c");
    private static async Task<string> ScalarAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<bool> AppendOnlyTriggerEnabledAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT tgenabled='O' FROM pg_trigger
            WHERE tgrelid='control.collector_contract'::regclass AND tgname='collector_contract_append_only'
            """);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
