using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

/// <summary>Repository-clock source window and durable cursor for one target revision.</summary>
public sealed record QueryStoreGroupSourceWindow
{
    public QueryStoreGroupSourceWindow(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, DateTimeOffset? cursorEndUtc,
        DateTimeOffset repositoryTimeUtc, bool gapBeforeWindow)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            repositoryTimeUtc.Offset != TimeSpan.Zero ||
            cursorEndUtc is { } nonUtcCursor && nonUtcCursor.Offset != TimeSpan.Zero ||
            toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromHours(2) ||
            toUtc > repositoryTimeUtc ||
            fromUtc < repositoryTimeUtc - TimeSpan.FromDays(7) ||
            gapBeforeWindow != (cursorEndUtc is { } cursorEnd && fromUtc > cursorEnd))
            throw new ArgumentException("Query Store group source window is outside its repository-clock contract.");
        FromUtc = fromUtc;
        ToUtc = toUtc;
        CursorEndUtc = cursorEndUtc;
        RepositoryTimeUtc = repositoryTimeUtc;
        GapBeforeWindow = gapBeforeWindow;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public DateTimeOffset? CursorEndUtc { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
    public bool GapBeforeWindow { get; }
}

public interface IQueryStoreGroupWindowPort
{
    ValueTask<QueryStoreGroupSourceWindow> ReadNextAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken);
}
