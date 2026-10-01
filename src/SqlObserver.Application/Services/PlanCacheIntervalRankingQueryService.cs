using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Services;

public interface IPlanCacheIntervalRankingQueryService
{
    ValueTask<PlanCacheIntervalRankingResult> ReadAsync(AuthorizationContext authorization,
        PlanCacheIntervalRankingRequest request, CancellationToken cancellationToken);
}

public sealed class PlanCacheIntervalRankingQueryService(IPlanCacheIntervalRankingRepositoryPort repository)
    : IPlanCacheIntervalRankingQueryService
{
    public async ValueTask<PlanCacheIntervalRankingResult> ReadAsync(AuthorizationContext authorization,
        PlanCacheIntervalRankingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer, ApplicationRole.Operator,
            ApplicationRole.TargetAdministrator);
        var result = await repository.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.TargetId != request.TargetId.Value || result.FromUtc != request.FromUtc ||
            result.ToUtc != request.ToUtc || result.Semantics != "plan_cache_delta" || result.Metric != request.MetricName ||
            result.SourceFrames < 0 || result.IncompleteFrames < 0 || result.IncompleteFrames > result.SourceFrames ||
            result.ComparableRows < 0 || result.UnavailableRows < 0 || result.BoundaryRows < 0 ||
            (decimal)result.ComparableRows + result.UnavailableRows + result.BoundaryRows > result.SourceFrames * 2000m ||
            result.Items is null || result.Items.Count > request.Limit || result.HasMore && result.Items.Count != request.Limit ||
            result.Coverage != (result.ComparableRows == 0 ? "no_comparable_rows" : "observed_entries_only") ||
            (result.ComparableRows == 0) != (result.Items.Count == 0))
            throw new InvalidDataException("Cache ranking exceeded its scope or coverage bounds.");
        var seen = new HashSet<(int, string, string, string)>();
        decimal? previous = null; decimal included = 0;
        foreach (var row in result.Items)
        {
            if (row.DatabaseId is < 5 or > 32766 || !Digest(row.DatabaseIncarnation) || !Digest(row.QueryFingerprint) ||
                !Digest(row.PlanFingerprint) || !seen.Add((row.DatabaseId, row.DatabaseIncarnation, row.QueryFingerprint, row.PlanFingerprint)) ||
                row.ComparableRows < 1 || row.FirstObservedAtUtc.Offset != TimeSpan.Zero || row.LastObservedAtUtc.Offset != TimeSpan.Zero ||
                row.FirstObservedAtUtc < request.FromUtc || row.LastObservedAtUtc >= request.ToUtc || row.LastObservedAtUtc <= row.FirstObservedAtUtc)
                throw new InvalidDataException("Cache ranking row identity or interval was invalid.");
            decimal[] metrics = [Number(row.CpuMilliseconds), Number(row.DurationMilliseconds), Number(row.Executions),
                Number(row.LogicalReads), Number(row.Writes), Number(row.Rows)];
            decimal value = metrics[(int)request.Metric - 1];
            if (previous < value) throw new InvalidDataException("Cache ranking was not ordered by its metric.");
            if (metrics.Skip(2).Any(number => decimal.Truncate(number) != number))
                throw new InvalidDataException("Cache ranking counts were fractional.");
            previous = value; included += row.ComparableRows;
        }
        if (included > result.ComparableRows) throw new InvalidDataException("Cache ranking row coverage exceeded the window.");
        return result;
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static decimal Number(string? value) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out decimal number) && number >= 0 ? number :
        throw new InvalidDataException("Cache ranking metric was not an exact nonnegative decimal.");
}
