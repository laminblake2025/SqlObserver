using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Audit;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpHttpErrorEnvelopeRegressionTests
{
    private const string ActorSid = "S-1-5-21-1-2-3-100";
    private const string Secret = "must-not-disclose-transport-detail";
    private static readonly string[] EnvelopeProperties = ["error", "id", "jsonrpc"];
    private static readonly Guid Invocation = Guid.Parse("81000000-0000-4000-8000-000000000001");
    private static readonly Guid Target = Guid.Parse("81000000-0000-4000-8000-000000000002");
    private static readonly Guid Incident = Guid.Parse("81000000-0000-4000-8000-000000000003");
    private static readonly JsonElement Arguments = JsonSerializer.SerializeToElement(new
    {
        instanceId = Target, threadId = Incident, secret = Secret
    });

    public enum Failure { Repository, Invalid, Denied, Timeout, Cancelled }

    public static IEnumerable<object[]> ValidIds()
    {
        foreach (bool pending in new[] { false, true })
        foreach (string id in new[] { "42", "9223372036854775807", "\"client-\\\"correlation\"" })
            yield return [pending, id];
    }

    [Theory]
    [MemberData(nameof(ValidIds))]
    public async Task OversizeReplacementIsNumericJsonRpcErrorWithOriginalIdAndOneAudit(bool pending, string id)
    {
        BoundaryResult result = await InvokeAsync(Frame(id),
            context => context.Response.WriteAsync(new string('x', McpQueryBounds.MaximumResponseBytes + 1)), pending);

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        AssertEnvelope(result, id, "response_too_large", (int)McpErrorCode.InternalError);
        AssertIdentityAndSingleAudit(result, McpInvocationOutcome.Oversize, McpInvocationAuditReason.ResponseOversize, pending);
    }

    [Theory]
    [InlineData(Failure.Repository, 503, "request_failed")]
    [InlineData(Failure.Invalid, 400, "invalid_request")]
    [InlineData(Failure.Denied, 403, "forbidden")]
    [InlineData(Failure.Timeout, 503, "request_timed_out")]
    [InlineData(Failure.Cancelled, 408, "request_cancelled")]
    public async Task DeferredFailureRetainsSafeHttpStatusAndCorrelatedEnvelope(Failure failure, int status, string reason)
    {
        using var caller = new CancellationTokenSource();
        BoundaryResult result = await InvokeAsync(Frame("\"request-17\""), async context =>
        {
            await context.Response.WriteAsync(Secret);
            if (failure == Failure.Cancelled) caller.Cancel();
            throw ExceptionFor(failure);
        }, pending: true, requestAborted: caller.Token);

        Assert.Equal(status, result.Status);
        AssertEnvelope(result, "\"request-17\"", reason);
        (McpInvocationOutcome outcome, McpInvocationAuditReason auditReason) = Classification(failure);
        AssertIdentityAndSingleAudit(result, outcome, auditReason, pending: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredAuditFailureWithholdsResultAndDoesNotRetryAppend(bool pending)
    {
        BoundaryResult result = await InvokeAsync(Frame("73"), context => context.Response.WriteAsync(Secret), pending, auditFailure: true);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Status);
        AssertEnvelope(result, "73", "audit_unavailable", (int)McpErrorCode.InternalError);
        Assert.Equal(1, result.Audit.Attempts);
        Assert.Empty(result.Audit.Records);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task GeneratedErrorNeverReflectsMalformedOrMissingIds(string? id)
    {
        // This explicitly exercises a generated boundary error. It does not
        // redefine successful notification handling or make notifications reply.
        BoundaryResult result = await InvokeAsync(Frame(id),
            context => context.Response.WriteAsync(new string('x', McpQueryBounds.MaximumResponseBytes + 1)));

        AssertEnvelope(result, "null", "response_too_large", (int)McpErrorCode.InternalError);
        AssertIdentityAndSingleAudit(result, McpInvocationOutcome.Oversize, McpInvocationAuditReason.ResponseOversize, pending: false);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":17,")]
    [InlineData("[1,2,3]")]
    public async Task UncorrelatableMalformedFrameUsesNullIdOnBoundaryFailure(string body)
    {
        BoundaryResult result = await InvokeAsync(body, _ => throw new IOException(Secret));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Status);
        AssertEnvelope(result, "null", "request_failed");
        Assert.Equal(1, result.Audit.Attempts);
        Assert.Equal(McpInvocationOutcome.RepositoryFailure, Assert.Single(result.Audit.Records).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadTransportFailureUsesNullIdAndOnlyAuditsRecognizedInvocation(bool control)
    {
        string method = control ? "initialize" : "tools/call";
        await using var input = new FailingReadStream(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"id\":19,"));
        BoundaryResult result = await InvokeAsync(null, _ => throw new InvalidOperationException("SDK must not run"), requestStream: input);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Status);
        AssertEnvelope(result, "null", "request_failed");
        Assert.Equal(control ? 0 : 1, result.Audit.Attempts);
        Assert.Equal(control ? 0 : 1, result.Audit.Records.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestOverflowRemains413AndIsAuditedOnceBeforeSdk(bool knownLength)
    {
        string body = new('x', 65_537);
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
        BoundaryResult result = await InvokeAsync(knownLength ? body : null,
            _ => throw new InvalidOperationException("SDK must not run"), requestStream: input);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.Status);
        AssertEnvelope(result, "null", "invalid_request", (int)McpErrorCode.InvalidRequest);
        Assert.Equal(1, result.Audit.Attempts);
        Assert.Equal(McpInvocationOutcome.Invalid, Assert.Single(result.Audit.Records).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseTransportWriteFailureDoesNotCreateSecondAudit(bool safeFailure)
    {
        await using var output = new FailingWriteStream();
        BoundaryResult result = await InvokeAsync(Frame("25"), context => safeFailure
            ? Task.FromException(new IOException(Secret)) : context.Response.WriteAsync("{}"),
            pending: true, responseStream: output);

        Assert.Equal(1, result.Audit.Attempts);
        McpInvocationAuditRecord record = Assert.Single(result.Audit.Records);
        Assert.Equal(safeFailure ? McpInvocationOutcome.RepositoryFailure : McpInvocationOutcome.Succeeded, record.Outcome);
        Assert.Equal(safeFailure ? 0 : 2, record.ResponseBytes);
        Assert.True(output.WriteAttempts > 0);
        Assert.False(Assert.Single(result.Audit.CancellationObserved));
    }

    [Fact]
    public async Task LegacyHandledMarkerDoesNotCauseAnotherAuditOnFailure()
    {
        BoundaryResult result = await InvokeAsync(Frame("18"), context =>
        {
            context.Items[McpHttpAuditBoundaryMiddleware.HandledItemKey] = true;
            throw new IOException(Secret);
        });
        AssertEnvelope(result, "18", "request_failed");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Status);
        Assert.Equal(0, result.Audit.Attempts);
    }

    [Fact]
    public async Task SuccessfulControlNotificationStaysEmptyAndUnaudited()
    {
        BoundaryResult result = await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", context =>
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        });
        Assert.Equal(StatusCodes.Status202Accepted, result.Status);
        Assert.Empty(result.Body);
        Assert.Equal(0, result.Audit.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialClientReceivesValidBoundaryErrorsWithExistingHttpStatus(bool auditFailure)
    {
        // Real SDK initialization and HTTP transport, in-process TestServer;
        // no socket or target/repository access. The existing project's harness
        // is TestServer, not WebApplicationFactory.
        await using WebApplication app = await BuildHostAsync(auditFailure);
        using var capture = new CaptureHttpHandler(app.GetTestServer().CreateHandler());
        using var http = new HttpClient(capture, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(5)
        }, http, ownsHttpClient: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        Exception? error = await Record.ExceptionAsync(async () =>
            await client.CallToolAsync(new CallToolRequestParams { Name = "list_instances", Arguments = new Dictionary<string, JsonElement>() }, cancellationToken: deadline.Token));
        if (auditFailure)
        {
            // The pinned SDK retains HTTP failure semantics for 503, even when
            // its body is a valid JSON-RPC error. Do not change server status
            // solely to force the SDK to throw a different exception type.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, Assert.IsType<HttpRequestException>(error).StatusCode);
        }
        else Assert.Equal(McpErrorCode.InternalError, Assert.IsType<McpProtocolException>(error).ErrorCode);
        Assert.NotNull(error);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        var audit = app.Services.GetRequiredService<RecordingAudit>();
        CallExchange exchange = Assert.Single(capture.Calls);
        Assert.Equal(auditFailure ? 503 : 200, exchange.Status);
        AssertEnvelope(new BoundaryResult(audit, exchange.Status, exchange.ContentType, exchange.Body), exchange.RequestId,
            auditFailure ? "audit_unavailable" : "response_too_large", (int)McpErrorCode.InternalError);
        Assert.Equal(1, audit.Attempts);
        McpInvocationAuditRecord attempted = Assert.Single(audit.AttemptedRecords);
        Assert.Equal(ActorSid, attempted.Actor.Value);
        Assert.Equal("list_instances", attempted.Tool.Value);
        Assert.Equal(auditFailure ? McpInvocationOutcome.Succeeded : McpInvocationOutcome.Oversize, attempted.Outcome);
        if (auditFailure) Assert.Empty(audit.Records);
        else Assert.Equal(McpInvocationOutcome.Oversize, Assert.Single(audit.Records).Outcome);
    }

    private static string Frame(string? id) => "{\"jsonrpc\":\"2.0\"," +
        (id is null ? string.Empty : "\"id\":" + id + ",") +
        "\"method\":\"tools/call\",\"params\":{\"name\":\"get_incident_evidence\",\"arguments\":" + Arguments.GetRawText() + "}}";

    private static void AssertEnvelope(BoundaryResult result, string expectedId, string reason, int? expectedCode = null)
    {
        Assert.Equal("application/json", result.ContentType);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Body), 1, 1024);
        Assert.DoesNotContain(Secret, result.Body, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(result.Body);
        JsonElement envelope = document.RootElement;
        Assert.Equal(EnvelopeProperties, envelope.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("2.0", envelope.GetProperty("jsonrpc").GetString());
        using JsonDocument identity = JsonDocument.Parse(expectedId);
        Assert.True(JsonElement.DeepEquals(identity.RootElement, envelope.GetProperty("id")));
        JsonElement error = envelope.GetProperty("error");
        Assert.Equal(JsonValueKind.Number, error.GetProperty("code").ValueKind);
        Assert.True(error.GetProperty("code").TryGetInt32(out int code));
        if (expectedCode.HasValue) Assert.Equal(expectedCode.Value, code);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal(reason, error.GetProperty("data").GetProperty("reason").GetString());
        // SDK decoding supplements the JSON assertions. SDK number coercion
        // alone must not let a quoted numeric code pass the wire contract.
        Assert.IsType<JsonRpcError>(JsonSerializer.Deserialize<JsonRpcMessage>(result.Body, McpJsonUtilities.DefaultOptions));
    }

    private static void AssertIdentityAndSingleAudit(BoundaryResult result, McpInvocationOutcome outcome, McpInvocationAuditReason reason, bool pending)
    {
        Assert.Equal(1, result.Audit.Attempts);
        McpInvocationAuditRecord record = Assert.Single(result.Audit.Records);
        Assert.Equal(ActorSid, record.Actor.Value);
        Assert.Equal("get_incident_evidence", record.Tool.Value);
        Assert.Equal("mcp.tool.get_incident_evidence", record.Action.Value);
        Assert.Equal(Target, record.TargetId!.Value);
        Assert.Equal(Incident, record.IncidentId);
        Assert.Equal(McpParameterCanonicalizer.Digest(Arguments).ToString(), record.ParameterDigest.ToString());
        Assert.Equal(outcome, record.Outcome);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(0, record.ResponseBytes);
        Assert.False(Assert.Single(result.Audit.CancellationObserved));
        if (pending) Assert.Equal(Invocation, record.InvocationId);
        else Assert.NotEqual(Guid.Empty, record.InvocationId);
    }

    private static async Task<BoundaryResult> InvokeAsync(string? body, RequestDelegate next, bool pending = false, bool auditFailure = false,
        Stream? requestStream = null, Stream? responseStream = null, CancellationToken requestAborted = default)
    {
        var audit = new RecordingAudit { Fail = auditFailure };
        var services = new ServiceCollection();
        services.AddSingleton<IMcpInvocationAuditPort>(audit);
        services.AddSingleton<IMcpInvocationAuditService, McpInvocationAuditService>();
        using ServiceProvider provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider, User = Principal(), RequestAborted = requestAborted };
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        await using var requestMemory = new MemoryStream(body is null ? [] : Encoding.UTF8.GetBytes(body));
        context.Request.Body = requestStream ?? requestMemory;
        if (body is not null) context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        await using var outputMemory = new MemoryStream();
        context.Response.Body = responseStream ?? outputMemory;
        await new McpHttpAuditBoundaryMiddleware(async current =>
        {
            if (pending)
            {
                current.Items[McpHttpAuditBoundaryMiddleware.HandledItemKey] = true;
                current.Items[McpHttpAuditBoundaryMiddleware.PendingAuditItemKey] = new McpPendingAudit(
                    Invocation, ActorSid, "get_incident_evidence", McpParameterCanonicalizer.Digest(Arguments), Target, Incident,
                    McpInvocationOutcome.Succeeded, McpInvocationAuditReason.Completed, TimeSpan.FromMilliseconds(3));
            }
            await next(current);
        }).InvokeAsync(context);
        return new(audit, context.Response.StatusCode, context.Response.ContentType, Encoding.UTF8.GetString(outputMemory.ToArray()));
    }

    private static Exception ExceptionFor(Failure failure) => failure switch
    {
        Failure.Invalid => new ArgumentException(Secret), Failure.Denied => new UnauthorizedAccessException(Secret),
        Failure.Timeout => new TimeoutException(Secret), Failure.Cancelled => new OperationCanceledException(Secret),
        _ => new IOException(Secret)
    };

    private static (McpInvocationOutcome, McpInvocationAuditReason) Classification(Failure failure) => failure switch
    {
        Failure.Invalid => (McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest),
        Failure.Denied => (McpInvocationOutcome.Denied, McpInvocationAuditReason.AuthorizationDenied),
        Failure.Timeout => (McpInvocationOutcome.Timeout, McpInvocationAuditReason.Timeout),
        Failure.Cancelled => (McpInvocationOutcome.Cancelled, McpInvocationAuditReason.Cancelled),
        _ => (McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure)
    };

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity([
        new Claim(ClaimTypes.PrimarySid, ActorSid), new Claim(ClaimTypes.GroupSid, ActorSid)
    ], "draft-test"));

    private static async Task<WebApplication> BuildHostAsync(bool auditFailure)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSqlObserverMcp();
        builder.Services.AddSingleton(new McpCursorSigner(Encoding.UTF8.GetBytes("01234567890123456789012345678901")));
        builder.Services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(ActorSid), [ApplicationRole.Viewer], allTargets: true)
        ]));
        builder.Services.AddSingleton<IObservationTargetQueryService, EmptyTargets>();
        builder.Services.AddSingleton(new RecordingAudit { Fail = auditFailure });
        builder.Services.AddSingleton<IMcpInvocationAuditPort>(provider => provider.GetRequiredService<RecordingAudit>());
        WebApplication app = builder.Build();
        app.Use(async (context, next) => { context.User = Principal(); await next(context); });
        app.UseMiddleware<McpHttpAuditBoundaryMiddleware>();
        app.Use(async (context, next) =>
        {
            if (!context.Items.ContainsKey(McpHttpAuditBoundaryMiddleware.BoundaryActiveItemKey)) { await next(context); return; }
            // Invoke the actual application handler to establish the immutable
            // pending snapshot, then simulate a serializer/transport response
            // exceeding its cap. This intentionally bypasses only the final
            // SDK tools/call dispatch, not SDK client parsing or initialization.
            _ = await context.RequestServices.GetRequiredService<IMcpCallHandler>().ExecuteAsync(
                "list_instances", new Dictionary<string, JsonElement>(), context.User, context.RequestAborted);
            await context.Response.WriteAsync(auditFailure ? Secret : new string('x', McpQueryBounds.MaximumResponseBytes + 1));
        });
        app.MapSqlObserverMcp();
        await app.StartAsync();
        return app;
    }

    private sealed class EmptyTargets : IObservationTargetQueryService
    {
        public ValueTask<ObservationTargetPage> ListAsync(ListObservationTargetsQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ObservationTargetPage([], null));
    }

    private sealed record BoundaryResult(RecordingAudit Audit, int Status, string? ContentType, string Body);

    private sealed record CallExchange(string RequestId, int Status, string? ContentType, string Body);

    private sealed class CaptureHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public ConcurrentQueue<CallExchange> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? id = null;
            if (request.Content?.Headers.ContentType?.MediaType == "application/json")
            {
                using JsonDocument document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("method", out JsonElement method) && method.GetString() == "tools/call")
                    id = document.RootElement.GetProperty("id").GetRawText();
            }
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (id is not null)
                Calls.Enqueue(new CallExchange(id, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                    await response.Content.ReadAsStringAsync(cancellationToken)));
            return response;
        }
    }

    private sealed class RecordingAudit : IMcpInvocationAuditPort
    {
        private int _attempts;
        public bool Fail { get; init; }
        public int Attempts => Volatile.Read(ref _attempts);
        public ConcurrentQueue<McpInvocationAuditRecord> Records { get; } = new();
        public ConcurrentQueue<McpInvocationAuditRecord> AttemptedRecords { get; } = new();
        public ConcurrentQueue<bool> CancellationObserved { get; } = new();
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            AttemptedRecords.Enqueue(request.Record);
            CancellationObserved.Enqueue(cancellationToken.IsCancellationRequested);
            if (Fail) return ValueTask.FromException<McpInvocationAuditReceipt>(new IOException(Secret));
            Records.Enqueue(request.Record);
            return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class FailingReadStream(byte[] prefix) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == prefix.Length) return ValueTask.FromException<int>(new IOException(Secret));
            int count = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public int WriteAttempts { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            throw new IOException(Secret);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteAttempts++;
            throw new IOException(Secret);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            return ValueTask.FromException(new IOException(Secret));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteAttempts++;
            return Task.FromException(new IOException(Secret));
        }
    }
}
