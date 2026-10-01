using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class PlanCacheRuntimeWatermarkTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
    private static readonly QueryOpaqueIdentity Query = new(5, new string('a', 64));
    private static readonly PlanOpaqueIdentity Plan = new(Query, new string('b', 64));

    [Fact]
    public void FirstReadIsUnknownWhileQuietReadHasARealZeroAndExactComparisonWindow()
    {
        PlanCacheRuntimeWatermark first = Sample(new(1000, 2000, 1, 10, 2, 3), Start);
        PlanCacheRuntimeTransition baseline = PlanCacheRuntimeWatermarkCalculator.Compare(null, first, true);
        Assert.Equal(PlanCacheRuntimeTransitionKind.BaselineUnavailable, baseline.Kind);
        Assert.Null(baseline.Delta); Assert.Null(baseline.DeltaFromUtc); Assert.Null(baseline.DeltaToUtc);
        Assert.Same(first, baseline.NextWatermark);
        var quiet = Sample(first.Counters, Start.AddSeconds(30));
        var result = PlanCacheRuntimeWatermarkCalculator.Compare(first, quiet, true);
        Assert.Equal(PlanCacheRuntimeTransitionKind.Comparable, result.Kind);
        Assert.Equal(new(0, 0, 0, 0, 0, 0), result.Delta);
        Assert.Equal(Start, result.DeltaFromUtc); Assert.Equal(quiet.ObservedAtUtc, result.DeltaToUtc);
        Assert.Same(quiet, result.NextWatermark);
    }

    [Fact]
    public void SubtractsRawCountersBeforeConvertingTimeUnits()
    {
        var first = Sample(new(999, 1001, 10, 100, 20, 30), Start);
        var current = Sample(new(1001, 3001, 13, 140, 22, 35), Start.AddSeconds(30));
        var result = PlanCacheRuntimeWatermarkCalculator.Compare(first, current, true);
        Assert.Equal(PlanCacheRuntimeTransitionKind.Comparable, result.Kind);
        Assert.Equal(new(2, 2000, 3, 40, 2, 5), result.Delta);
        QueryPerformanceMetricSet metrics = result.Delta!.ToMilliseconds();
        Assert.Equal(0L, metrics.CpuMilliseconds); Assert.Equal(2L, metrics.DurationMilliseconds);
        Assert.Equal(3L, metrics.Executions); Assert.Equal(40L, metrics.LogicalReads);
        Assert.Equal(2L, metrics.Writes); Assert.Equal(5L, metrics.Rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AnyFallingCounterResetsAllDeltasRatherThanClampingOneField(int index)
    {
        var first = Sample(new(100, 100, 100, 100, 100, 100), Start);
        long[] values = [110, 110, 110, 110, 110, 110]; values[index] = 99;
        var current = Sample(new(values[0], values[1], values[2], values[3], values[4], values[5]), Start.AddSeconds(30));
        var result = PlanCacheRuntimeWatermarkCalculator.Compare(first, current, true);
        Assert.Equal(PlanCacheRuntimeTransitionKind.Reset, result.Kind);
        Assert.Null(result.Delta); Assert.Null(result.DeltaFromUtc); Assert.Null(result.DeltaToUtc);
        Assert.Same(current, result.NextWatermark);
    }

    [Theory]
    [InlineData("target_revision")]
    [InlineData("database_incarnation")]
    [InlineData("source_epoch")]
    [InlineData("cache_entry")]
    [InlineData("plan")]
    public void RecompileEvictionRestartOrDatabaseReplacementStartsASeparateBaseline(string changed)
    {
        var first = Sample(new(100, 100, 100, 100, 100, 100), Start);
        var key = new PlanCacheRuntimeWatermarkKey(Target, new(changed == "target_revision" ? 2 : 1), Query,
            changed == "plan" ? new(Query, new string('f', 64)) : Plan,
            new string(changed == "database_incarnation" ? 'f' : 'c', 64),
            new string(changed == "source_epoch" ? 'f' : 'd', 64),
            new string(changed == "cache_entry" ? 'f' : 'e', 64));
        // A new incarnation can refill above the old totals. It is still not
        // evidence of the apparent increment across the old and new entries.
        var current = new PlanCacheRuntimeWatermark(key, new(200, 200, 200, 200, 200, 200), Start.AddSeconds(30));
        var result = PlanCacheRuntimeWatermarkCalculator.Compare(first, current, true);
        Assert.Equal(PlanCacheRuntimeTransitionKind.BaselineUnavailable, result.Kind);
        Assert.Null(result.Delta); Assert.Same(current, result.NextWatermark);
    }

    [Fact]
    public void TruncatedAndStaleSamplesCannotMoveTheBaselineOrEmitAZero()
    {
        var first = Sample(new(100, 100, 100, 100, 100, 100), Start);
        var current = Sample(new(200, 200, 200, 200, 200, 200), Start.AddSeconds(30));
        var incomplete = PlanCacheRuntimeWatermarkCalculator.Compare(first, current, false);
        Assert.Equal(PlanCacheRuntimeTransitionKind.Incomplete, incomplete.Kind);
        Assert.Null(incomplete.Delta); Assert.Null(incomplete.NextWatermark);
        Assert.Null(PlanCacheRuntimeWatermarkCalculator.Compare(null, current, false).NextWatermark);
        foreach (DateTimeOffset staleAt in new[] { Start, Start.AddTicks(-1) })
        {
            var stale = PlanCacheRuntimeWatermarkCalculator.Compare(first, Sample(current.Counters, staleAt), true);
            Assert.Equal(PlanCacheRuntimeTransitionKind.Stale, stale.Kind);
            Assert.Null(stale.Delta); Assert.Null(stale.NextWatermark);
            Assert.Null(stale.DeltaFromUtc); Assert.Null(stale.DeltaToUtc);
        }
    }

    [Fact]
    public void ExcessiveGapRestartsTheBaselineAtTheExistingObservationIntervalBoundary()
    {
        var first = Sample(new(100, 100, 100, 100, 100, 100), Start);
        var limit = Sample(new(200, 200, 200, 200, 200, 200), Start.AddDays(7));
        Assert.Equal(PlanCacheRuntimeTransitionKind.Comparable, PlanCacheRuntimeWatermarkCalculator.Compare(first, limit, true).Kind);
        var expired = Sample(limit.Counters, limit.ObservedAtUtc.AddTicks(1));
        var result = PlanCacheRuntimeWatermarkCalculator.Compare(first, expired, true);
        Assert.Equal(PlanCacheRuntimeTransitionKind.ComparisonWindowExpired, result.Kind);
        Assert.Null(result.Delta); Assert.Same(expired, result.NextWatermark);
    }

    [Fact]
    public void ANewerReadCannotRestoreAnOlderTargetRevision()
    {
        var counters = new PlanCacheRuntimeCounters(1, 1, 1, 1, 1, 1);
        var latest = new PlanCacheRuntimeWatermark(
            new(Target, new(2), Query, Plan, new string('c', 64), new string('d', 64), new string('e', 64)),
            counters, Start);
        var stale = Sample(counters, Start.AddSeconds(30));
        var result = PlanCacheRuntimeWatermarkCalculator.Compare(latest, stale, true);
        Assert.Equal(PlanCacheRuntimeTransitionKind.Stale, result.Kind);
        Assert.Null(result.Delta); Assert.Null(result.NextWatermark);
    }

    [Fact]
    public void CrossTargetAndCrossQueryWatermarksAreRejectedEvenOnIncompleteReads()
    {
        var first = Sample(new(1, 1, 1, 1, 1, 1), Start);
        foreach (var key in new[]
        {
            Key(new(Guid.NewGuid()), Query),
            Key(Target, new(6, Query.QueryFingerprint)),
            Key(Target, new(5, new string('f', 64))),
        })
        {
            var current = new PlanCacheRuntimeWatermark(key, first.Counters, Start.AddSeconds(30));
            Assert.Throws<ArgumentException>(() => PlanCacheRuntimeWatermarkCalculator.Compare(first, current, true));
            Assert.Throws<ArgumentException>(() => PlanCacheRuntimeWatermarkCalculator.Compare(first, current, false));
        }
    }

    [Fact]
    public void LongCountersSubtractWithoutOverflowAndRejectNegativeOrNonUtcInputs()
    {
        var first = Sample(new(1, 1, 1, 1, 1, 1), Start);
        var current = Sample(new(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue), Start.AddSeconds(30));
        var result = PlanCacheRuntimeWatermarkCalculator.Compare(first, current, true);
        Assert.Equal(long.MaxValue - 1, result.Delta!.CpuMicroseconds);
        Assert.Equal(long.MaxValue - 1, result.Delta.Rows);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanCacheRuntimeCounters(0, 0, -1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new PlanCacheRuntimeWatermark(first.Key, first.Counters, Start.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => new PlanCacheRuntimeWatermarkKey(Target, new(1), Query, Plan, "database-name", new string('d', 64), new string('e', 64)));
    }

    private static PlanCacheRuntimeWatermark Sample(PlanCacheRuntimeCounters counters, DateTimeOffset observedAt) =>
        new(Key(Target, Query), counters, observedAt);

    private static PlanCacheRuntimeWatermarkKey Key(MonitoredInstanceId target, QueryOpaqueIdentity query) =>
        new(target, new(1), query, new(query, Plan.PlanFingerprint), new string('c', 64), new string('d', 64), new string('e', 64));
}
