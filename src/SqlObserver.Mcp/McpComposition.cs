using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.AspNetCore;
using SqlObserver.Audit;
using SqlObserver.Application.Ports;

namespace SqlObserver.Mcp;

/// <summary>Composes the official SDK while keeping SDK types inside the MCP adapter.</summary>
public static class McpComposition
{
    public static IServiceCollection AddSqlObserverMcp(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        // Resolve configuration after host/test registrations are complete. An
        // explicitly configured malformed key must not fall back to another key.
        services.AddSingleton(provider => McpCursorSigningConfiguration.Read(configuration ?? provider.GetService<IConfiguration>()));
        services.TryAddSingleton(provider => provider.GetRequiredService<McpCursorSigningConfiguration>().Signer!);
        services.AddHostedService<McpCursorStartupDiagnostics>();
        services.AddSingleton<IMcpCallHandler, McpCallHandler>();
        services.AddSingleton<IMcpInvocationAuditService, McpInvocationAuditService>();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "SqlObserver.Server",
                    Version = McpCatalog.ServerVersion
                };
                options.ServerInstructions = "Read-only SQL Observer diagnostics; all calls are authenticated, authorized, bounded, and audited.";
            })
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithTools(McpCatalog.CreateTools());
        return services;
    }

    public static IEndpointConventionBuilder MapSqlObserverMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapMcp("/mcp");
    }

}
