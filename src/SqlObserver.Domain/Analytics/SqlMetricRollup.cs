namespace SqlObserver.Domain.Analytics;

/// <summary>One scoped repository projection; missing values remain observations.</summary>
public sealed record SqlMetricPoint
{
    public SqlMetricPoint(Guid sourceRunId, DateTimeOffset observedAtUtc, string metricKey,
        double? value, double? aggregationWeight, bool complete)
    {
        AnalyticsMetricDefinition definition = MetricCatalogV2.Get(metricKey);
        if (definition.Source != AnalyticsMetricSource.SqlEngine || sourceRunId == Guid.Empty || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("A SQL source run, metric and UTC timestamp are required.");
        if (value is not null && (!double.IsFinite(value.Value) || value < 0) ||
            aggregationWeight is not null && (!double.IsFinite(aggregationWeight.Value) || aggregationWeight < 0) ||
            complete != (value is not null && aggregationWeight > 0) || value is not null && !complete ||
            definition.Derivation == AnalyticsMetricDerivation.DirectGauge && aggregationWeight != 1 ||
            definition.Derivation != AnalyticsMetricDerivation.DirectGauge && complete && aggregationWeight > 120 ||
            definition.Derivation == AnalyticsMetricDerivation.SchedulerCpuPercent && value > 100)
            throw new ArgumentException("SQL input validity and aggregation weight disagree.");
        SourceRunId = sourceRunId; ObservedAtUtc = observedAtUtc; MetricKey = metricKey;
        Value = value; AggregationWeight = aggregationWeight; Complete = complete;
    }

    public Guid SourceRunId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string MetricKey { get; }
    public double? Value { get; }
    public double? AggregationWeight { get; }
    public bool Complete { get; }
}

/// <summary>Catalog and algorithm identity travel with the weighted result.</summary>
public sealed record SqlMetricRollupResult(RollupResult Statistics, double WeightSum, double? WeightedValueSum)
{
    public int CatalogVersion { get; } = MetricCatalogV2.Version;
    public string CatalogChecksum { get; } = MetricCatalogV2.Checksum;
    public string AlgorithmVersion { get; } = "sql-rollup-v2";

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Statistics); Statistics.Validate();
        if (MetricCatalogV2.Get(Statistics.MetricKey).Source != AnalyticsMetricSource.SqlEngine || Statistics.Dimensions.Count != 0 ||
            Statistics.Expected < 1 || Statistics.SourceCutoffUtc is null || Statistics.SourceCutoffUtc < Statistics.BucketEndUtc ||
            Statistics.ResetCount != 0 || Statistics.CounterDelta is not null || Statistics.RatePerSecond is not null || Statistics.Truncated ||
            !double.IsFinite(WeightSum) || WeightSum < 0 ||
            WeightedValueSum is not null && !double.IsFinite(WeightedValueSum.Value) ||
            Statistics.Count == 0 && (WeightSum != 0 || WeightedValueSum is not null || Statistics.Mean is not null) ||
            Statistics.Count > 0 && (WeightSum <= 0 || WeightedValueSum is null || Statistics.Mean is null || Statistics.Mean != WeightedValueSum / WeightSum))
            throw new ArgumentException("Weighted SQL rollup metadata is invalid.");
    }
}
