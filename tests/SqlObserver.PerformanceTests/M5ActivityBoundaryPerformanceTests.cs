using System.Diagnostics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;

namespace SqlObserver.PerformanceTests;

public sealed class M5ActivityBoundaryPerformanceTests
{
    [Fact]
    public void ActivityBatchesEnforceCardinalityAndConstructWithinBound()
    {
        var target = new MonitoredInstanceId(Guid.Parse("12121212-1212-1212-1212-121212121212"));
        var revision = new ObservationTargetRevision(1);
        DateTimeOffset observedAt = new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
        var stopwatch = Stopwatch.StartNew();
        var sessions = Enumerable.Range(1, ActivitySessionObservationBatch.MaximumItems)
            .Select(session => new ActivitySessionObservation(
                target, revision, session, ActivitySessionStatus.Running, true, null,
                0, session, 0, session, 0, session, session, observedAt.AddTicks(session * 10)))
            .ToArray();
        var batch = new ActivitySessionObservationBatch(sessions);
        stopwatch.Stop();

        Assert.Equal(ActivitySessionObservationBatch.MaximumItems, batch.Items.Count);
        Assert.Equal(ActivitySessionObservationBatch.MaximumItems * ActivitySessionObservation.FixedEstimatedBytes, batch.Items.Sum(static item => item.EstimatedSizeBytes));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"bounded activity batch construction took {stopwatch.Elapsed}");
        Assert.Throws<ArgumentException>(() => new ActivitySessionObservationBatch(
            sessions.Concat([sessions[^1]]).ToArray()));

        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(5));
        _ = new ListActivitySessionsRepositoryRequest(target, 25, null, timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ListActivitySessionsRepositoryRequest(target, 101, null, timeout));
        _ = new ListCurrentBlockingRepositoryRequest(target, BlockingChainLimits.MaximumNodes, null, timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ListCurrentBlockingRepositoryRequest(target, BlockingChainLimits.MaximumNodes + 1, null, timeout));
        Assert.Equal(1_024, BlockingEdgeObservationBatch.MaximumItems);
    }
}
