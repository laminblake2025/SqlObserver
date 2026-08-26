using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using SqlObserver.Audit;
using SqlObserver.Application.Ports;

namespace SqlObserver.Mcp;

/// <summary>Composes the official SDK while keeping SDK types inside the MCP adapter.</summary>
public static class McpComposition
{
    public static IServiceCollection AddSqlObserverMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        // Cursor signing is deliberately opt-in and server-owned. A missing or
        // malformed deployment key leaves cursor-bearing calls fail-closed.
        string? configuredKey = Environment.GetEnvironmentVariable("SQLOBSERVER_MCP_CURSOR_KEY");
        if (TryReadCursorKey(configuredKey, out byte[]? cursorKey)) services.AddSingleton(new McpCursorSigner(cursorKey!));
        services.AddSingleton<IMcpCallHandler, McpCallHandler>();
        services.AddSingleton<IMcpInvocationAuditService, McpInvocationAuditService>();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "SqlObserver.Server",
                    Version = "m11-2.2.0+catalog-" + McpCatalog.Digest
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

    private static bool TryReadCursorKey(string? value, out byte[]? key)
    {
        key = null;
        try
        {
            if (value is null) return false;
            byte[] decoded = Convert.FromBase64String(value);
            if (decoded.Length is < 32 or > 4096) return false;
            key = decoded;
            return true;
        }
        catch (FormatException) { return false; }
    }
}
