using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Services;

public interface IQueryStorePlanRegressionQueryService
{
    ValueTask<QueryStorePlanRegressionResult> ReadAsync(
        AuthorizationContext authorization, QueryStorePlanRegressionRequest request,
        CancellationToken cancellationToken);
}

public sealed class QueryStorePlanRegressionQueryService(IQueryStorePlanRegressionRepositoryPort repository)
    : IQueryStorePlanRegressionQueryService
{
    public async ValueTask<QueryStorePlanRegressionResult> ReadAsync(
        AuthorizationContext authorization, QueryStorePlanRegressionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        QueryStorePlanRegressionResult result = await repository.ReadAsync(request,
            cancellationToken).ConfigureAwait(false);
        if (result.TargetId != request.TargetId || result.FromUtc != request.FromUtc ||
            result.ToUtc != request.ToUtc || result.Items.Count > request.Limit)
            throw new InvalidDataException("Plan regression result exceeded its target or window bounds.");
        decimal? previousRatio = null;
        var seen = new HashSet<QueryOpaqueIdentity>();
        foreach (QueryStorePlanRegression item in result.Items)
        {
            if (!seen.Add(item.Query) ||
                item.PreviousPlanFingerprint == item.CurrentPlanFingerprint ||
                !ValidNumber(item.PreviousAverageMilliseconds, out decimal previous) || previous <= 0 ||
                !ValidNumber(item.CurrentAverageMilliseconds, out decimal current) ||
                !ValidNumber(item.SlowdownRatio, out decimal ratio) || ratio < 2 ||
                !ValidNumber(item.PreviousExecutions, out decimal previousExecutions) || previousExecutions < 10 ||
                !ValidNumber(item.CurrentExecutions, out decimal currentExecutions) || currentExecutions < 10 ||
                current + 0.02m < previous * 2 || current - previous < 49.98m ||
                item.PreviousComparableRows < 2 || item.CurrentComparableRows < 2 ||
                item.PreviousSourceRows < item.PreviousComparableRows ||
                item.CurrentSourceRows < item.CurrentComparableRows ||
                item.LastObservedAtUtc < request.FromUtc || item.LastObservedAtUtc >= request.ToUtc ||
                previousRatio < ratio)
                throw new InvalidDataException("Plan regression evidence or order was invalid.");
            previousRatio = ratio;
        }
        return result;
    }

    private static bool ValidNumber(string value, out decimal number) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out number) && number >= 0;
}
