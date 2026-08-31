using System.Collections.ObjectModel;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record DeadlockPageCursor
{
    public DeadlockPageCursor(MonitoredInstanceId targetId, DateTimeOffset occurredAtUtc, Guid eventId, DateTimeOffset snapshotCollectedAtUtc, DateTimeOffset fromUtc, DateTimeOffset toUtc) { TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); OccurredAtUtc = RequireUtc(occurredAtUtc); SnapshotCollectedAtUtc = RequireUtc(snapshotCollectedAtUtc); FromUtc = RequireUtc(fromUtc); ToUtc = RequireUtc(toUtc); if (ToUtc <= FromUtc || ToUtc - FromUtc > TimeSpan.FromDays(31)) throw new ArgumentException("Cursor window is invalid."); if (eventId == Guid.Empty) throw new ArgumentException("Event id is required.", nameof(eventId)); EventId = eventId; }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public Guid EventId { get; }
    public DateTimeOffset SnapshotCollectedAtUtc { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Timestamp must be UTC.");
}
public sealed record DeadlockParticipantDto(int SessionId, bool IsVictim);
public sealed record DeadlockRelationDto(int BlockerSessionId, int WaiterSessionId, string ResourceCategory, string LockMode);
public sealed record DeadlockSummaryDto(MonitoredInstanceId TargetId, Guid EventId, DateTimeOffset OccurredAtUtc, string Fingerprint, int ParticipantCount, int RelationCount, bool ParseTruncated, DateTimeOffset CollectedAtUtc);
public sealed class DeadlockDetailDto
{
    public DeadlockDetailDto(DeadlockSummaryDto summary, IReadOnlyList<DeadlockParticipantDto> participants, IReadOnlyList<DeadlockRelationDto> relations)
    { Summary = summary ?? throw new ArgumentNullException(nameof(summary)); Participants = new ReadOnlyCollection<DeadlockParticipantDto>((participants ?? throw new ArgumentNullException(nameof(participants))).ToArray()); Relations = new ReadOnlyCollection<DeadlockRelationDto>((relations ?? throw new ArgumentNullException(nameof(relations))).ToArray()); }
    public DeadlockSummaryDto Summary { get; }
    public IReadOnlyList<DeadlockParticipantDto> Participants { get; }
    public IReadOnlyList<DeadlockRelationDto> Relations { get; }
}
public sealed class DeadlockPage
{
    public DeadlockPage(MonitoredInstanceId targetId, DateTimeOffset repositoryTimeUtc, IReadOnlyList<DeadlockSummaryDto> items, DeadlockPageCursor? nextCursor)
    { TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); RepositoryTimeUtc = repositoryTimeUtc; Items = new ReadOnlyCollection<DeadlockSummaryDto>((items ?? throw new ArgumentNullException(nameof(items))).ToArray()); NextCursor = nextCursor; }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
    public IReadOnlyList<DeadlockSummaryDto> Items { get; }
    public DeadlockPageCursor? NextCursor { get; }
}
public sealed class ListDeadlocksRepositoryRequest
{
    public const int MaximumResults = 256;
    public ListDeadlocksRepositoryRequest(MonitoredInstanceId targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxResults, DeadlockPageCursor? cursor, RepositoryCallTimeout timeout)
    { TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); FromUtc = RequireUtc(fromUtc); ToUtc = RequireUtc(toUtc); if (ToUtc <= FromUtc || ToUtc - FromUtc > TimeSpan.FromDays(31)) throw new ArgumentException("UTC window must be increasing and bounded."); if (maxResults is <= 0 or > MaximumResults) throw new ArgumentOutOfRangeException(nameof(maxResults)); if (cursor is not null && (cursor.TargetId != targetId || cursor.FromUtc != FromUtc || cursor.ToUtc != ToUtc)) throw new ArgumentException("Cursor target or window mismatch."); Cursor = cursor; MaxResults = maxResults; Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout)); }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int MaxResults { get; }
    public DeadlockPageCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Timestamp must be UTC.");
}
public interface IDeadlockProjectionRepositoryPort
{
    ValueTask<DeadlockPage?> ListDeadlocksAsync(ListDeadlocksRepositoryRequest request, CancellationToken cancellationToken);
    ValueTask<DeadlockDetailDto?> GetDeadlockAsync(MonitoredInstanceId targetId, Guid eventId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}
