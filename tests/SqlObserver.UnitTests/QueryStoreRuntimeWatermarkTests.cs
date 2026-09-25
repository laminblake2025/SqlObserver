using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryStoreRuntimeWatermarkTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstCompleteReadStartsBaselineAndQuietReadProducesKnownZero()
    {
        QueryStoreRuntimeWatermark first = Sample(10, 100, Start.AddMinutes(1), Start.AddMinutes(2));
        QueryStoreRuntimeTransition baseline = QueryStoreRuntimeWatermarkCalculator.Compare(null, first, true);
        Assert.Equal(QueryStoreRuntimeTransitionKind.BaselineUnavailable, baseline.Kind);
        Assert.Null(baseline.Delta);
        Assert.Same(first, baseline.NextWatermark);

        QueryStoreRuntimeWatermark quiet = Sample(10, 100, Start.AddMinutes(1), Start.AddMinutes(3), first.Key);
        QueryStoreRuntimeTransition next = QueryStoreRuntimeWatermarkCalculator.Compare(first, quiet, true);
        Assert.Equal(QueryStoreRuntimeTransitionKind.Comparable, next.Kind);
        Assert.Equal(new QueryStoreRuntimeCounters(0, 0, 0, 0, 0, 0), next.Delta);
        Assert.Same(quiet, next.NextWatermark);
    }

    [Fact]
    public void NewWorkPublishesOnlyComponentDifferences()
    {
        QueryStoreRuntimeWatermark first = Sample(10, 100, Start.AddMinutes(1), Start.AddMinutes(2));
        QueryStoreRuntimeWatermark next = Sample(13, 140, Start.AddMinutes(1), Start.AddMinutes(3), first.Key);
        QueryStoreRuntimeTransition result = QueryStoreRuntimeWatermarkCalculator.Compare(first, next, true);
        Assert.Equal(QueryStoreRuntimeTransitionKind.Comparable, result.Kind);
        Assert.Equal(new QueryStoreRuntimeCounters(40, 80, 3, 6, 9, 12), result.Delta);
    }

    [Fact]
    public void RefilledResetDoesNotInventAnIncrement()
    {
        QueryStoreRuntimeWatermark first = Sample(2, 100, Start.AddMinutes(1), Start.AddMinutes(2));
        QueryStoreRuntimeWatermark refilled = Sample(6, 150, Start.AddMinutes(4), Start.AddMinutes(5), first.Key);
        QueryStoreRuntimeTransition result = QueryStoreRuntimeWatermarkCalculator.Compare(first, refilled, true);
        Assert.Equal(QueryStoreRuntimeTransitionKind.Reset, result.Kind);
        Assert.Null(result.Delta);
        Assert.Same(refilled, result.NextWatermark);
    }

    [Fact]
    public void FallingComponentStartsNewBaseline()
    {
        QueryStoreRuntimeWatermark first = Sample(10, 100, Start.AddMinutes(1), Start.AddMinutes(2));
        QueryStoreRuntimeWatermark fallen = Sample(12, 99, Start.AddMinutes(1), Start.AddMinutes(3), first.Key);
        QueryStoreRuntimeTransition result = QueryStoreRuntimeWatermarkCalculator.Compare(first, fallen, true);
        Assert.Equal(QueryStoreRuntimeTransitionKind.Reset, result.Kind);
        Assert.Null(result.Delta);
    }

    [Fact]
    public void IncompleteAndStaleReadsDoNotAdvanceWatermark()
    {
        QueryStoreRuntimeWatermark first = Sample(10, 100, Start.AddMinutes(1), Start.AddMinutes(2));
        QueryStoreRuntimeWatermark incomplete = Sample(20, 200, Start.AddMinutes(1), Start.AddMinutes(3), first.Key);
        QueryStoreRuntimeTransition lost = QueryStoreRuntimeWatermarkCalculator.Compare(first, incomplete, false);
        Assert.Equal(QueryStoreRuntimeTransitionKind.Incomplete, lost.Kind);
        Assert.Null(lost.Delta);
        Assert.Null(lost.NextWatermark);

        QueryStoreRuntimeWatermark stale = Sample(20, 200, Start.AddMinutes(1), Start.AddMinutes(2), first.Key);
        QueryStoreRuntimeTransition ignored = QueryStoreRuntimeWatermarkCalculator.Compare(first, stale, true);
        Assert.Equal(QueryStoreRuntimeTransitionKind.Stale, ignored.Kind);
        Assert.Null(ignored.NextWatermark);
    }

    [Fact]
    public void DifferentDatabaseIncarnationCannotReuseAnotherWatermark()
    {
        QueryStoreRuntimeWatermark first = Sample(10, 100, Start.AddMinutes(1), Start.AddMinutes(2));
        QueryStoreRuntimeWatermarkKey changed = Key(Guid.NewGuid());
        QueryStoreRuntimeWatermark recreated = Sample(11, 110, Start.AddMinutes(1), Start.AddMinutes(3), changed);
        Assert.Throws<ArgumentException>(() => QueryStoreRuntimeWatermarkCalculator.Compare(first, recreated, true));
        Assert.Equal(QueryStoreRuntimeTransitionKind.BaselineUnavailable,
            QueryStoreRuntimeWatermarkCalculator.Compare(null, recreated, true).Kind);
    }

    private static QueryStoreRuntimeWatermark Sample(long executions, long cpu, DateTimeOffset firstExecution,
        DateTimeOffset observed, QueryStoreRuntimeWatermarkKey? key = null) =>
        new(key ?? Key(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
            new QueryStoreRuntimeCounters(cpu, 2 * cpu, executions, 2 * executions,
                3 * executions, 4 * executions), firstExecution, observed, observed);

    private static QueryStoreRuntimeWatermarkKey Key(Guid databaseIncarnation)
    {
        QueryOpaqueIdentity query = new(5, new string('a', 64));
        return new QueryStoreRuntimeWatermarkKey(new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            new ObservationTargetRevision(3), databaseIncarnation, query,
            new PlanOpaqueIdentity(query, new string('c', 64)), Start.AddHours(-1), 1,
            Start, Start.AddHours(1), 0);
    }
}
