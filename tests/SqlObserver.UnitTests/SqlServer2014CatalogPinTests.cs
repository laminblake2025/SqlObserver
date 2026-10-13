using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServer2014CatalogPinTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(5, false)] [InlineData(5, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    public async Task ChangedCompatibilityManifestOrBundleFailsBeforeOpeningRepository(int index, bool bundle)
    {
        var core = SqlServerCollectorAssetCatalog.LoadWithSqlServer2014Support();
        var activity = SqlServerActivityCollectorAssetCatalog.LoadWithSqlServer2014Support();
        var entries = core.Collectors.Select(item => (Asset: item, Bundle: core.BundleChecksum))
            .Concat(activity.Collectors.Select(item => (Asset: item, Bundle: activity.BundleChecksum)))
            .Select((item, order) => new CollectorCatalogEntry(order + 1, item.Asset.Manifest,
                new(item.Asset.ManifestChecksum), new(item.Bundle))).ToArray();
        var original = entries[index];
        entries[index] = new(original.ExecutionOrder, original.Manifest,
            bundle ? original.ManifestDigest : new(new string('0', 64)),
            bundle ? new(new string('0', 64)) : original.AssetBundleDigest);
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=invalid;Username=invalid;Timeout=1");
        var repository = new PostgreSqlCollectorRuntimeRepositoryPort(source);
        var request = new ReconcileCollectorCatalogRequest(entries,
            new(new("collector/catalog/reconcile"), new(Guid.NewGuid()), new(1)), new(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReconcileCatalogAsync(request, default).AsTask());
    }
}
