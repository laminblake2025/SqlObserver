using SqlObserver.Analytics;
using SqlObserver.Domain.Analytics;

namespace SqlObserver.UnitTests;

public sealed class ForecastHistoryRegressionTests
{
    private const string Metric = "host.volume.free_bytes";
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, string> Dimensions =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["volume"] = "opaque-history-volume" };

    [Fact]
    public void RawGaugeRetainsHistoryAfterLargeLateDrop()
    {
        MetricPoint[] points = GaugeHistoryWithLateDrop();

        ForecastResult result = ForecastV1.Compute(Metric, points, Start.AddDays(30), TimeSpan.FromDays(7), 2_000);

        AssertUsefulGaugeForecast(result);
    }

    [Fact]
    public void DailyGaugeRollupsRetainHistoryAfterLargeLateDrop()
    {
        string dimensionsHash = CanonicalDimensions.Sha256(Dimensions);
        RollupResult[] rows = GaugeHistoryWithLateDrop().Select(point => new RollupResult
        {
            Interval = RollupInterval.Day,
            BucketStartUtc = point.ObservedAtUtc,
            BucketEndUtc = point.ObservedAtUtc.AddDays(1),
            MetricKey = Metric,
            Dimensions = Dimensions,
            DimensionsSha256 = dimensionsHash,
            Count = 24,
            Expected = 24,
            Mean = point.Value,
            Min = point.Value,
            Max = point.Value,
            Sum = point.Value * 24,
            Last = point.Value,
            SourceCutoffUtc = Start.AddDays(30),
        }).ToArray();

        ForecastResult result = ForecastV1.Compute(Metric, rows, Start.AddDays(30), TimeSpan.FromDays(7), 2_000, dimensionsHash);

        AssertUsefulGaugeForecast(result);
    }

    [Theory]
    [InlineData(MetricKind.Gauge)]
    [InlineData(MetricKind.Counter)]
    public void ExplicitLateResetStillRequiresEnoughNewHistory(MetricKind kind)
    {
        // There is no magnitude drop: only the explicit reset marks the boundary.
        MetricPoint[] points = Enumerable.Range(0, 30)
            .Select(day => new MetricPoint(Start.AddDays(day), Metric, 1_000 + 10 * day, kind, Dimensions, reset: day == 28))
            .ToArray();

        ForecastResult result = ForecastV1.Compute(Metric, points, Start.AddDays(30), TimeSpan.FromDays(7), 2_000);

        Assert.False(result.Available);
        Assert.Null(result.Estimate);
        Assert.Null(result.SlopePerDay);
        Assert.Equal("forecast-v1", result.Model);
    }

    [Fact]
    public void CounterMagnitudeDropUsesOnlyTheNewCounterSegment()
    {
        MetricPoint[] points = Enumerable.Range(0, 30)
            .Select(day => new MetricPoint(Start.AddDays(day), Metric,
                day < 22 ? 1_000 + 10 * day : 100 + 10 * (day - 22), MetricKind.Counter, Dimensions))
            .ToArray();

        // Eight post-reset days satisfy the history bound. A zero threshold here
        // isolates segmentation from the separate confidence policy for short history.
        ForecastResult result = ForecastV1.Compute(Metric, points, Start.AddDays(30), TimeSpan.FromDays(7), 2_000, minimumConfidence: 0);

        Assert.True(result.Available);
        Assert.Equal(10d, result.SlopePerDay!.Value, 6);
        Assert.Equal(250d, result.Estimate!.Value, 6);
        Assert.Equal(8d / 30d, result.Confidence, 6);
        Assert.Equal("forecast-v1", result.Model);
    }

    [Fact]
    public void LateCounterMagnitudeDropStillRefusesInsufficientHistory()
    {
        MetricPoint[] points = Enumerable.Range(0, 30)
            .Select(day => new MetricPoint(Start.AddDays(day), Metric,
                day < 28 ? 1_000 + 10 * day : 100 + 10 * (day - 28), MetricKind.Counter, Dimensions))
            .ToArray();

        ForecastResult result = ForecastV1.Compute(Metric, points, Start.AddDays(30), TimeSpan.FromDays(7), 2_000, minimumConfidence: 0);

        Assert.False(result.Available);
    }

    private static MetricPoint[] GaugeHistoryWithLateDrop() => Enumerable.Range(0, 30)
        // The first 28 days follow a -20/day trend. Two late observations reflect
        // substantial real consumption, not a counter reset. Only two retained
        // days would be insufficient for a forecast.
        .Select(day => new MetricPoint(Start.AddDays(day), Metric,
            day < 28 ? 1_000 - 20 * day : 200 - 20 * (day - 28), MetricKind.Gauge, Dimensions))
        .ToArray();

    private static void AssertUsefulGaugeForecast(ForecastResult result)
    {
        Assert.True(result.Available);
        // The robust trend remains -20/day and extrapolates to day 37.
        Assert.Equal(-20d, result.SlopePerDay!.Value, 6);
        Assert.Equal(260d, result.Estimate!.Value, 6);
        Assert.InRange(result.Confidence, .5, 1);
        Assert.True(double.IsFinite(result.Residual));
        Assert.InRange(result.LowerBound!.Value, 0, result.Estimate.Value);
        Assert.InRange(result.UpperBound!.Value, result.Estimate.Value, 2_000);
        Assert.Equal(Start.AddDays(30), result.HorizonStartUtc);
        Assert.Equal(Start.AddDays(37), result.HorizonEndUtc);
        Assert.Equal(CanonicalDimensions.Sha256(Dimensions), result.DimensionsSha256);
        Assert.Equal("opaque-history-volume", result.Dimensions["volume"]);
        Assert.Equal("forecast-v1", result.Model);
        Assert.Equal("forecast-v1", ForecastResult.AlgorithmVersion);
    }
}
