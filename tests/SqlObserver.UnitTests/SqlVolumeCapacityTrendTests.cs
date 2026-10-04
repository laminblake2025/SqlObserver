using SqlObserver.Application.Services;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class SqlVolumeCapacityTrendTests
{
    private const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Today = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompleteDailyDeclineEstimatesTargetVolumeExhaustion()
    {
        SqlVolumeCapacityTrend trend = SqlVolumeCapacityTrendEstimator.Estimate(Current(4_000),
            Samples(10_000, 1_000, 7));
        Assert.Equal("estimated", trend.State);
        Assert.Equal(7, trend.SampleDays);
        Assert.InRange(trend.DaysUntilFull!.Value, 3.99, 4.01);
    }

    [Fact]
    public void SparseOrUnstableHistoryDoesNotProduceAnEstimate()
    {
        Assert.Equal("insufficient_history", SqlVolumeCapacityTrendEstimator.Estimate(
            Current(4_000), Samples(7_000, 1_000, 4)).State);
        SqlVolumeDailyCapacitySample[] unstable =
        [
            Sample(-4, 5_000), Sample(-3, 9_000), Sample(-2, 4_000),
            Sample(-1, 8_000), Sample(0, 4_000),
        ];
        Assert.Equal("unstable_trend", SqlVolumeCapacityTrendEstimator.Estimate(
            Current(4_000), unstable).State);
    }

    [Fact]
    public void ResizeOrNonDepletionDoesNotProduceAFalseDeadline()
    {
        SqlVolumeDailyCapacitySample[] resized = Samples(10_000, 1_000, 7);
        resized[0] = resized[0] with { TotalBytes = 25_000 };
        Assert.Equal("capacity_changed", SqlVolumeCapacityTrendEstimator.Estimate(
            Current(4_000), resized).State);
        Assert.Equal("not_depleting", SqlVolumeCapacityTrendEstimator.Estimate(
            Current(10_000), Samples(4_000, -1_000, 7)).State);
        Assert.Equal("full", SqlVolumeCapacityTrendEstimator.Estimate(Current(0), []).State);
    }

    private static SqlVolumeObservation Current(long? available) => new(
        M4TestData.TargetId, M4TestData.TargetRevision, Key, SqlVolumeIdentityKind.VolumeId,
        1, available is null ? null : 20_000, available, Today);

    [Fact]
    public void SmallDeclineNearInt64MaximumRemainsDepletingBeyondSupportedHorizon()
    {
        var current = new SqlVolumeObservation(M4TestData.TargetId, M4TestData.TargetRevision, Key,
            SqlVolumeIdentityKind.VolumeId, 1, long.MaxValue, long.MaxValue - 6, Today);
        SqlVolumeDailyCapacitySample[] samples = Enumerable.Range(0, 7).Select(index =>
            new SqlVolumeDailyCapacitySample(Key, Today.AddDays(index - 6), long.MaxValue, long.MaxValue - index)).ToArray();
        Assert.Equal("beyond_horizon", SqlVolumeCapacityTrendEstimator.Estimate(current, samples).State);
    }

    private static SqlVolumeDailyCapacitySample Sample(int daysFromToday, long available) =>
        new(Key, Today.AddDays(daysFromToday), 20_000, available);

    private static SqlVolumeDailyCapacitySample[] Samples(long first, long lossPerDay, int count) =>
        Enumerable.Range(0, count)
            .Select(index => Sample(index - count + 1, first - index * lossPerDay))
            .ToArray();
}
