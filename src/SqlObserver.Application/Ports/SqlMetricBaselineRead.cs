using SqlObserver.Domain.Analytics;

namespace SqlObserver.Application.Ports;

/// <summary>One exact source window; all hourly rows are returned together.</summary>
public sealed record SqlMetricBaselinePage(Guid TargetId, long TargetRevision, string MetricKey, string Unit,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset SnapshotUtc, long Generation,
    IReadOnlyList<SqlMetricBaselineResult> Items, string State)
{
    public int CatalogVersion { get; } = MetricCatalogV2.Version;
    public string CatalogChecksum { get; } = MetricCatalogV2.Checksum;
    public string AlgorithmVersion { get; } = "sql-baseline-v2";
    public string SourceAlgorithmVersion { get; } = "sql-rollup-v2";
}
