using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpStdioBridgeRegressionTests
{
    private const string Endpoint = "https://localhost/mcp";
    private const string Current = "2026-07-28";
    private const string Downlevel = "2025-11-25";

    [Theory]
    [InlineData(Current)]
    [InlineData(Downlevel)]
    public async Task ReleaseProtocolProbeMatchesThePinnedStatelessServer(string protocol)
    {
        await using WebApplication app = await BuildHostAsync();
        using HttpClient http = app.GetTestClient();
        await M12McpProtocolCertificationTests.AssertProtocolRevisionAsync(new Uri(Endpoint), http, protocol);
        Assert.Empty(app.Services.GetRequiredService<AuditPort>().Records);
    }

    [Theory]
    [InlineData(Current)]
    [InlineData(Downlevel)]
    public async Task ExactReleaseStdioTranscriptProducesOnlyTheExpectedFrames(string protocol)
    {
        await using WebApplication app = await BuildHostAsync();
        using HttpMessageHandler handler = app.GetTestServer().CreateHandler();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var captured = new MemoryStream();
        using var output = new CapturingWriteStream(Stream.Null, captured);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var incoming = new Pipe();
        Task<int> bridge = McpStdioBridge.RunCoreAsync(Endpoint, [], handler, incoming.Reader.AsStream(), output, diagnostics, deadline.Token);
        try
        {
            await incoming.Writer.WriteAsync(Encoding.UTF8.GetBytes(M12McpProtocolCertificationTests.CreateStdioProtocolTranscript(protocol)), deadline.Token);
            await output.TwoFrames.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await incoming.Writer.CompleteAsync();
            Assert.Equal(0, await bridge.WaitAsync(TimeSpan.FromSeconds(3)));
            M12McpProtocolCertificationTests.AssertStdioProtocolOutput(captured.ToArray(), protocol);
            Assert.Equal(string.Empty, diagnostics.ToString());
            Assert.Empty(app.Services.GetRequiredService<AuditPort>().Records);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await bridge.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("event: arbitrary\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n")]
    [InlineData("event: message\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}\n\n")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"result\":{}}\n\n")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32603}}\n\n")]
    public void ReleaseHttpParserRejectsUnexpectedEventsFramesAndEnvelopeShapes(string text)
        => Assert.ThrowsAny<Exception>(() => M12McpProtocolCertificationTests.ParseJsonRpcResponse(Encoding.UTF8.GetBytes(text)));

    [Theory]
    [InlineData(Current, null)]
    [InlineData(Downlevel, null)]
    [InlineData(Current, Downlevel)]
    [InlineData(Downlevel, Downlevel)]
    public async Task RealBridgeForwardsCallsAndExposesApprovedMetadataForBothProtocols(string downstreamProtocol, string? upstreamProtocol)
    {
        await using WebApplication app = await BuildHostAsync(upstreamProtocol);
        using var handler = new TraceHandler(app.GetTestServer().CreateHandler());
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var captured = new MemoryStream();
        var incoming = new Pipe();
        var outgoing = new Pipe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var output = new CapturingWriteStream(outgoing.Writer.AsStream(), captured);
        Task<int> bridge = McpStdioBridge.RunCoreAsync(Endpoint, [], handler, incoming.Reader.AsStream(), output, diagnostics, deadline.Token);
        try
        {
            await using (McpClient client = await McpClient.CreateAsync(
                new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ProtocolVersion = downstreamProtocol, InitializationTimeout = TimeSpan.FromSeconds(5) },
                cancellationToken: deadline.Token))
            {
                IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
                Assert.Equal(McpCatalog.Definitions.Select(x => x.Name).Order(), tools.Select(x => x.Name).Order());
                CallToolResult result = await client.CallToolAsync("list_instances", new Dictionary<string, object?> { ["limit"] = 1 }, cancellationToken: deadline.Token);
                Assert.False(result.IsError);
                JsonSchemaAssertions.AssertValid(result.StructuredContent!.Value, McpCatalog.OutputSchema("list_instances"), "bridge round trip");
                Assert.Equal("bridge target", result.StructuredContent.Value.GetProperty("data").GetProperty("targets")[0].GetProperty("displayName").GetString());
                Assert.Equal(McpInvocationOutcome.Succeeded, Assert.Single(app.Services.GetRequiredService<AuditPort>().Records).Outcome);
                Assert.Equal(downstreamProtocol, client.NegotiatedProtocolVersion);
                Assert.Equal("SqlObserver.McpStdio", client.ServerInfo.Name);
                Assert.Equal(McpCatalog.ServerVersion, client.ServerInfo.Version);
                Assert.Contains("Read-only", client.ServerInstructions, StringComparison.Ordinal);
                Assert.NotNull(client.ServerCapabilities.Tools);
            }
            // The client session does not own the supplied pipe. Complete the
            // writer explicitly to model the parent process closing stdin.
            await incoming.Writer.CompleteAsync();
            Assert.Equal(0, await bridge.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(string.Empty, diagnostics.ToString());
            AssertProtocolFrames(captured.ToArray());
            Assert.Contains(handler.Requests, x => x.GetProperty("method").GetString() == "server/discover");
            if (upstreamProtocol is null)
                Assert.DoesNotContain(handler.Requests, x => x.GetProperty("method").GetString() == "initialize");
            else
                Assert.Contains(handler.Requests, x => x.GetProperty("method").GetString() == "initialize");
        }
        finally
        {
            await deadline.CancelAsync();
            try { await bridge.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("catalog")]
    [InlineData("missing-identity")]
    public async Task IdentityMismatchDoesNotAdvertiseLocalTools(string mismatch)
    {
        await using WebApplication app = await BuildHostAsync(mismatch: mismatch);
        using HttpMessageHandler handler = app.GetTestServer().CreateHandler();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int code = await McpStdioBridge.RunCoreAsync(Endpoint, [], handler, Stream.Null, output, diagnostics, deadline.Token);
        Assert.Equal(3, code);
        Assert.Equal(0, output.Length);
        Assert.Equal("MCP server catalog or digest mismatch; refusing to start." + Environment.NewLine, diagnostics.ToString());
        Assert.Empty(app.Services.GetRequiredService<AuditPort>().Records);
    }

    [Fact]
    public async Task UnreachableUpstreamReturnsOneSafeLineAndNoProtocolOutput()
    {
        using var handler = new FaultingHandler();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        int code = await McpStdioBridge.RunCoreAsync(Endpoint, [], handler, Stream.Null, output, diagnostics);
        Assert.Equal(4, code);
        Assert.Equal(0, output.Length);
        Assert.Equal("MCP server connection failed; verify the endpoint, service, and Windows access." + Environment.NewLine, diagnostics.ToString());
        Assert.DoesNotContain("private-provider-detail", diagnostics.ToString(), StringComparison.Ordinal);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogFailureOrTimeoutAlsoStopsStartupCleanly(bool pending)
    {
        await using WebApplication app = await BuildHostAsync();
        using var handler = new CatalogFailureHandler(app.GetTestServer().CreateHandler(), pending);
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var cancellation = new CancellationTokenSource();
        Task<int> bridge = McpStdioBridge.RunCoreAsync(Endpoint, [], handler, Stream.Null, output, diagnostics, cancellation.Token);
        try
        {
            Assert.Equal(4, await bridge.WaitAsync(TimeSpan.FromSeconds(13)));
            Assert.True(handler.ReachedCatalog);
            Assert.Equal(pending, handler.Cancelled);
            Assert.Equal(0, output.Length);
            Assert.Equal("MCP server connection failed; verify the endpoint, service, and Windows access." + Environment.NewLine, diagnostics.ToString());
            Assert.Empty(app.Services.GetRequiredService<AuditPort>().Records);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await bridge.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task InvalidEndpointDoesNotContactUpstream()
    {
        using var handler = new FaultingHandler();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        int code = await McpStdioBridge.RunCoreAsync("http://localhost/mcp", [], handler, Stream.Null, output, diagnostics);
        Assert.Equal(2, code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, output.Length);
        Assert.StartsWith("SQLOBSERVER_MCP_ENDPOINT must be an HTTPS /mcp endpoint", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationStopsPendingStartupWithoutMisreportingFailure()
    {
        using var handler = new PendingHandler();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var cancellation = new CancellationTokenSource();
        Task<int> bridge = McpStdioBridge.RunCoreAsync(Endpoint, [], handler, Stream.Null, output, diagnostics, cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, output.Length);
        Assert.Equal(string.Empty, diagnostics.ToString());
        Assert.True(handler.Cancelled);
    }

    [Fact]
    public async Task EntireUpstreamStartupHasATenSecondBudget()
    {
        using var handler = new PendingHandler();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var cancellation = new CancellationTokenSource();
        Task<int> bridge = McpStdioBridge.RunCoreAsync(Endpoint, [], handler, Stream.Null, output, diagnostics, cancellation.Token);
        try
        {
            Assert.Equal(4, await bridge.WaitAsync(TimeSpan.FromSeconds(13)));
            Assert.Equal(0, output.Length);
            Assert.Equal("MCP server connection failed; verify the endpoint, service, and Windows access." + Environment.NewLine, diagnostics.ToString());
            Assert.True(handler.Cancelled);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await bridge.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    private static void AssertProtocolFrames(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        Assert.NotEmpty(text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        foreach (string line in text.TrimEnd('\n').Split('\n'))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement frame = document.RootElement;
            Assert.Equal("2.0", frame.GetProperty("jsonrpc").GetString());
            Assert.True(frame.TryGetProperty("result", out _) || frame.TryGetProperty("error", out _) || frame.TryGetProperty("method", out _));
        }
    }

    private static async Task<WebApplication> BuildHostAsync(string? protocol = null, string? mismatch = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSqlObserverMcp(builder.Configuration);
        builder.Services.Configure<McpServerOptions>(options =>
        {
            options.ProtocolVersion = protocol;
            if (mismatch == "version") options.ServerInfo = new Implementation { Name = "SqlObserver.Server", Version = "unapproved" };
        });
        builder.Services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1"), [ApplicationRole.Viewer], true)]));
        builder.Services.AddSingleton<IObservationTargetQueryService, Targets>();
        builder.Services.AddSingleton<AuditPort>();
        builder.Services.AddSingleton<IMcpInvocationAuditPort>(services => services.GetRequiredService<AuditPort>());
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<McpHttpAuditBoundaryMiddleware>();
        app.UseAuthorization();
        if (mismatch is "catalog" or "missing-identity")
        {
            // A valid discovery response still has the approved identity; only
            // the upstream tools/list shape is substituted for this control.
            app.Use(async (context, next) =>
            {
                context.Request.EnableBuffering();
                using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body);
                context.Request.Body.Position = 0;
                string? method = body.RootElement.GetProperty("method").GetString();
                if (mismatch == "catalog" && method == "tools/list")
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = body.RootElement.GetProperty("id").Clone(), result = new { tools = Array.Empty<object>() } });
                }
                else if (mismatch == "missing-identity" && method == "server/discover")
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = body.RootElement.GetProperty("id").Clone(), result = new { supportedVersions = new[] { Current }, capabilities = new { tools = new { } }, instructions = "Read-only test", ttlMs = 0, cacheScope = "private" } });
                }
                else await next(context);
            });
        }
        app.MapSqlObserverMcp().RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class Targets : IObservationTargetQueryService
    {
        private static readonly ObservationTarget Target = new(new MonitoredInstanceId(Guid.Parse("11111111-1111-4111-8111-111111111111")), new ObservationTargetKey("bridge"), new ObservationTargetDisplayName("bridge target"), new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))), ObservationTargetLifecycle.Active, new ObservationTargetRevision(1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        public ValueTask<ObservationTargetPage> ListAsync(ListObservationTargetsQuery query, CancellationToken cancellationToken) => ValueTask.FromResult(new ObservationTargetPage([Target], null));
    }

    private sealed class AuditPort : IMcpInvocationAuditPort
    {
        public ConcurrentQueue<McpInvocationAuditRecord> Records { get; } = new();
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        {
            Records.Enqueue(request.Record);
            return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class TraceHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                using JsonDocument content = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                Requests.Enqueue(content.RootElement.Clone());
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FaultingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("private-provider-detail");
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CatalogFailureHandler(HttpMessageHandler inner, bool pending) : DelegatingHandler(inner)
    {
        public bool ReachedCatalog { get; private set; }
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument content = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            if (content.RootElement.GetProperty("method").GetString() != "tools/list") return await base.SendAsync(request, cancellationToken);
            ReachedCatalog = true;
            if (pending)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            throw new HttpRequestException("private-catalog-detail");
        }
    }

    private sealed class PendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class CapturingWriteStream(Stream destination, Stream capture) : Stream
    {
        private int newlines;
        public TaskCompletionSource TwoFrames { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { Capture(buffer.AsSpan(offset, count)); destination.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Capture(buffer); destination.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            await destination.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) destination.Dispose(); base.Dispose(disposing); }
        private void Capture(ReadOnlySpan<byte> buffer)
        {
            capture.Write(buffer);
            newlines += buffer.Count((byte)'\n');
            if (newlines >= 2) TwoFrames.TrySetResult();
        }
    }
}
