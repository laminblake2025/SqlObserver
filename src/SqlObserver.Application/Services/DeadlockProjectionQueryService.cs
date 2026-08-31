using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class ListDeadlocksQuery
{
    public ListDeadlocksQuery(AuthorizationContext authorization, MonitoredInstanceId targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxResults, DeadlockPageCursor? cursor, RepositoryCallTimeout timeout)
    { Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization)); _ = new ListDeadlocksRepositoryRequest(targetId, fromUtc, toUtc, maxResults, cursor, timeout); TargetId = targetId; FromUtc = fromUtc; ToUtc = toUtc; MaxResults = maxResults; Cursor = cursor; Timeout = timeout; }
    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int MaxResults { get; }
    public DeadlockPageCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}
public interface IDeadlockProjectionQueryService
{
    ValueTask<DeadlockPage?> ListDeadlocksAsync(ListDeadlocksQuery query, CancellationToken cancellationToken);
    ValueTask<DeadlockDetailDto?> GetDeadlockAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, Guid eventId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}
public sealed class DeadlockProjectionQueryService : IDeadlockProjectionQueryService
{
    private static readonly ApplicationRole[] ReadRoles = [ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator];
    private readonly IDeadlockProjectionRepositoryPort repository;
    public DeadlockProjectionQueryService(IDeadlockProjectionRepositoryPort repository) => this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    public async ValueTask<DeadlockPage?> ListDeadlocksAsync(ListDeadlocksQuery query, CancellationToken cancellationToken)
    { ArgumentNullException.ThrowIfNull(query); Authorize(query.Authorization, query.TargetId); DeadlockPage? page = await repository.ListDeadlocksAsync(new ListDeadlocksRepositoryRequest(query.TargetId, query.FromUtc, query.ToUtc, query.MaxResults, query.Cursor, query.Timeout), cancellationToken); if (page is null) return null; if (page.TargetId != query.TargetId || page.RepositoryTimeUtc.Offset != TimeSpan.Zero || page.Items.Count > query.MaxResults || page.Items.Any(item => !ValidSummary(item, query.TargetId, query.FromUtc, query.ToUtc)) || page.Items.Zip(page.Items.Skip(1)).Any(pair => (pair.First.OccurredAtUtc, pair.First.EventId).CompareTo((pair.Second.OccurredAtUtc, pair.Second.EventId)) <= 0) || page.NextCursor is not null && (page.NextCursor.TargetId != query.TargetId || page.NextCursor.FromUtc != query.FromUtc || page.NextCursor.ToUtc != query.ToUtc || page.NextCursor.SnapshotCollectedAtUtc.Offset != TimeSpan.Zero)) throw new InvalidDataException("Deadlock projection exceeded its target or bounds contract."); return page; }
    public async ValueTask<DeadlockDetailDto?> GetDeadlockAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, Guid eventId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    { ArgumentNullException.ThrowIfNull(authorization); Authorize(authorization, targetId); DeadlockDetailDto? detail = await repository.GetDeadlockAsync(targetId, eventId, timeout, cancellationToken); if (detail is null) return null; if (detail.Summary.EventId != eventId || !ValidSummary(detail.Summary, targetId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue) || detail.Summary.ParticipantCount != detail.Participants.Count || detail.Summary.RelationCount != detail.Relations.Count || detail.Participants.Any(item => item.SessionId is < 1 or > 32767) || detail.Relations.Any(item => item.BlockerSessionId is < 1 or > 32767 || item.WaiterSessionId is < 1 or > 32767 || item.ResourceCategory is not ("key" or "page" or "object_lock" or "metadata" or "exchange" or "other") || DeadlockLockModes.Normalize(item.LockMode) != item.LockMode)) throw new InvalidDataException("Deadlock detail exceeded its target or bounds contract."); return detail; }
    private static bool ValidSummary(DeadlockSummaryDto item, MonitoredInstanceId targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc) => item.TargetId == targetId && item.EventId != Guid.Empty && item.Fingerprint is { Length: 64 } && item.Fingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') && item.EventId == DeadlockObservation.ComputeEventId(item.TargetId, item.Fingerprint) && item.ParticipantCount is >= 0 and <= 128 && item.RelationCount is >= 0 and <= 256 && item.OccurredAtUtc.Offset == TimeSpan.Zero && item.CollectedAtUtc.Offset == TimeSpan.Zero && (fromUtc == DateTimeOffset.MinValue || item.OccurredAtUtc >= fromUtc && item.OccurredAtUtc < toUtc);
    private static void Authorize(AuthorizationContext context, MonitoredInstanceId targetId) { if (!ReadRoles.Any(context.HasRole) || !context.CanAccess(targetId)) throw new UnauthorizedAccessException("The caller is not authorized for this observation target."); }
}
