namespace SqlObserver.Domain.Analytics;

/// <summary>One hour-of-week baseline retains its catalog and source identity.</summary>
public sealed record SqlMetricBaselineResult(BaselineResult Statistics, DateTimeOffset SourceCutoffUtc)
{
    public int CatalogVersion { get; } = MetricCatalogV2.Version;
    public string CatalogChecksum { get; } = MetricCatalogV2.Checksum;
    public string AlgorithmVersion { get; } = "sql-baseline-v2";
    public string SourceAlgorithmVersion { get; } = "sql-rollup-v2";

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Statistics);
        BaselineResult row = Statistics;
        if (MetricCatalogV2.Get(row.MetricKey).Source != AnalyticsMetricSource.SqlEngine || row.Dimensions.Count != 0 ||
            row.DimensionsSha256 != CanonicalDimensions.Sha256(null) || row.Generation < 1 ||
            row.WindowStartUtc is not { } from || row.WindowEndUtc is not { } to || from.Offset != TimeSpan.Zero ||
            to.Offset != TimeSpan.Zero || to.TimeOfDay != TimeSpan.Zero || to - from != TimeSpan.FromDays(28) ||
            SourceCutoffUtc.Offset != TimeSpan.Zero || SourceCutoffUtc < to ||
            row.HourOfWeek is < 0 or > 167 || row.CompleteDays is < 0 or > 28 || row.SampleCount is < 0 or > 4 ||
            !double.IsFinite(row.Coverage) || !double.IsFinite(row.Confidence) || row.Coverage != row.CompleteDays / 28d ||
            row.Confidence != row.Coverage * Math.Min(1, row.SampleCount / 8d) ||
            row.Mean is not null || row.Stddev is not null || row.LowerBound is not null || row.UpperBound is not null ||
            row.VisibilityState != (row.SampleCount == 0 ? "unavailable" : row.CompleteDays == 28 ? "complete" : "partial"))
            throw new ArgumentException("SQL baseline identity or coverage is invalid.");
        double?[] summaries = [row.Median, row.Mad, row.P10, row.P90];
        if (summaries.Any(value => value is not null && (!double.IsFinite(value.Value) || value < 0)) ||
            row.SampleCount == 0 && summaries.Any(value => value is not null) ||
            row.SampleCount > 0 && summaries.Any(value => value is null) || row.P10 > row.Median || row.Median > row.P90)
            throw new ArgumentException("SQL baseline statistics are invalid.");
    }
}
