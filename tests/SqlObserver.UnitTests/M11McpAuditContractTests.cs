using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Audit;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.UnitTests;

public sealed class M11McpAuditContractTests
{
    [Fact]
    public void CanonicalParameterDigestIgnoresObjectPropertyOrder()
    {
        using JsonDocument first = JsonDocument.Parse("{\"b\":2,\"a\":1}");
        using JsonDocument second = JsonDocument.Parse("{\"a\":1,\"b\":2}");

        Assert.Equal(
            McpParameterCanonicalizer.Digest(first.RootElement).ToString(),
            McpParameterCanonicalizer.Digest(second.RootElement).ToString());
    }

    [Fact]
    public async Task AuditFailureWithholdsSuccessfulResult()
    {
        var audit = new RecordingAudit { Failure = new InvalidOperationException("transport") };
        var service = new McpInvocationAuditService(audit);
        McpInvocationRequest request = CreateRequest();

        await Assert.ThrowsAsync<McpAuditUnavailableException>(() => service.ExecuteAsync(
            request,
            _ => ValueTask.FromResult("sensitive result"),
            value => value.Length,
            CancellationToken.None).AsTask());

        Assert.Equal(1, audit.Calls);
    }

    [Fact]
    public async Task CallerCancellationStillAppendsCancelledTerminalRecord()
    {
        var audit = new RecordingAudit();
        var service = new McpInvocationAuditService(audit);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ExecuteAsync(
            CreateRequest(),
            _ => ValueTask.FromResult("should not execute"),
            value => value.Length,
            caller.Token).AsTask());

        McpInvocationAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(McpInvocationOutcome.Cancelled, record.Outcome);
        Assert.Equal(McpAuthorizationResult.Allowed, record.Authorization);
        Assert.False(audit.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task EveryTerminalFailureClassIsAppendedExactlyOnce()
    {
        var audit = new RecordingAudit();
        var service = new McpInvocationAuditService(audit);
        Exception[] failures =
        [
            new McpInvocationRejectedException(McpInvocationOutcome.Denied, McpInvocationAuditReason.AuthorizationDenied),
            new ArgumentException("invalid"),
            new KeyNotFoundException("unknown"),
            new TimeoutException(),
            new McpInvocationLimitException(),
            new McpResponseOversizeException(),
            new McpApplicationResponseOversizeException(),
            new InvalidOperationException("repository"),
        ];

        foreach (Exception failure in failures)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync(
                CreateRequest(),
                _ => ValueTask.FromException<string>(failure),
                value => value.Length,
                CancellationToken.None).AsTask());
        }

        Assert.Equal(failures.Length, audit.Calls);
        Assert.Equal(
            new[]
            {
                McpInvocationOutcome.Denied, McpInvocationOutcome.Invalid,
                McpInvocationOutcome.UnknownTool, McpInvocationOutcome.Timeout,
                McpInvocationOutcome.Limited, McpInvocationOutcome.Oversize,
                McpInvocationOutcome.Oversize,
                McpInvocationOutcome.RepositoryFailure,
            },
            audit.Records.Select(static r => r.Outcome));
        Assert.All(audit.Records, static record => Assert.Equal("mcp_client", record.ActorKind));
    }

    private static McpInvocationRequest CreateRequest()
    {
        using JsonDocument document = JsonDocument.Parse("{\"instanceId\":\"11111111-1111-4111-8111-111111111111\"}");
        return new McpInvocationRequest(
            new McpClientIdentifier("client-a"),
            new McpToolName("get_instance_health"),
            new McpActionName("get_instance_health"),
            document.RootElement,
            new AuditCorrelationId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));
    }

    private sealed class RecordingAudit : IMcpInvocationAuditPort
    {
        public List<McpInvocationAuditRecord> Records { get; } = [];
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = cancellationToken;
            if (Failure is not null) throw Failure;
            Records.Add(request.Record);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
            return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, now));
        }
    }
}
