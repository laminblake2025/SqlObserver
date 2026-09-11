using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class ServerWaitSummaryPageTests
{
    [Fact]
    public void FinalPageRetainsBaselineWithoutRequiringAnotherCursor()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var baseline = new CollectorRunId(Guid.NewGuid());
        var page = new ServerWaitSummaryPage(target, null, baseline, [], null, DateTimeOffset.UnixEpoch);
        Assert.Equal(baseline, page.BaselineRunId);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void ContinuingPageRejectsDifferentBaseline()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var run = new CollectorRunId(Guid.NewGuid());
        var revision = new ObservationTargetRevision(1);
        var evidence = new ActivitySnapshotEvidence(target, run, revision, new CollectorId("waits.server"),
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, CollectorLossEvidence.None, DateTimeOffset.UnixEpoch);
        var cursor = new ServerWaitSummaryCursor(target, run, new CollectorRunId(Guid.NewGuid()), revision, new("WAITFOR"));
        var exception = Assert.Throws<ArgumentException>(() => new ServerWaitSummaryPage(target, evidence,
            new CollectorRunId(Guid.NewGuid()), [], cursor, DateTimeOffset.UnixEpoch));
        Assert.Equal("nextCursor", exception.ParamName);
    }
}
