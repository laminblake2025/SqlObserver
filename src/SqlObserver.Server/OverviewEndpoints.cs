using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/overview", ReadAsync).RequireAuthorization()
            .WithRequestTimeout(ServerRequestTimeouts.OverviewPolicyName);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, IOverviewQueryService service,
        WindowsGroupRoleResolver resolver, DateTimeOffset fromUtc, DateTimeOffset toUtc, Guid? targetId = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var snapshot = await service.ReadAsync(new(resolver.Resolve(context.User), targetId, fromUtc, toUtc), cancellationToken);
            // One envelope keeps summary, rankings and trends together; no shared cross-user cache.
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > 1_048_576) return Results.StatusCode(413);
            return Results.Ok(snapshot);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        // Inventory's internal budget can expire while the HTTP request is still active.
        // HTTP deadline and client-abort cancellation remain owned by the outer middleware.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }
}
