using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class AlertAssignmentMigrationCatalogTests
{
    [Fact]
    public void EpisodeAssignmentIsAnEmbeddedChecksumVerifiedForwardTransactionalMigration()
    {
        PostgreSqlMigrationCatalog catalog=PostgreSqlMigrationCatalog.LoadEmbedded();
        PostgreSqlMigrationResource migration=Assert.Single(catalog.Migrations,
            item=>item.FileName=="0350_alert_episode_assignment.sql");
        Assert.Equal("0350",migration.Descriptor.Number.ToString());
        Assert.True(migration.Descriptor.IsTransactional);
        Assert.Equal(migration.ChecksumHex,migration.Descriptor.Checksum.ToHexString().ToLowerInvariant());
    }
}
