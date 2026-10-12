using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class ServerWaitTrendQuery
{
    public ServerWaitTrendQuery(AuthorizationContext authorization, MonitoredInstanceId targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, RepositoryCallTimeout timeout)
    {
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _ = new ServerWaitTrendRepositoryRequest(targetId, fromUtc, toUtc, timeout);
        TargetId = targetId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public interface IServerWaitTrendQueryService
{
    ValueTask<ServerWaitTrendPage?> ReadAsync(ServerWaitTrendQuery query,
        CancellationToken cancellationToken);
}

public sealed class ServerWaitTrendQueryService(IServerWaitTrendRepositoryPort repository)
    : IServerWaitTrendQueryService
{
    public async ValueTask<ServerWaitTrendPage?> ReadAsync(ServerWaitTrendQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.Authorization.CanAccess(ApplicationRole.Viewer, query.TargetId) &&
            !query.Authorization.CanAccess(ApplicationRole.Operator, query.TargetId) &&
            !query.Authorization.CanAccess(ApplicationRole.TargetAdministrator, query.TargetId))
            throw new UnauthorizedAccessException("The principal cannot read this target's wait trend.");
        ServerWaitTrendPage? result = await repository.ReadAsync(
            new ServerWaitTrendRepositoryRequest(query.TargetId, query.FromUtc,
                query.ToUtc, query.Timeout), cancellationToken).ConfigureAwait(false);
        if (result is not null && (result.TargetId != query.TargetId ||
            result.FromUtc != query.FromUtc || result.ToUtc != query.ToUtc))
            throw new InvalidDataException("The repository returned a wait trend outside the requested scope.");
        return result;
    }
}
