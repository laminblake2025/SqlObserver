using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;

namespace SqlObserver.Domain.Analytics;

public enum AnalyticsMetricSource { Host = 1, Replication = 2, SqlEngine = 3 }
public enum AnalyticsMetricDerivation { DirectGauge = 1, BatchRate = 2, SchedulerCpuPercent = 3 }

/// <summary>Versioned input provenance for a privacy-neutral metric projection.</summary>
public sealed record AnalyticsMetricDefinition
{
    internal AnalyticsMetricDefinition(MetricCatalogEntry metric, AnalyticsMetricSource source,
        AnalyticsMetricDerivation derivation, params string[] sourceMetricKeys)
    {
        Metric = metric;
        Source = source;
        Derivation = derivation;
        SourceMetricKeys = Array.AsReadOnly((string[])sourceMetricKeys.Clone());
    }

    public MetricCatalogEntry Metric { get; }
    public AnalyticsMetricSource Source { get; }
    public AnalyticsMetricDerivation Derivation { get; }
    public IReadOnlyList<string> SourceMetricKeys { get; }
    public bool RequiresComparableEngineSamples => Derivation != AnalyticsMetricDerivation.DirectGauge;
    public bool RequiresSameEngineEpoch => RequiresComparableEngineSamples;
    public TimeSpan? MaximumSampleGap => RequiresComparableEngineSamples ? TimeSpan.FromMinutes(2) : null;
    public bool RequiresStableSchedulerCount => Derivation == AnalyticsMetricDerivation.SchedulerCpuPercent;
    public string DerivationVersion => Derivation switch
    {
        AnalyticsMetricDerivation.DirectGauge => "direct-gauge-v1",
        AnalyticsMetricDerivation.BatchRate => "core-batch-rate-v1",
        AnalyticsMetricDerivation.SchedulerCpuPercent => "core-scheduler-cpu-v1",
        _ => throw new InvalidOperationException("Unknown metric derivation."),
    };
}

/// <summary>
/// SQL-capable analytics input contract. Catalog-v1 remains immutable for existing
/// jobs and cursors. This definition is not activated until its repository and
/// worker adapters support version 2 together.
/// </summary>
public static class MetricCatalogV2
{
    public const int Version = 2;
    public const string ChecksumAlgorithm = "SHA-256";
    private static readonly ReadOnlyCollection<AnalyticsMetricDefinition> Definitions = Build();
    public static IReadOnlyList<AnalyticsMetricDefinition> All => Definitions;
    public static string Checksum { get; } = ComputeChecksum(Definitions);

    public static bool TryGet(string key, out AnalyticsMetricDefinition? definition)
    {
        definition = Definitions.SingleOrDefault(item => item.Metric.Key == key);
        return definition is not null;
    }

    public static AnalyticsMetricDefinition Get(string key) => TryGet(key, out AnalyticsMetricDefinition? definition)
        ? definition! : throw new KeyNotFoundException("Metric is not in catalog-v2.");

    private static ReadOnlyCollection<AnalyticsMetricDefinition> Build()
    {
        var definitions = MetricCatalogV1.All.Select(metric => new AnalyticsMetricDefinition(metric,
            metric.Key.StartsWith("replication.", StringComparison.Ordinal) ? AnalyticsMetricSource.Replication : AnalyticsMetricSource.Host,
            AnalyticsMetricDerivation.DirectGauge, metric.Key)).ToList();
        foreach ((string key, string label, string unit) in new[]
        {
            ("engine.user_connections", "SQL user connections", "count"),
            ("engine.process_physical_memory_bytes", "SQL process physical memory", "bytes"),
            ("engine.os_available_memory_bytes", "OS available memory via SQL", "bytes"),
            ("engine.memory_grants_pending", "SQL memory grants pending", "count"),
            ("engine.scheduler_runnable_tasks", "Runnable SQL tasks", "count"),
        })
        {
            definitions.Add(new AnalyticsMetricDefinition(new MetricCatalogEntry(key, label, unit, MetricKind.Gauge),
                AnalyticsMetricSource.SqlEngine, AnalyticsMetricDerivation.DirectGauge, key));
        }
        definitions.Add(new AnalyticsMetricDefinition(
            new MetricCatalogEntry("engine.batch_requests_per_second", "SQL batch requests per second", "batches_per_second", MetricKind.Gauge),
            AnalyticsMetricSource.SqlEngine, AnalyticsMetricDerivation.BatchRate,
            "engine.batch_requests_total", "engine.start_time_key"));
        definitions.Add(new AnalyticsMetricDefinition(
            new MetricCatalogEntry("engine.sql_scheduler_cpu_percent", "SQL scheduler CPU utilization", "percent", MetricKind.Gauge),
            AnalyticsMetricSource.SqlEngine, AnalyticsMetricDerivation.SchedulerCpuPercent,
            "engine.scheduler_cpu_milliseconds_total", "engine.visible_scheduler_count", "engine.start_time_key"));
        return Array.AsReadOnly(definitions.OrderBy(item => item.Metric.Key, StringComparer.Ordinal).ToArray());
    }

    private static string ComputeChecksum(IEnumerable<AnalyticsMetricDefinition> definitions)
    {
        // Repository version-2 seed generation must use this exact UTF-8, LF
        // representation. It covers the declared inputs and derivation version
        // along with labels, units and dimensions. Algorithm changes require a
        // new derivation version even when the output key remains the same.
        string canonical = string.Join("\n", definitions.OrderBy(item => item.Metric.Key, StringComparer.Ordinal).Select(item =>
            $"{item.Metric.Key}|{item.Metric.DisplayName}|{item.Metric.Unit}|{SourceToken(item.Source)}|gauge|{item.Metric.Enabled.ToString().ToLowerInvariant()}|{string.Join(",", item.Metric.DimensionAllowlist.OrderBy(key => key, StringComparer.Ordinal))}|{item.DerivationVersion}|{string.Join(",", item.SourceMetricKeys.OrderBy(key => key, StringComparer.Ordinal))}|{(item.RequiresComparableEngineSamples ? "120" : "none")}|{(item.RequiresStableSchedulerCount ? "stable" : "none")}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string SourceToken(AnalyticsMetricSource source) => source switch
    {
        AnalyticsMetricSource.Host => "host",
        AnalyticsMetricSource.Replication => "replication",
        AnalyticsMetricSource.SqlEngine => "sql_engine",
        _ => throw new InvalidOperationException("Unknown metric source."),
    };
}
