using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
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
    private const string ApprovedServerVersion = "m11-2.2.0+catalog-984319BD896C532E6E4B942334318FF5AB00E768677824023CCCEA180BF1DC2C";
    public static async Task<int> RunAsync(string endpointText, string[] args, CancellationToken cancellationToken = default)
    {
        using var httpHandler = new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true };
        return await RunCoreAsync(endpointText, args, httpHandler, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, cancellationToken).ConfigureAwait(false);
    }

    // The caller owns the HTTP handler; the SDK owns the session streams for
    // the lifetime of this invocation. This seam also exercises the real bridge
    // over in-process HTTP and pipe streams without replacing global Console.
    internal static async Task<int> RunCoreAsync(string endpointText, string[] args, HttpMessageHandler httpHandler,
        Stream input, Stream output, TextWriter diagnostics, CancellationToken cancellationToken = default)
    {
        if (!TryValidateEndpoint(endpointText, out Uri? endpoint))
        {
            await diagnostics.WriteLineAsync("SQLOBSERVER_MCP_ENDPOINT must be an HTTPS /mcp endpoint without userinfo, query, or fragment.").ConfigureAwait(false);
            return 2;
        }
        Uri validatedEndpoint = endpoint!;

        // Startup failures are reported once through the diagnostic writer.
        // SDK exception logs must not add provider details or stack traces.
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        using var httpClient = new HttpClient(httpHandler, disposeHandler: false) { BaseAddress = validatedEndpoint };
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = validatedEndpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0
        }, httpClient, loggerFactory, ownsHttpClient: false);

        McpClient? client = null;
        try
        {
            try
            {
                // The transport's connection timeout only covers connection
                // setup. Bound discovery, fallback, and catalog retrieval as
                // one operation, then release this deadline before proxying.
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(10));
                client = await McpClient.CreateAsync(transport,
                    new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(10) },
                    loggerFactory, startup.Token).ConfigureAwait(false);
                IList<McpClientTool> remoteTools = await client.ListToolsAsync(cancellationToken: startup.Token).ConfigureAwait(false);
                string[] expected = McpCatalog.Definitions.Select(static d => d.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray();
                string[] actual = remoteTools.Select(static t => t.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray();
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal) ||
                    !HasApprovedRemoteIdentity(client))
                {
                    await diagnostics.WriteLineAsync("MCP server catalog or digest mismatch; refusing to start.").ConfigureAwait(false);
                    return 3;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or IOException or McpException or TimeoutException or OperationCanceledException)
            {
                await diagnostics.WriteLineAsync("MCP server connection failed; verify the endpoint, service, and Windows access.").ConfigureAwait(false);
                return 4;
            }

            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
            builder.Services.AddSingleton<IMcpCallHandler>(new McpProxyCallHandler(client));
            builder.Services.AddMcpServer(options =>
                {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "SqlObserver.McpStdio",
                        Version = ApprovedServerVersion
                    };
                    options.ServerInstructions = McpCatalogDescriptions.ServerInstructions;
                })
                .WithStreamServerTransport(input, output).WithTools(McpCatalog.CreateTools());
            await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static bool TryValidateEndpoint(string endpointText, out Uri? endpoint)
        => McpEndpointValidator.TryValidate(endpointText, out endpoint);

    private static bool HasApprovedRemoteIdentity(McpClient client)
    {
        try { return HasApprovedIdentity(client.ServerInfo.Version, client.NegotiatedProtocolVersion); }
        catch (InvalidOperationException)
        {
            // Discovery permits omitting server identity. The SDK then throws
            // from ServerInfo; this bridge requires an approved identity.
            return false;
        }
    }

    internal static bool HasApprovedIdentity(string? serverVersion, string? protocolVersion)
        => string.Equals(serverVersion, ApprovedServerVersion, StringComparison.Ordinal)
            && (string.Equals(protocolVersion, ApprovedCurrentProtocol, StringComparison.Ordinal)
                || string.Equals(protocolVersion, ApprovedDownlevelProtocol, StringComparison.Ordinal));
}
