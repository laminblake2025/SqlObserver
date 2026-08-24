using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class GetInstanceHealthQuery
{
    public GetInstanceHealthQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        Authorization = authorization;
        TargetId = targetId;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListDatabaseHealthQuery
{
    public ListDatabaseHealthQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        int maxResults,
        DatabaseHealthCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _ = new ListDatabaseHealthRepositoryRequest(targetId, maxResults, cursor, timeout);
        Authorization = authorization;
        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public DatabaseHealthCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListDatabaseFileHealthQuery
{
    public ListDatabaseFileHealthQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        int maxResults,
        DatabaseFileHealthCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _ = new ListDatabaseFileHealthRepositoryRequest(targetId, maxResults, cursor, timeout);
        Authorization = authorization;
        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public DatabaseFileHealthCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public interface IHealthProjectionQueryService
{
    ValueTask<InstanceHealthProjection?> GetInstanceAsync(
        GetInstanceHealthQuery query,
        CancellationToken cancellationToken);

    ValueTask<DatabaseHealthPage?> ListDatabasesAsync(
        ListDatabaseHealthQuery query,
        CancellationToken cancellationToken);

    ValueTask<DatabaseFileHealthPage?> ListDatabaseFilesAsync(
        ListDatabaseFileHealthQuery query,
        CancellationToken cancellationToken);
}

/// <summary>Applies target-scoped read authorization before any health repository call.</summary>
public sealed class HealthProjectionQueryService : IHealthProjectionQueryService
{
    private static readonly ApplicationRole[] ReadRoles =
    [
        ApplicationRole.Viewer,
        ApplicationRole.Operator,
        ApplicationRole.TargetAdministrator,
    ];

    private readonly IHealthProjectionRepositoryPort _repository;

    public HealthProjectionQueryService(IHealthProjectionRepositoryPort repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async ValueTask<InstanceHealthProjection?> GetInstanceAsync(
        GetInstanceHealthQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        InstanceHealthProjection? result = await _repository.GetInstanceHealthAsync(
                new GetInstanceHealthRepositoryRequest(query.TargetId, query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);
        if (result is not null && result.TargetId != query.TargetId)
        {
            throw new InvalidDataException("The health repository returned a different target than requested.");
        }

        return result;
    }

    public async ValueTask<DatabaseHealthPage?> ListDatabasesAsync(
        ListDatabaseHealthQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        DatabaseHealthPage? result = await _repository.ListDatabaseHealthAsync(
                new ListDatabaseHealthRepositoryRequest(
                    query.TargetId,
                    query.MaxResults,
                    query.Cursor,
                    query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);
        if (result is not null &&
            (result.TargetId != query.TargetId ||
             result.Items.Any(item => item.Observation.TargetId != query.TargetId)))
        {
            throw new InvalidDataException("The health repository returned database evidence outside the requested target.");
        }

        return result;
    }

    public async ValueTask<DatabaseFileHealthPage?> ListDatabaseFilesAsync(
        ListDatabaseFileHealthQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        DatabaseFileHealthPage? result = await _repository.ListDatabaseFileHealthAsync(
                new ListDatabaseFileHealthRepositoryRequest(
                    query.TargetId,
                    query.MaxResults,
                    query.Cursor,
                    query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);
        if (result is not null &&
            (result.TargetId != query.TargetId ||
             result.Items.Any(item => item.Observation.TargetId != query.TargetId)))
        {
            throw new InvalidDataException("The health repository returned database-file evidence outside the requested target.");
        }

        return result;
    }

    private static void Authorize(AuthorizationContext authorization, MonitoredInstanceId targetId)
    {
        if (!ReadRoles.Any(role => authorization.CanAccess(role, targetId)))
        {
            throw new UnauthorizedAccessException("The principal is not authorized for this health projection.");
        }
    }
}
