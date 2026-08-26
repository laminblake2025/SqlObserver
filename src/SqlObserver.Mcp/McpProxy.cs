using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SqlObserver.Mcp;

/// <summary>Forwards calls to the authenticated Server without opening any local data path.</summary>
public sealed class McpProxyCallHandler(McpClient client) : IMcpCallHandler
{
    private readonly McpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public ValueTask<CallToolResult> ExecuteAsync(string toolName, IDictionary<string, JsonElement>? arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        _ = user; // Server authentication and RBAC are authoritative.
        Dictionary<string, JsonElement> forwarded = arguments is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);
        return _client.CallToolAsync(new CallToolRequestParams { Name = toolName, Arguments = forwarded }, cancellationToken);
    }
}
