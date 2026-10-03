using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.Server;

public sealed class AlertRuleTemplateCatalog : IAlertRuleTemplateApprovalPort
{
    public SqlObserver.Application.Ports.AlertRuleTemplateCatalog ReadCatalog() => new(AlertCatalog.Digest,
        AlertCatalog.Entries.Select(static entry => new AlertRuleTemplateCatalogEntry(entry.Name, AlertRuleKindWire.Format(entry.Kind),
            entry.Metric, entry.Unit, Comparison(entry.Comparison), entry.Threshold, entry.Hysteresis, entry.ConfirmationCount,
            (int)entry.ConfirmationWindow.TotalSeconds, (int)entry.EvaluationInterval.TotalSeconds, entry.ClearConfirmationCount)
            { SourceCollector = entry.SourceCollector, SourceSchemaVersion = entry.SourceSchemaVersion }).ToArray());
    public AlertRuleTemplateApproval? Resolve(string sourceTemplateName, string requestedCatalogDigest, AlertRuleTemplateValues values)
    {
        AlertCatalogEntry? entry = AlertCatalog.Entries.SingleOrDefault(item => item.Name == sourceTemplateName);
        if (entry is null || requestedCatalogDigest != AlertCatalog.Digest ||
            AlertRuleKindWire.Format(entry.Kind) != values.Kind || entry.Metric != values.MetricId || !AlertCatalog.IsApproved(values.ToRule(Guid.NewGuid()))) return null;
        return new(entry.Name, AlertCatalog.Digest, entry.SourceCollector, entry.SourceSchemaVersion, entry.Unit);
    }
    private static string Comparison(AlertComparison value) => value switch
    {
        AlertComparison.GreaterThan => "greater_than", AlertComparison.GreaterThanOrEqual => "greater_than_or_equal",
        AlertComparison.LessThan => "less_than", AlertComparison.LessThanOrEqual => "less_than_or_equal",
        AlertComparison.Equal => "equal", _ => throw new InvalidOperationException("Unsupported comparison in product catalog."),
    };
}
