using SqlObserver.Alerting;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.UnitTests;

public sealed class BackupAlertCatalogTests
{
    [Fact]
    public void CatalogApprovesBackupCountsWithConfigurableTimingAndExplicitWireKinds()
    {
        Assert.Equal("b90f9f21d338c62239a82c4244a68537884f03fc6b2166e3c386b1dd1b55af15", AlertCatalog.Digest);
        Assert.Equal(5, AlertCatalog.Entries.Count);
        foreach (AlertCatalogEntry entry in AlertCatalog.Entries.Where(entry => BackupAlertRuleContract.IsBackup(entry.Kind)))
        {
            Assert.Equal(entry.Kind, AlertRuleKindWire.Parse(AlertRuleKindWire.Format(entry.Kind)));
            Assert.Equal("backups.status", entry.SourceCollector); Assert.Equal(2, entry.SourceSchemaVersion);
            Assert.Null(entry.Metric); Assert.Equal(0, entry.Threshold);
            var custom = new AlertRuleDefinition(Guid.NewGuid(), "backup.custom", entry.Kind, null,
                AlertComparison.GreaterThan, 0, 0, 3, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60), clearConfirmationCount: 4);
            Assert.True(AlertCatalog.IsApproved(custom));
        }
        Assert.Throws<ArgumentException>(() => AlertRuleKindWire.Parse("backup_age"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AlertRuleKindWire.Format((AlertRuleKind)999));
    }
}
