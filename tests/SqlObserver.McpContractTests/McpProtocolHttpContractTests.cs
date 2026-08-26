using System.Net;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlObserver.Mcp;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Audit;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpProtocolHttpContractTests
{
    [Fact]
    public async Task UnauthenticatedToolsCallGetsOneDeniedTerminalAudit()
    {
        await using WebApplication app = BuildHost();
        using HttpClient http = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_instance_health\",\"arguments\":{}}}", Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        RecordingAuditPort audit = app.Services.GetRequiredService<RecordingAuditPort>();
        Assert.Equal(McpInvocationOutcome.Denied, Assert.Single(audit.Records).Outcome);
    }

    [Fact]
    public async Task AuthenticatedUnknownToolGetsOneUnknownTerminalAuditWithoutBodyDisclosure()
    {
        await using WebApplication app = BuildHost();
        using HttpClient http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Auth", "allow");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"not_allowed\",\"arguments\":{\"secret\":\"must-not-return\"}}}", Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("must-not-return", body, StringComparison.Ordinal);
        RecordingAuditPort audit = app.Services.GetRequiredService<RecordingAuditPort>();
        Assert.Equal(McpInvocationOutcome.UnknownTool, Assert.Single(audit.Records).Outcome);
    }

    [Fact]
    public async Task DirectInvocationAuditRetainsCanonicalArgumentsAndResourceIdentity()
    {
        const string target = "11111111-1111-4111-8111-111111111111";
        const string incident = "22222222-2222-4222-8222-222222222222";
        async Task<McpInvocationAuditRecord> SendAsync(string arguments)
        {
            return await InvokeBoundaryAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"not_allowed\",\"arguments\":" + arguments + "}}");
        }

        McpInvocationAuditRecord record = await SendAsync("{\"threadId\":\"" + incident + "\",\"instanceId\":\"" + target + "\",\"value\":1}");
        Assert.Equal(Guid.Parse(target), record.TargetId!.Value);
        Assert.Equal(Guid.Parse(incident), record.IncidentId);
        Assert.NotEqual("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", record.ParameterDigest.ToString());
    }

    [Fact]
    public async Task DownstreamExceptionIsAuditedOnceAndResponseIsWithheld()
    {
        BoundaryResult result = await InvokeBoundaryRawAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_instance_health\",\"arguments\":{\"instanceId\":\"11111111-1111-4111-8111-111111111111\"}}}",
            async context =>
            {
                await context.Response.WriteAsync("sensitive downstream result");
                throw new InvalidOperationException("downstream");
            });

        Assert.Equal(McpInvocationOutcome.RepositoryFailure, Assert.Single(result.Records).Outcome);
        Assert.DoesNotContain("sensitive downstream result", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BodyTransportFailureIsAuditedOnce()
    {
        BoundaryResult result = await InvokeBoundaryRawAsync(null, _ => Task.CompletedTask, new ThrowingReadStream(new IOException("transport"), "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\"}"u8.ToArray()));
        Assert.Equal(McpInvocationOutcome.RepositoryFailure, Assert.Single(result.Records).Outcome);
    }

    [Fact]
    public async Task BodyCancellationIsAuditedOnce()
    {
        using var cancellation = new CancellationTokenSource();
        BoundaryResult result = await InvokeBoundaryRawAsync(null, _ => Task.CompletedTask, new ThrowingReadStream(new OperationCanceledException(), "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\"}"u8.ToArray(), cancellation.Cancel), requestAborted: cancellation.Token);
        Assert.Equal(McpInvocationOutcome.Cancelled, Assert.Single(result.Records).Outcome);
    }

    [Fact]
    public async Task OversizedBodyIsRejectedBeforeSdkAndAuditedOnce()
    {
        BoundaryResult result = await InvokeBoundaryRawAsync(
            new string('x', 65_537),
            _ => throw new InvalidOperationException("SDK must not receive an oversized body"));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
        Assert.Equal(McpInvocationOutcome.Invalid, Assert.Single(result.Records).Outcome);
    }

    [Fact]
    public async Task PartialControlOrAmbiguousBodyFailureIsNotToolAudited()
    {
        foreach (string prefix in new[]
        {
            "{\"jsonrpc\":\"2.0\",\"method\":\"initialize\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"metho"
        })
        {
            BoundaryResult result = await InvokeBoundaryRawAsync(null, _ => throw new InvalidOperationException("must not receive incomplete control frame"), new ThrowingReadStream(new IOException("transport"), Encoding.UTF8.GetBytes(prefix)));
            Assert.Empty(result.Records);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_instance_health\",\"arguments\":{}}}]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task ValidNonObjectJsonRootIsAuditedAsOneInvalidInvocation(string body)
    {
        BoundaryResult result = await InvokeBoundaryRawAsync(body, context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });
        McpInvocationAuditRecord record = Assert.Single(result.Records);
        Assert.Equal(McpInvocationOutcome.Invalid, record.Outcome);
        Assert.InRange(result.Body.Length, 0, 1024);
    }

    [Fact]
    public async Task AuditFailureWithholdsDownstreamResponse()
    {
        BoundaryResult result = await InvokeBoundaryRawAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_instance_health\",\"arguments\":{}}}",
            context => context.Response.WriteAsync("sensitive result"), auditFailure: true);
        Assert.Empty(result.Records);
        Assert.DoesNotContain("sensitive result", result.Body, StringComparison.Ordinal);
        Assert.Contains("audit_unavailable", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturedWireResponseCeilingProducesBoundedOversizeAudit()
    {
        BoundaryResult result = await InvokeBoundaryRawAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_instance_health\",\"arguments\":{}}}",
            context => context.Response.WriteAsync(new string('x', McpQueryBounds.MaximumResponseBytes + 1)));
        McpInvocationAuditRecord record = Assert.Single(result.Records);
        Assert.Equal(McpInvocationOutcome.Oversize, record.Outcome);
        Assert.Equal(0, record.ResponseBytes);
        Assert.InRange(result.Body.Length, 1, 256);
        Assert.Contains("response_too_large", result.Body, StringComparison.Ordinal);
    }

    private static async Task<McpInvocationAuditRecord> InvokeBoundaryAsync(string body)
    {
        var services = new ServiceCollection();
        var audit = new RecordingAuditPort();
        services.AddSingleton<IMcpInvocationAuditPort>(audit);
        services.AddSingleton<IMcpInvocationAuditService, McpInvocationAuditService>();
        using ServiceProvider provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        var boundary = new McpHttpAuditBoundaryMiddleware(httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });
        await boundary.InvokeAsync(context);
        return Assert.Single(audit.Records);
    }

    private static async Task<BoundaryResult> InvokeBoundaryRawAsync(string? body, RequestDelegate next, Stream? requestBody = null, bool auditFailure = false, CancellationToken requestAborted = default)
    {
        var services = new ServiceCollection();
        var audit = new RecordingAuditPort();
        if (auditFailure) audit.Failure = new InvalidOperationException("audit transport");
        services.AddSingleton<IMcpInvocationAuditPort>(audit);
        services.AddSingleton<IMcpInvocationAuditService, McpInvocationAuditService>();
        using ServiceProvider provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider, RequestAborted = requestAborted };
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        if (body is not null)
        {
            context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }
        else context.Request.Body = requestBody!;
        var response = new MemoryStream();
        context.Response.Body = response;
        await new McpHttpAuditBoundaryMiddleware(next).InvokeAsync(context);
        return new BoundaryResult(audit.Records.ToArray(), context.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
    }

    private sealed record BoundaryResult(McpInvocationAuditRecord[] Records, int StatusCode, string Body);

    private sealed class ThrowingReadStream(Exception failure, byte[]? prefix = null, Action? afterPrefix = null) : Stream
    {
        private readonly byte[] _prefix = prefix ?? [];
        private readonly Action? _afterPrefix = afterPrefix;
        private bool _prefixServed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_prefixServed) { _prefixServed = true; _prefix.CopyTo(buffer, offset); return _prefix.Length; }
            _afterPrefix?.Invoke();
            throw failure;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_prefixServed) { _prefixServed = true; _prefix.CopyTo(buffer, offset); return Task.FromResult(_prefix.Length); }
            _afterPrefix?.Invoke();
            return Task.FromException<int>(failure);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefixServed) { _prefixServed = true; _prefix.AsSpan().CopyTo(buffer.Span); return ValueTask.FromResult(_prefix.Length); }
            _afterPrefix?.Invoke();
            return ValueTask.FromException<int>(failure);
        }
    }

    [Fact]
    public async Task MalformedJsonRpcGetsOneInvalidTerminalAudit()
    {
        await using WebApplication app = BuildHost();
        using HttpClient http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Auth", "allow");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{malformed", Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await http.SendAsync(request);
        RecordingAuditPort audit = app.Services.GetRequiredService<RecordingAuditPort>();
        Assert.Equal(McpInvocationOutcome.Invalid, Assert.Single(audit.Records).Outcome);
    }

    [Fact]
    public async Task UnauthenticatedMcpIsRejectedAndOfficialClientListsExactCatalog()
    {
        await using WebApplication app = BuildHost();
        using HttpClient unauthenticated = app.GetTestClient();
        HttpResponseMessage denied = await unauthenticated.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using HttpClient http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Auth", "allow");
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport);
        IList<McpClientTool> tools = await client.ListToolsAsync();
        Assert.Equal(McpCatalog.Definitions.Select(static x => x.Name).OrderBy(static x => x), tools.Select(static x => x.Name).OrderBy(static x => x));
        CallToolResult call = await client.CallToolAsync(new CallToolRequestParams { Name = "list_instances", Arguments = new Dictionary<string, System.Text.Json.JsonElement>() });
        Assert.NotEqual(true, call.IsError);
        Assert.True(call.StructuredContent.HasValue);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, call.StructuredContent.Value.ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, call.StructuredContent.Value.GetProperty("data").ValueKind);
        RecordingAuditPort audit = app.Services.GetRequiredService<RecordingAuditPort>();
        Assert.Equal(McpInvocationOutcome.Succeeded, Assert.Single(audit.Records).Outcome);
    }

    [Fact]
    public async Task DownlevelInitializeTranscriptIsAcceptedAndAdvertisesCatalog()
    {
        await using WebApplication app = BuildHost();
        using HttpClient http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Auth", "allow");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"contract\",\"version\":\"1\"}}}", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("2025-11-25", body, StringComparison.Ordinal);
        Assert.Contains("tools", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfficialClientRoundTripsSignedCursorThroughAuthenticatedRealHandler()
    {
        await using WebApplication app = BuildHost();
        using HttpClient http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Auth", "allow");
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(5)
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport);
        var first = await client.CallToolAsync(new CallToolRequestParams { Name = "list_instances", Arguments = new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(1) } });
        Assert.False(first.IsError, string.Join(" | ", first.Content.OfType<TextContentBlock>().Select(x => x.Text)));
        string cursor = first.StructuredContent!.Value.GetProperty("data").GetProperty("nextCursor").GetString()!;
        Assert.InRange(cursor.Length, 1, McpCursorSigner.MaximumTokenLength);
        var secondArgs = new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(1), ["cursor"] = JsonSerializer.SerializeToElement(cursor) };
        var second = await client.CallToolAsync(new CallToolRequestParams { Name = "list_instances", Arguments = secondArgs });
        Assert.NotEqual(true, second.IsError);
        Assert.Equal("second", second.StructuredContent!.Value.GetProperty("data").GetProperty("targets")[0].GetProperty("displayName").GetString());
        Assert.False(second.StructuredContent.Value.GetProperty("data").TryGetProperty("nextCursor", out _));
    }

    private static WebApplication BuildHost()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(new Action<AuthorizationOptions>(options => options.FallbackPolicy = new AuthorizationPolicyBuilder("test").RequireAuthenticatedUser().Build()));
        builder.Services.AddSqlObserverMcp();
        builder.Services.AddSingleton(new McpCursorSigner(Encoding.UTF8.GetBytes("01234567890123456789012345678901")));
        builder.Services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1-2-3-100"), [ApplicationRole.Viewer], allTargets: true)
        ]));
        builder.Services.AddSingleton<IObservationTargetQueryService, PagingTargetQueryService>();
        builder.Services.AddSingleton<RecordingAuditPort>();
        builder.Services.AddSingleton<IMcpInvocationAuditPort>(services => services.GetRequiredService<RecordingAuditPort>());
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<McpHttpAuditBoundaryMiddleware>();
        app.UseAuthorization();
        app.MapSqlObserverMcp().RequireAuthorization();
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }

    private sealed class PagingTargetQueryService : IObservationTargetQueryService
    {
        private static readonly MonitoredInstanceId FirstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        private static readonly MonitoredInstanceId SecondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        private static readonly ObservationTarget First = Make(FirstId, "first");
        private static readonly ObservationTarget Second = Make(SecondId, "second");
        public ValueTask<ObservationTargetPage> ListAsync(ListObservationTargetsQuery query, CancellationToken cancellationToken)
        {
            if (query.Cursor is null) return ValueTask.FromResult(new ObservationTargetPage([First], new ObservationTargetListCursor(First.Key, First.TargetId)));
            return ValueTask.FromResult(new ObservationTargetPage([Second], null));
        }
        private static ObservationTarget Make(MonitoredInstanceId id, string name) => new(id, new ObservationTargetKey(name), new ObservationTargetDisplayName(name), new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))), ObservationTargetLifecycle.Active, new ObservationTargetRevision(1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingAuditPort : IMcpInvocationAuditPort
    {
        public ConcurrentQueue<McpInvocationAuditRecord> Records { get; } = new();
        public Exception? Failure { get; set; }
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        {
            if (Failure is not null) return ValueTask.FromException<McpInvocationAuditReceipt>(Failure);
            return Append(request);
        }

        private ValueTask<McpInvocationAuditReceipt> Append(AppendMcpInvocationAuditRequest request)
        {
            Records.Enqueue(request.Record);
            return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.ContainsKey("X-Test-Auth")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.Name, "contract"),
                    new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-100"),
                    new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-100")
                ], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
