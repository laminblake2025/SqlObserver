using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/overview", ReadAsync)
            .RequireAuthorization()
            .WithRequestTimeout(TimeSpan.FromSeconds(35));
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, IOverviewQueryService service,
        WindowsGroupRoleResolver resolver, DateTimeOffset fromUtc, DateTimeOffset toUtc, Guid? targetId = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var snapshot = await service.ReadAsync(new(resolver.Resolve(context.User), targetId, fromUtc, toUtc), deadline.Token);
            // One envelope keeps summary, rankings and trends together; no shared cross-user cache.
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > 1_048_576) return Results.StatusCode(413);
            return Results.Ok(snapshot);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (OperationCanceledException) { return Results.StatusCode(504); }
        catch (Exception) { return Results.StatusCode(503); }
    }
}
