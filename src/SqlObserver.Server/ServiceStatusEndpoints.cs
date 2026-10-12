using Microsoft.AspNetCore.Routing;
using SqlObserver.Application.Ports;
using SqlObserver.Observability;

namespace SqlObserver.Server;

/// <summary>Maps service identity, liveness, and repository readiness.</summary>
public static class ServiceStatusEndpoints
{
    public static IEndpointRouteBuilder MapSqlObserverServiceStatusEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool includeRootDescriptor = true)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (includeRootDescriptor)
        {
            endpoints.MapGet(
                "/",
                static () => Results.Ok(new ServiceDescriptor("SqlObserver", "running")));
        }

        endpoints.MapGet(
            "/api/v1/service",
            static () => Results.Ok(new ServiceDescriptor("SqlObserver", "running")));
        endpoints.MapGet(
            "/health",
            static () => Results.Ok(new HealthDescriptor("alive")));
        endpoints.MapGet(
            "/ready",
            static async (HttpContext context, IRepositoryReadinessMonitor monitor) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                RepositoryReadinessObservation observation = await monitor.CheckAsync(
                    new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), context.RequestAborted).ConfigureAwait(false);
                var descriptor = new ReadinessDescriptor(observation.IsReady ? "ready" : "not_ready");
                return Results.Json(descriptor, statusCode: observation.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            }).RequireAuthorization();

        return endpoints;
    }

    /// <summary>Describes the running HTTP service without asserting repository readiness.</summary>
    public sealed record ServiceDescriptor(string Name, string Status);

    /// <summary>Describes process liveness only; it makes no dependency or target-health claim.</summary>
    public sealed record HealthDescriptor(string Status);

    public sealed record ReadinessDescriptor(string Status);
}
