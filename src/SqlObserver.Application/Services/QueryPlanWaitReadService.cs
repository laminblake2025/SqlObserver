using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Services;

public sealed class QueryPlanWaitReadService(IQueryPlanWaitReadRepositoryPort repository)
    : IQueryPlanWaitReadService
{
    private static readonly ApplicationRole[] ReadRoles =
        [ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator];

    public async ValueTask<QueryPlanWaitReadResult> ReadAsync(
        AuthorizationContext authorization, QueryPlanReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        if (request.CollectionRunId == Guid.Empty)
            throw new ArgumentException("A collection run is required.", nameof(request));
        authorization.RequireAny(request.TargetId, ReadRoles);
        QueryPlanWaitSnapshot? snapshot = await repository.ReadAsync(request, cancellationToken);
        if (snapshot is null) return new("unavailable", null, null);
        if (snapshot.CapturedAtUtc.Offset != TimeSpan.Zero ||
            snapshot.Categories.Count > QueryStoreWaitSnapshot.MaximumCategories ||
            snapshot.Categories.Select(static item => item.Category).Distinct().Count() != snapshot.Categories.Count)
            throw new InvalidDataException("Query plan wait evidence exceeded its bounds.");
        return new("available", snapshot.Categories, snapshot.CapturedAtUtc);
    }
}
