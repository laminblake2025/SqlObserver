using System.Security.Cryptography;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class PlanCacheContentReadServiceTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly MonitoredInstanceId Other = new(Guid.NewGuid());

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task MixedGrantsAreDeniedAndAuditedBeforeReading(bool readerHere, bool viewerHere)
    {
        var repository = new ProbeRepository();
        var protector = new ProbeProtector();
        var authorization = Active(Grant(ApplicationRole.Viewer, viewerHere ? Target : Other),
            Grant(ApplicationRole.QueryTextReader, readerHere ? Target : Other));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await new PlanCacheContentReadService(repository, protector).ReadAsync(authorization, Request(), default));
        Assert.Equal(0, repository.Reads);
        Assert.Equal(0, protector.Reads);
        Assert.Equal(["denied"], repository.Outcomes);
    }

    [Theory]
    [InlineData(SensitivePayloadKind.QueryText)]
    [InlineData(SensitivePayloadKind.ExecutionPlan)]
    public async Task SuccessfulReadAuditsBeforeReturningAndClearsTheDecryptedBuffer(SensitivePayloadKind kind)
    {
        var protector = new ProbeProtector();
        var repository = new ProbeRepository { Stored = new("available", DateTimeOffset.UtcNow, Payload(kind)) };
        repository.Audit = () => Assert.Contains((byte)'<', protector.Clear);
        var result = await new PlanCacheContentReadService(repository, protector).ReadAsync(Allowed(), Request(kind), default);
        Assert.Equal("available", result.Reason);
        Assert.Equal("<script>inert</script>", result.Content);
        Assert.Equal(repository.Stored.ObservedAtUtc, result.ObservedAtUtc);
        Assert.Equal(["opened"], repository.Outcomes);
        Assert.All(protector.Clear, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task FailedAuditPreventsReturningPlaintextAndClearsTheBuffer()
    {
        var protector = new ProbeProtector();
        var repository = new ProbeRepository
        {
            Stored = new("available", null, Payload()),
            Audit = () => throw new IOException("Audit unavailable")
        };
        await Assert.ThrowsAsync<IOException>(async () =>
            await new PlanCacheContentReadService(repository, protector).ReadAsync(Allowed(), Request(), default));
        Assert.Equal(["opened"], repository.Outcomes);
        Assert.All(protector.Clear, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("wrong_kind", "protection_failed", 0)]
    [InlineData("large_cipher", "protection_failed", 0)]
    [InlineData("empty_clear", "protection_failed", 1)]
    [InlineData("large_clear", "protection_failed", 1)]
    [InlineData("invalid_utf8", "protection_failed", 1)]
    [InlineData("crypto", "protection_failed", 1)]
    [InlineData("missing_key", "protection_unavailable", 0)]
    public async Task InvalidOrUndecryptableContentIsUnavailableAndAudited(string fault, string reason, int reads)
    {
        var protector = new ProbeProtector
        {
            IsAvailable = fault != "missing_key",
            CryptoFailure = fault == "crypto",
            Clear = fault switch
            {
                "empty_clear" => [],
                "large_clear" => new byte[16385],
                "invalid_utf8" => [0xff],
                _ => Encoding.UTF8.GetBytes("<script>inert</script>")
            }
        };
        var repository = new ProbeRepository
        {
            Stored = new("available", null,
            Payload(fault == "wrong_kind" ? SensitivePayloadKind.ExecutionPlan : SensitivePayloadKind.QueryText,
                fault == "large_cipher" ? 16385 : 1))
        };
        var result = await new PlanCacheContentReadService(repository, protector).ReadAsync(Allowed(), Request(), default);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Content);
        Assert.Equal(reads, protector.Reads);
        Assert.Equal(["unavailable"], repository.Outcomes);
        if (reads == 1 && !protector.CryptoFailure) Assert.All(protector.Clear, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("evidence_unavailable")]
    [InlineData("capture_missing")]
    [InlineData("capture_disabled")]
    [InlineData("not_selected")]
    [InlineData("source_unavailable")]
    [InlineData("oversized")]
    [InlineData("response_byte_limit")]
    [InlineData("protection_unavailable")]
    [InlineData("protection_failed")]
    public async Task MissingEvidencePreservesItsReasonWithoutDecrypting(string reason)
    {
        var repository = new ProbeRepository { Stored = new(reason, null, null) };
        var protector = new ProbeProtector();
        var result = await new PlanCacheContentReadService(repository, protector).ReadAsync(Allowed(), Request(), default);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Content);
        Assert.Equal(0, protector.Reads);
        Assert.Equal(["unavailable"], repository.Outcomes);
    }

    [Fact]
    public async Task CancellationAfterDecryptionClearsTheBufferAndDoesNotReturnContent()
    {
        using var cancellation = new CancellationTokenSource();
        var protector = new ProbeProtector { AfterDecrypt = cancellation.Cancel };
        var repository = new ProbeRepository { Stored = new("available", null, Payload()) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new PlanCacheContentReadService(repository, protector).ReadAsync(Allowed(), Request(), cancellation.Token));
        Assert.All(protector.Clear, value => Assert.Equal(0, value));
        Assert.Empty(repository.Outcomes);
    }

    private static PlanCacheContentReadRequest Request(SensitivePayloadKind kind = SensitivePayloadKind.QueryText) =>
        new(Target, Guid.NewGuid(), 6, new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64),
            kind, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
    private static ProtectedSensitivePayload Payload(SensitivePayloadKind kind = SensitivePayloadKind.QueryText, int bytes = 1) =>
        new(kind, new(new byte[32]), "AES-256-GCM", "test", new byte[12], new byte[16], new byte[bytes]);
    private static RoleAuthorizationGrant Grant(ApplicationRole role, MonitoredInstanceId target) =>
        new(role, TargetAuthorizationScope.ForTargets([target]));
    private static AuthorizationContext Active(params RoleAuthorizationGrant[] grants) =>
        new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, grants);
    private static AuthorizationContext Allowed() => Active(Grant(ApplicationRole.Viewer, Target), Grant(ApplicationRole.QueryTextReader, Target));

    private sealed class ProbeRepository : IPlanCacheContentReadRepositoryPort
    {
        public PlanCacheContentStoredResult Stored { get; init; } = new("evidence_unavailable", null, null);
        public int Reads { get; private set; }
        public List<string> Outcomes { get; } = [];
        public Action? Audit { get; set; }
        public ValueTask<PlanCacheContentStoredResult> ReadAsync(PlanCacheContentReadRequest request, CancellationToken cancellationToken)
        { Reads++; return ValueTask.FromResult(Stored); }
        public ValueTask AuditAsync(PlanCacheContentReadRequest request, string actorSid, string outcome, CancellationToken cancellationToken)
        { Outcomes.Add(outcome); Audit?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class ProbeProtector : IQuerySensitiveContentProtector
    {
        public bool IsAvailable { get; init; } = true;
        public bool CryptoFailure { get; init; }
        public byte[] Clear { get; init; } = Encoding.UTF8.GetBytes("<script>inert</script>");
        public int Reads { get; private set; }
        public Action? AfterDecrypt { get; init; }
        public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken cancellationToken)
        {
            Reads++; if (CryptoFailure) throw new CryptographicException();
            AfterDecrypt?.Invoke(); return ValueTask.FromResult(Clear);
        }
    }
}
