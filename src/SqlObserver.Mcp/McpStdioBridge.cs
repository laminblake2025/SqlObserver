using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using SqlObserver.Domain.Deployment;

namespace SqlObserver.Mcp;

/// <summary>The only public entry point for the stdio proxy; SDK transport types stay in this adapter.</summary>
public static class McpStdioBridge
{
    // This is an immutable transport approval point.  It intentionally does
    // not derive identity from the mutable catalog object: a catalog change
    // must fail closed until this reviewed value is changed as policy.
    private const string ApprovedCurrentProtocol = "2026-07-28";
    private const string ApprovedDownlevelProtocol = "2025-11-25";
    private const string ApprovedServerVersion = "m11-2.2.0+catalog-3787BD8A9511035F08781766E684083EF18F0CB7047BBF8FD8D1B50B61418D0C";
    public static async Task<int> RunAsync(string endpointText, string[] args, CancellationToken cancellationToken = default)
    {
        if (!TryValidateEndpoint(endpointText, out Uri? endpoint))
        {
            Console.Error.WriteLine("SQLOBSERVER_MCP_ENDPOINT must be an HTTPS /mcp endpoint without userinfo, query, or fragment.");
            return 2;
        }
        Uri validatedEndpoint = endpoint!;

        using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        using var httpHandler = new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true };
        using var httpClient = new HttpClient(httpHandler) { BaseAddress = validatedEndpoint };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = validatedEndpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0
        }, httpClient, loggerFactory, ownsHttpClient: false);

        await using McpClient client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken).ConfigureAwait(false);
        IList<McpClientTool> remoteTools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        string[] expected = McpCatalog.Definitions.Select(static d => d.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray();
        string[] actual = remoteTools.Select(static t => t.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal) ||
            !HasApprovedIdentity(client.ServerInfo.Version, client.NegotiatedProtocolVersion))
        {
            Console.Error.WriteLine("MCP server catalog or digest mismatch; refusing to start.");
            return 3;
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<IMcpCallHandler>(new McpProxyCallHandler(client));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(McpCatalog.CreateTools());
        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public static bool TryValidateEndpoint(string endpointText, out Uri? endpoint)
        => McpEndpointValidator.TryValidate(endpointText, out endpoint);

    internal static bool HasApprovedIdentity(string? serverVersion, string? protocolVersion)
        => string.Equals(serverVersion, ApprovedServerVersion, StringComparison.Ordinal)
            && (string.Equals(protocolVersion, ApprovedCurrentProtocol, StringComparison.Ordinal)
                || string.Equals(protocolVersion, ApprovedDownlevelProtocol, StringComparison.Ordinal));
}
