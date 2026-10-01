using SqlObserver.Alerting;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.UnitTests;

public sealed class DeadlockAlertCatalogTests
{
    [Fact]
    public void DeadlockCatalogBindsCountsToThePassiveCollectorAndAllowsCustomTiming()
    {
        AlertCatalogEntry entry = Assert.Single(AlertCatalog.Entries, e => e.Kind == AlertRuleKind.DeadlockCount);
        Assert.Equal("deadlocks.count", entry.Name);
        Assert.Equal("deadlocks.system-health", entry.SourceCollector); Assert.Equal(1, entry.SourceSchemaVersion);
        Assert.Null(entry.Metric); Assert.Equal("count", entry.Unit);
        Assert.Equal(AlertComparison.GreaterThan, entry.Comparison);
        Assert.Equal(0, entry.Threshold); Assert.Equal(0, entry.Hysteresis);
        Assert.Equal(2, entry.ConfirmationCount); Assert.Equal(2, entry.ClearConfirmationCount);
        Assert.Equal(TimeSpan.FromSeconds(15), entry.EvaluationInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.ConfirmationWindow);
        foreach (AlertComparison comparison in new[] { AlertComparison.GreaterThan, AlertComparison.GreaterThanOrEqual })
            foreach (int seconds in new[] { 15, 60 })
            {
                var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.custom", entry.Kind, null,
                    comparison, 3, 1, 3, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(seconds), clearConfirmationCount: 4);
                Assert.True(AlertCatalog.IsApproved(rule));
            }
    }
}
