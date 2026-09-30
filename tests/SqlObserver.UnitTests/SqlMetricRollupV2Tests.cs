using SqlObserver.Analytics;
using SqlObserver.Domain.Analytics;

namespace SqlObserver.UnitTests;

public sealed class SqlMetricRollupV2Tests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private const string Batch = "engine.batch_requests_per_second";

    [Fact]
    public void RatesUseElapsedWeightsAndRetainCatalogAndGeneration()
    {
        SqlMetricRollupResult result = Compute([Point(30, 100, 30), Point(120, 10, 90)], cadence: 150, generation: 8);
        Assert.Equal(32.5, result.Statistics.Mean); Assert.Equal(120, result.WeightSum);
        Assert.Equal(3900, result.WeightedValueSum); Assert.Equal(2, result.Statistics.Count);
        Assert.Equal(2, result.Statistics.Expected); Assert.Equal(1, result.Statistics.Coverage);
        Assert.Equal(10, result.Statistics.Min); Assert.Equal(100, result.Statistics.Max);
        Assert.Equal(10, result.Statistics.Last); Assert.Equal(8, result.Statistics.Generation);
        Assert.Equal(2, result.CatalogVersion); Assert.Equal(MetricCatalogV2.Checksum, result.CatalogChecksum);
        Assert.Equal("sql-rollup-v2", result.AlgorithmVersion); result.Validate();
    }

    [Fact]
    public void MissingSamplesAndInvalidDeltasReduceCoverageWithoutAddingZeros()
    {
        SqlMetricRollupResult result = Compute([Point(30, 20, 30), Point(60, null, 30)]);
        Assert.Equal(20, result.Statistics.Mean); Assert.Equal(1, result.Statistics.Count);
        Assert.Equal(10, result.Statistics.Expected); Assert.Equal(.1, result.Statistics.Coverage);
        Assert.Equal(9, result.Statistics.GapCount); Assert.Equal("partial", result.Statistics.VisibilityState);
        SqlMetricRollupResult empty = Compute([]);
        Assert.Null(empty.Statistics.Mean); Assert.Null(empty.WeightedValueSum); Assert.Equal(0, empty.WeightSum);
        Assert.Equal(0, empty.Statistics.Count); Assert.Equal(10, empty.Statistics.Expected);
        Assert.Equal("unavailable", empty.Statistics.VisibilityState);
    }

    [Fact]
    public void DirectGaugesUseSampleWeightsAndZeroRatesRemainRealValues()
    {
        const string memory = "engine.process_physical_memory_bytes";
        SqlMetricPoint[] points = [new(Guid.NewGuid(), Start.AddSeconds(1), memory, 100, 1, true), new(Guid.NewGuid(), Start.AddSeconds(50), memory, 300, 1, true)];
        SqlMetricRollupResult result = SqlMetricRollupV2.Compute(points, memory, RollupInterval.FiveMinutes, Start, Start.AddMinutes(5), 150);
        Assert.Equal(200, result.Statistics.Mean); Assert.Equal(2, result.WeightSum);
        SqlMetricRollupResult zero = Compute([Point(30, 0, 30)]);
        Assert.Equal(0, zero.Statistics.Mean); Assert.Equal(1, zero.Statistics.Count);
    }

    [Fact]
    public void MixedDuplicateAndOutOfWindowInputsAreRejected()
    {
        SqlMetricPoint first = Point(30, 10, 30);
        Assert.Throws<ArgumentException>(() => Compute([first, first]));
        Assert.Throws<ArgumentException>(() => Compute([new(Guid.NewGuid(), Start, "engine.user_connections", 1, 1, true)]));
        Assert.Throws<ArgumentException>(() => Compute([Point(300, 10, 30)]));
        Assert.Throws<ArgumentException>(() => Compute([Point(-1, 10, 30)]));
        Assert.Throws<ArgumentException>(() => Compute([], cadence: 0));
        Assert.Throws<ArgumentException>(() => SqlMetricRollupV2.Compute([], Batch, RollupInterval.FiveMinutes, Start.AddSeconds(1), Start.AddMinutes(6), 30));
        Assert.Throws<ArgumentException>(() => SqlMetricRollupV2.Compute([], Batch, RollupInterval.FiveMinutes, Start, Start.AddMinutes(4), 30));
        Assert.Throws<ArgumentException>(() => SqlMetricRollupV2.Compute([], "host.cpu.percent", RollupInterval.FiveMinutes, Start, Start.AddMinutes(5), 30));
    }

    [Fact]
    public void InputValidityCannotBeRelabelledAndFiniteResultsAreRequired()
    {
        Assert.Throws<ArgumentException>(() => new SqlMetricPoint(Guid.NewGuid(), Start, Batch, null, 30, true));
        Assert.Throws<ArgumentException>(() => new SqlMetricPoint(Guid.NewGuid(), Start, Batch, 10, 0, true));
        Assert.Throws<ArgumentException>(() => new SqlMetricPoint(Guid.NewGuid(), Start, Batch, 10, 30, false));
        Assert.Throws<ArgumentException>(() => new SqlMetricPoint(Guid.NewGuid(), Start, Batch, 10, 121, true));
        Assert.Throws<ArgumentException>(() => new SqlMetricPoint(Guid.NewGuid(), Start, "engine.user_connections", 1, 2, true));
        Assert.Throws<ArgumentException>(() => new SqlMetricPoint(Guid.NewGuid(), Start, "engine.sql_scheduler_cpu_percent", 101, 30, true));
        Assert.Throws<ArgumentException>(() => new SqlMetricPoint(Guid.Empty, Start, Batch, 1, 30, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => Compute([Point(30, double.MaxValue, 120)]));
    }

    private static SqlMetricPoint Point(int seconds, double? value, double? weight) => new(Guid.NewGuid(), Start.AddSeconds(seconds), Batch, value, weight, value is not null);
    private static SqlMetricRollupResult Compute(SqlMetricPoint[] points, double cadence = 30, long generation = 1)
        => SqlMetricRollupV2.Compute(points, Batch, RollupInterval.FiveMinutes, Start, Start.AddMinutes(5), cadence, generation);
}
