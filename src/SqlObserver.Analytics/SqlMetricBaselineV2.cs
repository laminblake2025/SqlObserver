using SqlObserver.Domain.Analytics;

namespace SqlObserver.Analytics;

public static class SqlMetricBaselineV2
{
    public static IReadOnlyList<SqlMetricBaselineResult> Compute(IEnumerable<SqlMetricRollupResult> source,
        string metricKey, DateTimeOffset windowEndUtc, DateTimeOffset sourceCutoffUtc, long generation = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (MetricCatalogV2.Get(metricKey).Source != AnalyticsMetricSource.SqlEngine || windowEndUtc.Offset != TimeSpan.Zero ||
            windowEndUtc.TimeOfDay != TimeSpan.Zero || sourceCutoffUtc.Offset != TimeSpan.Zero || sourceCutoffUtc < windowEndUtc || generation < 1)
            throw new ArgumentException("A complete UTC SQL baseline window is required.");
        DateTimeOffset start = windowEndUtc.AddDays(-28);
        SqlMetricRollupResult[] inputs = source.ToArray();
        if (inputs.Length > 672) throw new ArgumentException("SQL baseline input exceeds 28 hourly days.", nameof(source));
        foreach (SqlMetricRollupResult input in inputs)
        {
            input.Validate(); RollupResult row = input.Statistics;
            if (row.MetricKey != metricKey || row.Interval != RollupInterval.Hour || row.BucketStartUtc < start ||
                row.BucketEndUtc > windowEndUtc || row.BucketStartUtc != RollupV1.Floor(row.BucketStartUtc, RollupInterval.Hour) ||
                row.BucketEndUtc - row.BucketStartUtc != TimeSpan.FromHours(1) || row.Generation != generation || row.SourceCutoffUtc > sourceCutoffUtc)
                throw new ArgumentException("SQL baseline inputs mix identity, generation or snapshot windows.", nameof(source));
        }
        if (inputs.Select(input => input.Statistics.BucketStartUtc).Distinct().Count() != inputs.Length)
            throw new ArgumentException("Duplicate SQL baseline hours are not allowed.", nameof(source));
        // The statistical method is unchanged: equal hourly means from complete
        // UTC days with at least 80% sample coverage. Typed input validation above
        // prevents the v1 compatibility filter from accepting another interval.
        Dictionary<int, BaselineResult> observed = BaselineV1.Compute(inputs.Select(input => input.Statistics), windowEndUtc)
            .ToDictionary(row => row.HourOfWeek);
        int completeDays = observed.Values.FirstOrDefault()?.CompleteDays ?? 0;
        var results = new List<SqlMetricBaselineResult>(168);
        for (int hour = 0; hour < 168; hour++)
        {
            BaselineResult row = observed.TryGetValue(hour, out BaselineResult? value) ? value with
            { Generation = generation, VisibilityState = completeDays == 28 ? "complete" : "partial" } : new BaselineResult
            {
                MetricKey = metricKey, HourOfWeek = hour, CompleteDays = completeDays, SampleCount = 0,
                WindowStartUtc = start, WindowEndUtc = windowEndUtc, Coverage = completeDays / 28d,
                Confidence = 0, Generation = generation, VisibilityState = "unavailable",
            };
            var result = new SqlMetricBaselineResult(row, sourceCutoffUtc); result.Validate(); results.Add(result);
        }
        return results.AsReadOnly();
    }
}
