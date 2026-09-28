using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class BufferNodePleWindowEndpoints
{
    public static IEndpointRouteBuilder MapBufferNodePleWindowEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/observation-targets/{instanceId:guid}/resources/buffer-nodes/ple",
            ReadAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId,
        IBufferNodePleWindowQueryService service, WindowsGroupRoleResolver roles,
        int? nodeId = null, string? fromUtc = null, string? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            DateTimeOffset to = Parse(toUtc) ?? DateTimeOffset.UtcNow;
            DateTimeOffset from = Parse(fromUtc) ?? to.AddHours(-24);
            var request = new BufferNodePleWindowRequest(new MonitoredInstanceId(instanceId),
                nodeId, from, to, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
            BufferNodePleWindowResult result = await service.ReadAsync(
                roles.Resolve(context.User), request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                targetId = instanceId,
                fromUtc = from,
                toUtc = to,
                semantics = "numa_node_page_life_expectancy_gauge",
                unit = "seconds",
                revisionScope = "current",
                bucketSeconds = 3600,
                selectedNodeId = result.SelectedNodeId,
                visibilityGapCount = result.VisibilityGapCount,
                nodes = result.Nodes,
                points = result.Points,
                aggregatePoints = result.AggregatePoints,
            });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static DateTimeOffset? Parse(string? value) => value is null ? null :
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) &&
        parsed.Offset == TimeSpan.Zero ? parsed :
        throw new ArgumentException("A UTC timestamp is required.");
}
