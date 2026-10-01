using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public sealed class SqlMetricBaselineQueryService(IAnalyticsRepositoryPort repository)
{
    public async ValueTask<SqlMetricBaselinePage> GetAsync(AuthorizationContext authorization, AnalyticsQueryRequest request,
        int catalogVersion, long generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        DateTimeOffset snapshot = request.SnapshotUtc ?? DateTimeOffset.UtcNow;
        if (catalogVersion != MetricCatalogV2.Version || generation < 1 || request.TargetRevision is not { Value: > 0 } ||
            !MetricCatalogV2.TryGet(request.MetricKey, out AnalyticsMetricDefinition? definition) || definition!.Source != AnalyticsMetricSource.SqlEngine ||
            request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero || request.ToUtc.TimeOfDay != TimeSpan.Zero ||
            request.ToUtc - request.FromUtc != TimeSpan.FromDays(28) || snapshot.Offset != TimeSpan.Zero || snapshot < request.ToUtc ||
            snapshot > DateTimeOffset.UtcNow.AddMinutes(1) || request.Limit != 168 || request.Dimensions is { Count: > 0 } ||
            request.DimensionsSha256 is not null && request.DimensionsSha256 != CanonicalDimensions.Sha256(null))
            throw new ArgumentException("A catalog-v2 SQL metric, target revision and exact 28-day UTC source window are required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        IReadOnlyList<SqlMetricBaselineResult> rows;
        try
        {
            rows = await repository.ReadSqlBaselinesAsync(request with { SnapshotUtc = snapshot,
                Timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(5)) }, generation, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("SQL baseline read deadline expired."); }
        if (rows is null || rows.Count is not (0 or 168)) throw new InvalidDataException("SQL baseline hours are incomplete.");
        foreach (SqlMetricBaselineResult row in rows)
        {
            try { row.Validate(); }
            catch (ArgumentException exception) { throw new InvalidDataException("SQL baseline response statistics are invalid.", exception); }
            BaselineResult statistics = row.Statistics;
            if (statistics.MetricKey != request.MetricKey || statistics.WindowStartUtc != request.FromUtc || statistics.WindowEndUtc != request.ToUtc ||
                statistics.Generation != generation || row.SourceCutoffUtc > snapshot)
                throw new InvalidDataException("SQL baseline response identity differs from the request.");
        }
        if (rows.Select(row => row.Statistics.HourOfWeek).Distinct().Count() != rows.Count || rows.Select(row => row.SourceCutoffUtc).Distinct().Count() > 1)
            throw new InvalidDataException("SQL baseline response mixes hours or snapshots.");
        string state = rows.Count == 0 ? "no_data" : rows.All(row => row.Statistics.VisibilityState == "unavailable") ? "unavailable" :
            rows.All(row => row.Statistics.VisibilityState == "complete") ? "complete" : "partial";
        return new SqlMetricBaselinePage(request.TargetId.Value, request.TargetRevision!.Value, request.MetricKey, definition.Metric.Unit,
            request.FromUtc, request.ToUtc, snapshot, generation, rows, state);
    }
}
