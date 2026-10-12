using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryStoreWaitWatermarkTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompleteEmptyBaselineMakesFirstCategoryARealDelta()
    {
        QueryStoreWaitWatermark empty = Snapshot(new Dictionary<int, long>(), Start);
        QueryStoreWaitTransition first = QueryStoreWaitWatermarkCalculator.Compare(null, empty, true,
            QueryStoreRuntimeTransitionKind.BaselineUnavailable);
        Assert.Equal(QueryStoreWaitTransitionKind.BaselineUnavailable, first.Kind);
        Assert.Null(first.DeltaMilliseconds);
        Assert.Same(empty, first.NextWatermark);

        QueryStoreWaitWatermark newWait = Snapshot(new Dictionary<int, long> { [3] = 42 },
            Start.AddMinutes(1), empty.Key);
        QueryStoreWaitTransition next = QueryStoreWaitWatermarkCalculator.Compare(empty, newWait, true,
            QueryStoreRuntimeTransitionKind.Comparable);
        Assert.Equal(QueryStoreWaitTransitionKind.Comparable, next.Kind);
        Assert.Equal(42, next.DeltaMilliseconds![3]);
        Assert.Same(newWait, next.NextWatermark);

        QueryStoreWaitWatermark quiet = Snapshot(new Dictionary<int, long> { [3] = 42 },
            Start.AddMinutes(2), empty.Key);
        Assert.Equal(0, QueryStoreWaitWatermarkCalculator.Compare(newWait, quiet, true,
            QueryStoreRuntimeTransitionKind.Comparable).DeltaMilliseconds![3]);
    }

    [Fact]
    public void DisappearingPositiveCategoryResetsWholeWaitSnapshot()
    {
        QueryStoreWaitWatermark first = Snapshot(new Dictionary<int, long> { [3] = 40, [6] = 10 }, Start);
        QueryStoreWaitWatermark missing = Snapshot(new Dictionary<int, long> { [6] = 15 },
            Start.AddMinutes(1), first.Key);
        QueryStoreWaitTransition result = QueryStoreWaitWatermarkCalculator.Compare(first, missing, true,
            QueryStoreRuntimeTransitionKind.Comparable);
        Assert.Equal(QueryStoreWaitTransitionKind.Reset, result.Kind);
        Assert.Null(result.DeltaMilliseconds);
        Assert.Same(missing, result.NextWatermark);
    }

    [Theory]
    [InlineData(QueryStoreRuntimeTransitionKind.BaselineUnavailable)]
    [InlineData(QueryStoreRuntimeTransitionKind.Reset)]
    [InlineData(QueryStoreRuntimeTransitionKind.EpochAmbiguous)]
    public void RuntimeEpochWithoutComparableDeltaRestartsWaitBaseline(
        QueryStoreRuntimeTransitionKind runtimeKind)
    {
        QueryStoreWaitWatermark first = Snapshot(new Dictionary<int, long> { [3] = 40 }, Start);
        QueryStoreWaitWatermark next = Snapshot(new Dictionary<int, long> { [3] = 70 },
            Start.AddMinutes(1), first.Key);
        QueryStoreWaitTransition result = QueryStoreWaitWatermarkCalculator.Compare(first, next, true,
            runtimeKind);
        Assert.Equal(QueryStoreWaitTransitionKind.BaselineUnavailable, result.Kind);
        Assert.Null(result.DeltaMilliseconds);
        Assert.Same(next, result.NextWatermark);
    }

    [Fact]
    public void IncompleteSourceAndStaleRuntimeCannotAdvanceWaits()
    {
        QueryStoreWaitWatermark first = Snapshot(new Dictionary<int, long> { [3] = 40 }, Start);
        QueryStoreWaitWatermark next = Snapshot(new Dictionary<int, long> { [3] = 70 },
            Start.AddMinutes(1), first.Key);
        QueryStoreWaitTransition incomplete = QueryStoreWaitWatermarkCalculator.Compare(first, next, false,
            QueryStoreRuntimeTransitionKind.Comparable);
        Assert.Equal(QueryStoreWaitTransitionKind.Incomplete, incomplete.Kind);
        Assert.Null(incomplete.NextWatermark);

        QueryStoreWaitTransition runtimeStale = QueryStoreWaitWatermarkCalculator.Compare(first, next, true,
            QueryStoreRuntimeTransitionKind.Stale);
        Assert.Equal(QueryStoreWaitTransitionKind.RuntimeIncomparable, runtimeStale.Kind);
        Assert.Null(runtimeStale.DeltaMilliseconds);
        Assert.Null(runtimeStale.NextWatermark);

        QueryStoreWaitTransition waitStale = QueryStoreWaitWatermarkCalculator.Compare(first, first, true,
            QueryStoreRuntimeTransitionKind.Comparable);
        Assert.Equal(QueryStoreWaitTransitionKind.Stale, waitStale.Kind);
        Assert.Null(waitStale.NextWatermark);
    }

    [Fact]
    public void WaitSnapshotRejectsInvalidCategoryAndCrossDatabaseComparison()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(new Dictionary<int, long> { [32] = 1 }, Start));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(new Dictionary<int, long> { [3] = -1 }, Start));
        QueryStoreWaitWatermark first = Snapshot(new Dictionary<int, long> { [3] = 1 }, Start);
        QueryStoreWaitWatermark other = Snapshot(new Dictionary<int, long> { [3] = 2 },
            Start.AddMinutes(1), Key(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => QueryStoreWaitWatermarkCalculator.Compare(first, other, true,
            QueryStoreRuntimeTransitionKind.Comparable));
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryStoreWaitWatermarkCalculator.Compare(null, first, true,
            (QueryStoreRuntimeTransitionKind)999));
    }

    private static QueryStoreWaitWatermark Snapshot(IReadOnlyDictionary<int, long> totals,
        DateTimeOffset observed, QueryStoreRuntimeWatermarkKey? key = null) =>
        new(key ?? Key(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")), totals, observed);

    private static QueryStoreRuntimeWatermarkKey Key(Guid databaseIncarnation)
    {
        QueryOpaqueIdentity query = new(5, new string('a', 64));
        return new QueryStoreRuntimeWatermarkKey(new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            new ObservationTargetRevision(3), databaseIncarnation, query,
            new PlanOpaqueIdentity(query, new string('c', 64)), Start.AddHours(-1), 1,
            Start, Start.AddHours(1), 0);
    }
}
