using SqlObserver.Alerting;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.UnitTests;

public sealed class BlockingAlertCatalogTests
{
    [Fact]
    public void BlockingCatalogBindsSecondsToThePassiveCollectorAndAllowsCustomTiming()
    {
        AlertCatalogEntry entry = Assert.Single(AlertCatalog.Entries, e => e.Kind == AlertRuleKind.BlockingDuration);
        Assert.Equal("blocking.duration", entry.Name);
        Assert.Equal("blocking.current", entry.SourceCollector); Assert.Equal(1, entry.SourceSchemaVersion);
        Assert.Null(entry.Metric); Assert.Equal("seconds", entry.Unit);
        Assert.Equal(60, entry.Threshold); Assert.Equal(10, entry.Hysteresis);
        Assert.Equal(2, entry.ConfirmationCount); Assert.Equal(2, entry.ClearConfirmationCount);
        Assert.Equal(TimeSpan.FromSeconds(15), entry.EvaluationInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.ConfirmationWindow);
        foreach (AlertComparison comparison in new[] { AlertComparison.GreaterThan, AlertComparison.GreaterThanOrEqual })
        foreach (int seconds in new[] { 5, 60 })
        {
            var rule = new AlertRuleDefinition(Guid.NewGuid(), "blocking.custom", entry.Kind, null,
                comparison, 65.001, 5.001, 3, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(seconds), clearConfirmationCount: 4);
            Assert.True(AlertCatalog.IsApproved(rule));
        }
    }
}
