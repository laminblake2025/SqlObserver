using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.Alerting;

/// <summary>Product-owned, deterministic rule catalog. Reconciliation compares this digest before enabling a rule.</summary>
public sealed record AlertCatalogEntry(string Name, AlertRuleKind Kind, string? Metric, string SourceCollector, int SourceSchemaVersion, string Unit, AlertComparison Comparison, double Threshold, double Hysteresis, int ConfirmationCount, TimeSpan ConfirmationWindow, TimeSpan EvaluationInterval, int ClearConfirmationCount);

public sealed class AlertCatalog
{
    public static readonly IReadOnlyList<AlertCatalogEntry> Entries = LoadEmbeddedEntries();
    public static string Digest { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Entries.Select(static x => $"{x.Name}|{x.Kind}|{x.Metric}|{x.SourceCollector}|{x.SourceSchemaVersion}|{x.Unit}|{x.Comparison}|{x.Threshold:R}|{x.Hysteresis:R}|{x.ConfirmationCount}|{x.ConfirmationWindow}|{x.EvaluationInterval}|{x.ClearConfirmationCount}"))))).ToLowerInvariant();
    public static bool IsApproved(AlertRuleDefinition rule) => Entries.Any(entry =>
        rule.ClearConfirmationCount is >= 1 and <= 100 && entry.Kind == rule.Kind && entry.Metric == rule.MetricId?.Value &&
        entry.SourceCollector.Length > 0 && entry.SourceSchemaVersion > 0 && entry.Unit.Length > 0 &&
        (rule.Kind == AlertRuleKind.MetricThreshold
            ? rule.Comparison is AlertComparison.GreaterThan or AlertComparison.GreaterThanOrEqual &&
              rule.Threshold is >= 0 and <= 1_000_000 && rule.Hysteresis <= rule.Threshold &&
              rule.EvaluationInterval >= TimeSpan.FromSeconds(15)
            : entry.Name == rule.Name && entry.Comparison == rule.Comparison && entry.Threshold == rule.Threshold &&
              entry.Hysteresis == rule.Hysteresis && entry.ConfirmationCount == rule.ConfirmationCount &&
              entry.ConfirmationWindow == rule.ConfirmationWindow && entry.EvaluationInterval == rule.EvaluationInterval));

    private static AlertCatalogEntry[] LoadEmbeddedEntries()
    {
        using Stream stream = typeof(AlertCatalog).Assembly.GetManifestResourceStream("SqlObserver.Alerting.Assets.alert-catalog.json") ?? throw new InvalidOperationException("The embedded alert catalog is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 2 || document.RootElement.GetProperty("contractVersion").GetInt32() != 2) throw new InvalidOperationException("Unsupported alert catalog schema.");
        return document.RootElement.GetProperty("rules").EnumerateArray().Select(static item =>
        {
            string kind = item.GetProperty("kind").GetString()!;
            AlertRuleKind ruleKind = kind == "metric_threshold" ? AlertRuleKind.MetricThreshold : kind == "collector_health" ? AlertRuleKind.CollectorHealth : throw new InvalidOperationException("Alert catalog rule kind is not approved.");
            string comparisonName = item.GetProperty("comparison").GetString()!;
            AlertComparison comparison = comparisonName switch { "greater_than" => AlertComparison.GreaterThan, "greater_than_or_equal" => AlertComparison.GreaterThanOrEqual, "less_than" => AlertComparison.LessThan, "less_than_or_equal" => AlertComparison.LessThanOrEqual, "equal" => AlertComparison.Equal, _ => throw new InvalidOperationException("Alert catalog comparison is not approved.") };
            double threshold = item.GetProperty("threshold").GetDouble(); double hysteresis = item.GetProperty("hysteresis").GetDouble();
            int confirmations = item.GetProperty("confirmationCount").GetInt32();
            int clearConfirmations = item.GetProperty("clearConfirmationCount").GetInt32();
            TimeSpan confirmationWindow = TimeSpan.FromSeconds(item.GetProperty("confirmationWindowSeconds").GetInt32());
            TimeSpan interval = TimeSpan.FromSeconds(item.GetProperty("evaluationIntervalSeconds").GetInt32());
            bool enabled = item.GetProperty("enabled").GetBoolean();
            string? metric = item.GetProperty("metric").ValueKind == JsonValueKind.Null ? null : item.GetProperty("metric").GetString();
            string sourceCollector = item.GetProperty("sourceCollector").GetString()!;
            int sourceSchemaVersion = item.GetProperty("sourceSchemaVersion").GetInt32();
            string unit = item.GetProperty("unit").GetString()!;
            if ((ruleKind == AlertRuleKind.MetricThreshold) != (metric is not null) || !sourceCollector.StartsWith("engine.", StringComparison.Ordinal) || sourceSchemaVersion < 1 || string.IsNullOrWhiteSpace(unit) || threshold is double.NaN or double.PositiveInfinity or double.NegativeInfinity || hysteresis < 0 || confirmations is < 1 or > 100 || clearConfirmations is < 1 or > 100 || interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromHours(1)) throw new InvalidOperationException("Alert catalog rule bounds are invalid.");
            if (!enabled) throw new InvalidOperationException("Disabled catalog rules are not approved signals.");
            return new AlertCatalogEntry(item.GetProperty("name").GetString()!, ruleKind, metric, sourceCollector, sourceSchemaVersion, unit, comparison, threshold, hysteresis, confirmations, confirmationWindow, interval, clearConfirmations);
        }).ToArray();
    }
}
