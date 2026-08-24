using Microsoft.AspNetCore.Routing;

namespace SqlObserver.Server;

/// <summary>Defines the intentionally small HTTP surface available during scaffolding.</summary>
public static class ScaffoldEndpoints
{
    /// <summary>Maps root metadata and a process-health response.</summary>
    public static IEndpointRouteBuilder MapSqlObserverScaffoldEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/",
            static () => Results.Ok(new ServiceDescriptor("SqlObserver", "scaffold")));
        endpoints.MapGet(
            "/health",
            static () => Results.Ok(new HealthDescriptor("alive")));

        return endpoints;
    }

    /// <summary>Describes the scaffold service.</summary>
    public sealed record ServiceDescriptor(string Name, string Status);

    /// <summary>Describes process liveness only; it makes no dependency or target-health claim.</summary>
    public sealed record HealthDescriptor(string Status);
}
