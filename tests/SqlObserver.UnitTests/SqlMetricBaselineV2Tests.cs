using SqlObserver.Analytics;
using SqlObserver.Domain.Analytics;

namespace SqlObserver.UnitTests;

public sealed class SqlMetricBaselineV2Tests
{
    private static readonly DateTimeOffset End = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private const string Metric = "engine.batch_requests_per_second";

    [Fact]
    public void CompleteWeightedHoursProduceAll168HoursWithStableVersionIdentity()
    {
        SqlMetricRollupResult[] input = Enumerable.Range(0, 672).Select(hour => Hour(End.AddDays(-28).AddHours(hour))).ToArray();
        IReadOnlyList<SqlMetricBaselineResult> result = SqlMetricBaselineV2.Compute(input, Metric, End, End, 3);
        Assert.Equal(Enumerable.Range(0, 168), result.Select(row => row.Statistics.HourOfWeek));
        Assert.All(result, row =>
        {
            Assert.Equal(32.5, row.Statistics.Median); Assert.Equal(0, row.Statistics.Mad);
            Assert.Equal(4, row.Statistics.SampleCount); Assert.Equal(28, row.Statistics.CompleteDays);
            Assert.Equal(1, row.Statistics.Coverage); Assert.Equal(.5, row.Statistics.Confidence);
            Assert.Equal("complete", row.Statistics.VisibilityState); Assert.Equal(3, row.Statistics.Generation);
            Assert.Equal(2, row.CatalogVersion); Assert.Equal(MetricCatalogV2.Checksum, row.CatalogChecksum); row.Validate();
        });
    }

    [Fact]
    public void IncompleteDayAndMissingHoursRemainUnavailableInsteadOfZeros()
    {
        DateTimeOffset day = End.AddDays(-1);
        SqlMetricRollupResult[] partial = Enumerable.Range(0, 23).Select(hour => Hour(day.AddHours(hour))).ToArray();
        IReadOnlyList<SqlMetricBaselineResult> none = SqlMetricBaselineV2.Compute(partial, Metric, End, End, 3);
        Assert.Equal(168, none.Count);
        Assert.All(none, row => { Assert.Null(row.Statistics.Median); Assert.Equal(0, row.Statistics.Coverage); Assert.Equal("unavailable", row.Statistics.VisibilityState); });
        SqlMetricRollupResult[] oneDay = [.. partial, Hour(day.AddHours(23))];
        IReadOnlyList<SqlMetricBaselineResult> result = SqlMetricBaselineV2.Compute(oneDay, Metric, End, End, 3);
        Assert.Equal(24, result.Count(row => row.Statistics.SampleCount == 1));
        Assert.Equal(144, result.Count(row => row.Statistics.SampleCount == 0));
        Assert.All(result, row => Assert.Equal(1 / 28d, row.Statistics.Coverage));
        Assert.All(result.Where(row => row.Statistics.SampleCount == 1), row => Assert.Equal("partial", row.Statistics.VisibilityState));
    }

    [Fact]
    public void MixedIdentityDuplicateFutureAndNonHourlyInputAreRejected()
    {
        SqlMetricRollupResult row = Hour(End.AddHours(-1));
        Assert.Throws<ArgumentException>(() => SqlMetricBaselineV2.Compute([row, row], Metric, End, End, 3));
        Assert.Throws<ArgumentException>(() => SqlMetricBaselineV2.Compute([row], Metric, End, End, 1));
        Assert.Throws<ArgumentException>(() => SqlMetricBaselineV2.Compute([row], "engine.user_connections", End, End, 3));
        Assert.Throws<ArgumentException>(() => SqlMetricBaselineV2.Compute([Hour(End)], Metric, End, End, 3));
        Assert.Throws<ArgumentException>(() => SqlMetricBaselineV2.Compute([row with { Statistics = row.Statistics with { SourceCutoffUtc = End.AddMinutes(1) } }], Metric, End, End, 3));
        Assert.Throws<ArgumentException>(() => SqlMetricBaselineV2.Compute([row with { Statistics = row.Statistics with { Interval = RollupInterval.FiveMinutes } }], Metric, End, End, 3));
        Assert.Throws<ArgumentException>(() => SqlMetricBaselineV2.Compute([], Metric, End.AddHours(1), End.AddHours(1), 3));
    }

    private static SqlMetricRollupResult Hour(DateTimeOffset at)
        => SqlMetricRollupV2.Compute([new(Guid.NewGuid(), at, Metric, 100, 30, true), new(Guid.NewGuid(), at.AddSeconds(90), Metric, 10, 90, true)],
            Metric, RollupInterval.Hour, at, at.AddHours(1), 1800, 3);
}
