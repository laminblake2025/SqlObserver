using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IQueryStoreWaitRankingQueryService
{
    ValueTask<QueryStoreWaitRankingResult> ReadAsync(
        AuthorizationContext authorization, QueryStoreWaitRankingRequest request,
        CancellationToken cancellationToken);
}

public sealed class QueryStoreWaitRankingQueryService(IQueryStoreWaitRankingRepositoryPort repository)
    : IQueryStoreWaitRankingQueryService
{
    public async ValueTask<QueryStoreWaitRankingResult> ReadAsync(
        AuthorizationContext authorization, QueryStoreWaitRankingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        QueryStoreWaitRankingResult result = await repository.ReadAsync(request,
            cancellationToken).ConfigureAwait(false);
        if (result.TargetId != request.TargetId || result.FromUtc != request.FromUtc ||
            result.ToUtc != request.ToUtc || result.Category != request.Category ||
            result.Items.Count > request.Limit ||
            result.Items.Any(item => item.ComparableRows is < 1 ||
                item.SourceRows < item.ComparableRows ||
                item.LastObservedAtUtc < request.FromUtc ||
                item.LastObservedAtUtc >= request.ToUtc ||
                !System.Numerics.BigInteger.TryParse(item.WaitMilliseconds,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var wait) || wait < 0))
            throw new InvalidDataException("Query wait ranking exceeded its target or evidence bounds.");
        if (result.Items.Select(static item => item.Plan).Distinct().Count() != result.Items.Count ||
            result.Items.Zip(result.Items.Skip(1)).Any(static pair =>
                System.Numerics.BigInteger.Parse(pair.First.WaitMilliseconds,
                    System.Globalization.CultureInfo.InvariantCulture) <
                System.Numerics.BigInteger.Parse(pair.Second.WaitMilliseconds,
                    System.Globalization.CultureInfo.InvariantCulture)))
            throw new InvalidDataException("Query wait ranking order or identity is invalid.");
        return result;
    }
}
