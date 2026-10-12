using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class AlertSnoozeMigrationCatalogTests
{
    [Fact]
    public void EpisodeSnoozeIsAnEmbeddedChecksumVerifiedForwardTransactionalMigration()
    {
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        PostgreSqlMigrationResource migration = Assert.Single(catalog.Migrations,
            item => item.FileName == "0349_alert_episode_snooze.sql");
        Assert.Equal("0349", migration.Descriptor.Number.ToString());
        Assert.True(migration.Descriptor.IsTransactional);
        Assert.Equal(migration.ChecksumHex, migration.Descriptor.Checksum.ToHexString().ToLowerInvariant());
    }
}
