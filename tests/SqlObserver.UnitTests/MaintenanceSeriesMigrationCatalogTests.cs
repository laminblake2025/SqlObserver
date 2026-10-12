using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class MaintenanceSeriesMigrationCatalogTests
{
    [Fact]
    public void FiniteMaintenanceSeriesIsChecksumVerifiedForwardTransactionalMigration()
    {
        PostgreSqlMigrationResource migration=Assert.Single(PostgreSqlMigrationCatalog.LoadEmbedded().Migrations,
            item=>item.FileName=="0351_maintenance_series.sql");
        Assert.Equal("0351",migration.Descriptor.Number.ToString());Assert.True(migration.Descriptor.IsTransactional);
        Assert.Equal(migration.ChecksumHex,migration.Descriptor.Checksum.ToHexString().ToLowerInvariant());
    }
}
