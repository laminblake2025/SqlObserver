using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public sealed record MetricCatalogItem(
    string MetricKey, string DisplayName, string Unit, string Source,
    string Aggregation, IReadOnlyList<string> DimensionKeys);

public sealed record MetricCatalogProjection(
    int Version, string Checksum, IReadOnlyList<MetricCatalogItem> Items);

/// <summary>Authorized discovery of embedded definitions, without instance data.</summary>
public static class MetricCatalogQueryService
{
    public static MetricCatalogProjection Get(AuthorizationContext authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!(authorization.HasRole(ApplicationRole.Viewer)
            || authorization.HasRole(ApplicationRole.Operator)
            || authorization.HasRole(ApplicationRole.TargetAdministrator)))
            throw new UnauthorizedAccessException("The caller is not authorized to read metric definitions.");

        // Source and aggregation match the versioned catalog's checksum inputs.
        MetricCatalogItem[] items = MetricCatalogV1.All.Where(static entry => entry.Enabled)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => new MetricCatalogItem(
                entry.Key, entry.DisplayName, entry.Unit,
                entry.Key.StartsWith("replication.", StringComparison.Ordinal) ? "replication" : "host",
                entry.Kind == MetricKind.Counter ? "sum" : "gauge",
                Array.AsReadOnly(entry.DimensionAllowlist.Order(StringComparer.Ordinal).ToArray())))
            .ToArray();
        return new MetricCatalogProjection(MetricCatalogV1.Version, MetricCatalogV1.Checksum, Array.AsReadOnly(items));
    }
}
