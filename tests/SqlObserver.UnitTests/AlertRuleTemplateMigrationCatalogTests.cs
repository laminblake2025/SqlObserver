using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class AlertRuleTemplateMigrationCatalogTests
{
    [Fact]
    public void GroupRuleTemplatesAreChecksumVerifiedForwardTransactionalMigration()
    {
        PostgreSqlMigrationResource migration = Assert.Single(PostgreSqlMigrationCatalog.LoadEmbedded().Migrations,
            item => item.FileName == "0352_alert_rule_group_templates.sql");
        Assert.Equal("0352", migration.Descriptor.Number.ToString()); Assert.True(migration.Descriptor.IsTransactional);
        Assert.Equal(migration.ChecksumHex, migration.Descriptor.Checksum.ToHexString().ToLowerInvariant());
    }
}
