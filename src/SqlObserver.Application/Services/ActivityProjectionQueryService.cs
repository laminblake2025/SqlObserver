using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class ListActivitySessionsQuery
{
    public ListActivitySessionsQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        int maxResults,
        ActivitySessionCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _ = new ListActivitySessionsRepositoryRequest(targetId, maxResults, cursor, timeout);
        Authorization = authorization;
        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public ActivitySessionCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListActivityRequestsQuery
{
    public ListActivityRequestsQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        int maxResults,
        ActivityRequestCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _ = new ListActivityRequestsRepositoryRequest(targetId, maxResults, cursor, timeout);
        Authorization = authorization;
        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public ActivityRequestCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListServerWaitSummaryQuery
{
    public ListServerWaitSummaryQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        int maxResults,
        ServerWaitSummaryCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _ = new ListServerWaitSummaryRepositoryRequest(targetId, maxResults, cursor, timeout);
        Authorization = authorization;
        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public ServerWaitSummaryCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListCurrentBlockingQuery
{
    public ListCurrentBlockingQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        int maxResults,
        BlockingEdgeCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _ = new ListCurrentBlockingRepositoryRequest(targetId, maxResults, cursor, timeout);
        Authorization = authorization;
        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public BlockingEdgeCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListBlockingHistoryQuery
{
    public ListBlockingHistoryQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxResults,
        BlockingHistoryCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _ = new ListBlockingHistoryRepositoryRequest(
            targetId, fromUtc, toUtc, maxResults, cursor, timeout);
        Authorization = authorization;
        TargetId = targetId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int MaxResults { get; }
    public BlockingHistoryCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListServerWaitHistoryQuery
{
    public ListServerWaitHistoryQuery(AuthorizationContext authorization,
        MonitoredInstanceId targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int maxResults, ServerWaitHistoryCursor? cursor, RepositoryCallTimeout timeout)
    {
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _ = new ListServerWaitHistoryRepositoryRequest(targetId, fromUtc, toUtc,
            maxResults, cursor, timeout);
        TargetId = targetId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int MaxResults { get; }
    public ServerWaitHistoryCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public interface IActivityProjectionQueryService
{
    ValueTask<ActivitySessionPage?> ListSessionsAsync(
        ListActivitySessionsQuery query,
        CancellationToken cancellationToken);

    ValueTask<ActivityRequestPage?> ListRequestsAsync(
        ListActivityRequestsQuery query,
        CancellationToken cancellationToken);

    ValueTask<ServerWaitSummaryPage?> ListWaitSummaryAsync(
        ListServerWaitSummaryQuery query,
        CancellationToken cancellationToken);

    ValueTask<CurrentBlockingPage?> ListCurrentBlockingAsync(
        ListCurrentBlockingQuery query,
        CancellationToken cancellationToken);

    ValueTask<BlockingHistoryPage?> ListBlockingHistoryAsync(
        ListBlockingHistoryQuery query,
        CancellationToken cancellationToken);

    ValueTask<ServerWaitHistoryPage?> ListServerWaitHistoryAsync(
        ListServerWaitHistoryQuery query,
        CancellationToken cancellationToken);
}

/// <summary>Enforces target-scoped read authorization before activity evidence is requested.</summary>
public sealed class ActivityProjectionQueryService : IActivityProjectionQueryService
{
    private static readonly ApplicationRole[] ReadRoles =
    [
        ApplicationRole.Viewer,
        ApplicationRole.Operator,
        ApplicationRole.TargetAdministrator,
    ];

    private readonly IActivityProjectionRepositoryPort _repository;

    public ActivityProjectionQueryService(IActivityProjectionRepositoryPort repository) =>
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async ValueTask<ActivitySessionPage?> ListSessionsAsync(
        ListActivitySessionsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        ActivitySessionPage? result = await _repository.ListSessionsAsync(
            new ListActivitySessionsRepositoryRequest(
                query.TargetId,
                query.MaxResults,
                query.Cursor,
                query.Timeout),
            cancellationToken).ConfigureAwait(false);
        return ValidateTarget(result, query.TargetId);
    }

    public async ValueTask<ActivityRequestPage?> ListRequestsAsync(
        ListActivityRequestsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        ActivityRequestPage? result = await _repository.ListRequestsAsync(
            new ListActivityRequestsRepositoryRequest(
                query.TargetId,
                query.MaxResults,
                query.Cursor,
                query.Timeout),
            cancellationToken).ConfigureAwait(false);
        return ValidateTarget(result, query.TargetId);
    }

    public async ValueTask<ServerWaitSummaryPage?> ListWaitSummaryAsync(
        ListServerWaitSummaryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        ServerWaitSummaryPage? result = await _repository.ListWaitSummaryAsync(
            new ListServerWaitSummaryRepositoryRequest(
                query.TargetId,
                query.MaxResults,
                query.Cursor,
                query.Timeout),
            cancellationToken).ConfigureAwait(false);
        return ValidateTarget(result, query.TargetId);
    }

    public async ValueTask<CurrentBlockingPage?> ListCurrentBlockingAsync(
        ListCurrentBlockingQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        CurrentBlockingPage? result = await _repository.ListCurrentBlockingAsync(
            new ListCurrentBlockingRepositoryRequest(
                query.TargetId,
                query.MaxResults,
                query.Cursor,
                query.Timeout),
            cancellationToken).ConfigureAwait(false);
        return ValidateTarget(result, query.TargetId);
    }

    public async ValueTask<BlockingHistoryPage?> ListBlockingHistoryAsync(
        ListBlockingHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        BlockingHistoryPage? result = await _repository.ListBlockingHistoryAsync(
            new ListBlockingHistoryRepositoryRequest(
                query.TargetId,
                query.FromUtc,
                query.ToUtc,
                query.MaxResults,
                query.Cursor,
                query.Timeout),
            cancellationToken).ConfigureAwait(false);
        if (result is not null && result.TargetId != query.TargetId)
        {
            throw new InvalidDataException("The activity repository returned history outside the requested target.");
        }

        return result;
    }

    public async ValueTask<ServerWaitHistoryPage?> ListServerWaitHistoryAsync(
        ListServerWaitHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        ServerWaitHistoryPage? result = await _repository.ListServerWaitHistoryAsync(
            new ListServerWaitHistoryRepositoryRequest(query.TargetId, query.FromUtc,
                query.ToUtc, query.MaxResults, query.Cursor, query.Timeout),
            cancellationToken).ConfigureAwait(false);
        if (result is not null &&
            (result.TargetId != query.TargetId || result.FromUtc != query.FromUtc ||
             result.ToUtc != query.ToUtc))
            throw new InvalidDataException("The activity repository returned wait history outside the requested scope.");
        return result;
    }

    private static TPage? ValidateTarget<TPage>(TPage? page, MonitoredInstanceId targetId)
        where TPage : class
    {
        if (page is ActivityPage<ActivitySessionSnapshotItem, ActivitySessionCursor> sessions &&
            sessions.TargetId != targetId ||
            page is ActivityPage<ActivityRequestSnapshotItem, ActivityRequestCursor> requests &&
            requests.TargetId != targetId ||
            page is ActivityPage<ServerWaitSummaryItem, ServerWaitSummaryCursor> waits &&
            waits.TargetId != targetId ||
            page is ActivityPage<BlockingEdgeSnapshotItem, BlockingEdgeCursor> blocking &&
            blocking.TargetId != targetId)
        {
            throw new InvalidDataException("The activity repository returned evidence outside the requested target.");
        }

        return page;
    }

    private static void Authorize(AuthorizationContext authorization, MonitoredInstanceId targetId)
    {
        if (!ReadRoles.Any(role => authorization.CanAccess(role, targetId)))
        {
            throw new UnauthorizedAccessException("The principal is not authorized for this activity projection.");
        }
    }
}
