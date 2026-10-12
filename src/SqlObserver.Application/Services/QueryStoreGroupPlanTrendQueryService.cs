using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IQueryStoreGroupPlanTrendQueryService
{
    ValueTask<QueryStoreGroupPlanTrendResult> ReadAsync(
        AuthorizationContext authorization, QueryStoreGroupPlanTrendRequest request,
        CancellationToken cancellationToken);
}

public sealed class QueryStoreGroupPlanTrendQueryService(
    IQueryStoreGroupPlanTrendRepositoryPort repository)
    : IQueryStoreGroupPlanTrendQueryService
{
    private static readonly ApplicationRole[] ReadRoles =
        [ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator];

    public async ValueTask<QueryStoreGroupPlanTrendResult> ReadAsync(
        AuthorizationContext authorization, QueryStoreGroupPlanTrendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ReadRoles);
        QueryStoreGroupPlanTrendResult result = await repository.ReadAsync(request,
            cancellationToken).ConfigureAwait(false);
        if (result.TargetId != request.TargetId || result.Plan != request.Plan ||
            result.FromUtc != request.FromUtc || result.ToUtc != request.ToUtc ||
            result.Points.Count > 169 ||
            result.Points.Any(point => point.BucketStartUtc.Offset != TimeSpan.Zero ||
                point.BucketStartUtc < request.FromUtc.AddHours(-1) ||
                point.BucketStartUtc >= request.ToUtc ||
                point.SourceRows <= 0 ||
                point.RuntimeComparableRows + point.RuntimeBaselineRows +
                    point.RuntimeResetRows + point.RuntimeAmbiguousRows != point.SourceRows ||
                point.WaitComparableRows + point.WaitBaselineRows +
                    point.WaitResetRows + point.WaitUnavailableRows +
                    point.WaitStaleRows != point.SourceRows ||
                point.WaitCategories.Count > 32))
            throw new InvalidDataException("Query Store group trend exceeded its target or coverage bounds.");
        return result;
    }
}
