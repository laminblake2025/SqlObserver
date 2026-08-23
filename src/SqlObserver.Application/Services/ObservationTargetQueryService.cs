using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public sealed class ListObservationTargetsQuery
{
    public ListObservationTargetsQuery(
        AuthorizationContext authorization,
        int maxResults,
        ObservationTargetListCursor? cursor,
        bool includeRetired,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(timeout);

        if (maxResults is <= 0 or > ListObservationTargetsRequest.MaximumResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        Authorization = authorization;
        MaxResults = maxResults;
        Cursor = cursor;
        IncludeRetired = includeRetired;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }

    public int MaxResults { get; }

    public ObservationTargetListCursor? Cursor { get; }

    public bool IncludeRetired { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public interface IObservationTargetQueryService
{
    ValueTask<ObservationTargetPage> ListAsync(
        ListObservationTargetsQuery query,
        CancellationToken cancellationToken);
}

public sealed class ObservationTargetQueryService : IObservationTargetQueryService
{
    private static readonly ApplicationRole[] ReadRoles =
    [
        ApplicationRole.Viewer,
        ApplicationRole.Operator,
        ApplicationRole.TargetAdministrator,
        ApplicationRole.SecurityAdministrator,
        ApplicationRole.Auditor,
    ];

    private static readonly ApplicationRole[] RetiredReadRoles =
    [
        ApplicationRole.TargetAdministrator,
        ApplicationRole.Auditor,
    ];

    private readonly IObservationTargetRepositoryPort _targets;

    public ObservationTargetQueryService(IObservationTargetRepositoryPort targets)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public async ValueTask<ObservationTargetPage> ListAsync(
        ListObservationTargetsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        TargetAuthorizationScope authorizedScope = GetAuthorizedScope(
            query.Authorization,
            query.IncludeRetired);

        ObservationTargetPage page = await _targets.ListObservationTargetsAsync(
                new ListObservationTargetsRequest(
                    authorizedScope,
                    query.MaxResults,
                    query.Cursor,
                    query.IncludeRetired,
                    query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);

        if (page.Targets.Any(target => !authorizedScope.Contains(target.TargetId)))
        {
            throw new InvalidDataException("The target repository returned a target outside the authorized scope.");
        }

        return page;
    }

    internal static TargetAuthorizationScope GetAuthorizedScope(
        AuthorizationContext authorization,
        bool includeRetired)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ApplicationRole[] allowedRoles = includeRetired ? RetiredReadRoles : ReadRoles;

        if (!allowedRoles.Any(authorization.HasRole))
        {
            throw new UnauthorizedAccessException("The principal is not authorized for this target projection.");
        }

        return authorization.GetScopeForRoles(allowedRoles);
    }
}
