using SqlObserver.Domain.Analytics;

namespace SqlObserver.Analytics;

/// <summary>One completed UTC bucket. Invalid inputs count toward missing coverage.</summary>
public static class SqlMetricRollupV2
{
    public static SqlMetricRollupResult Compute(IEnumerable<SqlMetricPoint> source, string metricKey,
        RollupInterval interval, DateTimeOffset bucketStartUtc, DateTimeOffset sourceCutoffUtc,
        double sourceCadenceSeconds, long generation = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (MetricCatalogV2.Get(metricKey).Source != AnalyticsMetricSource.SqlEngine)
            throw new ArgumentException("A catalog-v2 SQL metric is required.", nameof(metricKey));
        if (bucketStartUtc != RollupV1.Floor(bucketStartUtc, interval) || sourceCutoffUtc.Offset != TimeSpan.Zero ||
            !double.IsFinite(sourceCadenceSeconds) || sourceCadenceSeconds is < 1 or > 86400 || generation < 1)
            throw new ArgumentException("SQL rollup window or cadence is invalid.");
        TimeSpan width = interval switch
        { RollupInterval.FiveMinutes => TimeSpan.FromMinutes(5), RollupInterval.Hour => TimeSpan.FromHours(1), RollupInterval.Day => TimeSpan.FromDays(1), _ => throw new ArgumentOutOfRangeException(nameof(interval)) };
        DateTimeOffset end = bucketStartUtc.Add(width);
        if (sourceCutoffUtc < end) throw new ArgumentException("A completed source window is required.", nameof(sourceCutoffUtc));
        SqlMetricPoint[] points = source.OrderBy(point => point.ObservedAtUtc).ThenBy(point => point.SourceRunId).ToArray();
        if (points.Length > AnalyticsJobBounds.MaximumRows || points.Any(point => point.MetricKey != metricKey || point.ObservedAtUtc < bucketStartUtc || point.ObservedAtUtc >= end) ||
            points.Select(point => point.SourceRunId).Distinct().Count() != points.Length)
            throw new ArgumentException("SQL rollup inputs are outside their single-metric run/window contract.", nameof(source));
        SqlMetricPoint[] valid = points.Where(point => point.Complete).ToArray();
        int expected = Math.Max(1, checked((int)Math.Ceiling(width.TotalSeconds / sourceCadenceSeconds)));
        double weight = valid.Sum(point => point.AggregationWeight!.Value);
        double? weightedValue = valid.Length == 0 ? null : valid.Sum(point => point.Value!.Value * point.AggregationWeight!.Value);
        var statistics = new RollupResult
        {
            Interval = interval, BucketStartUtc = bucketStartUtc, BucketEndUtc = end,
            MetricKey = metricKey, DimensionsSha256 = CanonicalDimensions.Sha256(null),
            Count = valid.Length, Expected = expected, Mean = weightedValue / weight,
            Min = valid.Length == 0 ? null : valid.Min(point => point.Value!.Value),
            Max = valid.Length == 0 ? null : valid.Max(point => point.Value!.Value),
            Sum = valid.Length == 0 ? null : valid.Sum(point => point.Value!.Value),
            Last = valid.LastOrDefault()?.Value, Generation = generation, SourceCutoffUtc = sourceCutoffUtc,
            GapCount = Math.Max(points.Length - valid.Length, expected - valid.Length),
            VisibilityState = valid.Length == 0 ? "unavailable" : valid.Length < expected || valid.Length < points.Length ? "partial" : "complete",
        };
        var result = new SqlMetricRollupResult(statistics, weight, weightedValue);
        result.Validate(); return result;
    }
}
