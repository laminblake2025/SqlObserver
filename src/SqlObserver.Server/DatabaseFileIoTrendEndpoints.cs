using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class DatabaseFileIoTrendEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseFileIoTrendEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/observation-targets/{instanceId:guid}/resources/files/{databaseId:int}/{fileId:int}/trend",
            ReadAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId,
        int databaseId, int fileId, IDatabaseFileIoTrendQueryService service,
        WindowsGroupRoleResolver roles, string? fromUtc = null, string? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            DateTimeOffset to = Parse(toUtc) ?? DateTimeOffset.UtcNow;
            DateTimeOffset from = Parse(fromUtc) ?? to.AddHours(-24);
            var request = new DatabaseFileIoTrendRequest(
                new MonitoredInstanceId(instanceId), databaseId, fileId, from, to,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
            DatabaseFileIoTrendResult result = await service.ReadAsync(
                roles.Resolve(context.User), request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                targetId = instanceId,
                databaseId,
                fileId,
                fromUtc = from,
                toUtc = to,
                semantics = "file_counter_delta",
                revisionScope = "current",
                bucketSeconds = 3600,
                points = result.Points,
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
