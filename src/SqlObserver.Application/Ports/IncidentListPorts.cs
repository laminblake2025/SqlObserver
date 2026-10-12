using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using System.Text.Json.Serialization;

namespace SqlObserver.Application.Ports;

public sealed record IncidentListQuery(
    AuthorizationContext Authorization,
    MonitoredInstanceId TargetId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Limit,
    RepositoryCallTimeout Timeout,
    ObservationTargetRevision? TargetRevision = null,
    DateTimeOffset? SnapshotUtc = null,
    IncidentListCursor? Cursor = null);

/// <summary>A frozen window and publication fence with the full ascending thread tie key.</summary>
public sealed record IncidentListCursor
{
    [JsonConstructor]
    public IncidentListCursor(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset snapshotUtc,
        long publicationRevision, DateTimeOffset openedAtUtc, Guid threadId)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero || snapshotUtc.Offset != TimeSpan.Zero ||
            openedAtUtc.Offset != TimeSpan.Zero || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(31) ||
            openedAtUtc < fromUtc || openedAtUtc >= toUtc || openedAtUtc > snapshotUtc)
            throw new ArgumentException("Incident cursor timestamps are invalid.");
        ArgumentOutOfRangeException.ThrowIfLessThan(publicationRevision, 1);
        if (threadId == Guid.Empty) throw new ArgumentException("Incident thread id is required.", nameof(threadId));
        FromUtc = fromUtc;
        ToUtc = toUtc;
        SnapshotUtc = snapshotUtc;
        PublicationRevision = publicationRevision;
        OpenedAtUtc = openedAtUtc;
        ThreadId = threadId;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public DateTimeOffset SnapshotUtc { get; }
    public long PublicationRevision { get; }
    public DateTimeOffset OpenedAtUtc { get; }
    public Guid ThreadId { get; }
}

public sealed record IncidentListItem(Guid ThreadId, DateTimeOffset OpenedAtUtc,
    DateTimeOffset? LatestGenerationObservedAtUtc, long GenerationCount);

public sealed record IncidentListPage(MonitoredInstanceId TargetId, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, IReadOnlyList<IncidentListItem> Items, ObservationTargetRevision TargetRevision,
    DateTimeOffset SnapshotUtc, long PublicationRevision, bool HasMore, IncidentListCursor? NextCursor);

public interface IIncidentListProjectionRepositoryPort
{
    ValueTask<IncidentListPage> ListIncidentsAsync(IncidentListQuery query, CancellationToken cancellationToken);
}

/// <summary>The incident set changed; continuing would risk gaps or inconsistent metadata.</summary>
public sealed class IncidentListChangedException : Exception
{
    public IncidentListChangedException() : base("Incidents changed. Restart list_incidents without a cursor.") { }
}
