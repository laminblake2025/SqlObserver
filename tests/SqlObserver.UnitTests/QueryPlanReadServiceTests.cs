using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryPlanReadServiceTests
{
    private static readonly MonitoredInstanceId TargetA = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly MonitoredInstanceId TargetB = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly QueryOpaqueIdentity Query = new(5, new string('a', 64));
    private static readonly PlanOpaqueIdentity Plan = new(Query, new string('b', 64));
    private static readonly Guid Run = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task ReaderGrantOnAnotherTargetIsDeniedAndAuditedWithoutReading()
    {
        var repository = new ProbeRepository();
        var service = new QueryPlanReadService(repository, new ProbeProtector());
        var authorization = Active(Grant(ApplicationRole.Viewer, TargetA),
            Grant(ApplicationRole.QueryTextReader, TargetB));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadAsync(authorization, Request(TargetA), CancellationToken.None));

        Assert.Equal(0, repository.Reads);
        Assert.Equal(["denied"], repository.Outcomes);
    }

    [Fact]
    public async Task MissingOrUnavailableContentIsAuditedWithoutExposingXml()
    {
        var repository = new ProbeRepository();
        var service = new QueryPlanReadService(repository, new UnavailableQuerySensitiveContentProtector());
        var authorization = Active(Grant(ApplicationRole.Viewer, TargetA),
            Grant(ApplicationRole.QueryTextReader, TargetA));

        QueryPlanReadResult result = await service.ReadAsync(authorization,
            Request(TargetA), CancellationToken.None);

        Assert.Equal("unavailable", result.Status);
        Assert.Null(result.Xml);
        Assert.Equal(["unavailable"], repository.Outcomes);
    }

    [Fact]
    public async Task XmlIsReturnedOnlyAfterOpenedAudit()
    {
        var repository = new ProbeRepository { Payload = new ProtectedSensitivePayload(
            SensitivePayloadKind.ExecutionPlan, new SensitivePayloadFingerprint(new byte[32]),
            "AES-256-GCM", "test", new byte[12], new byte[16], new byte[1]) };
        var service = new QueryPlanReadService(repository, new ProbeProtector());
        var authorization = Active(Grant(ApplicationRole.Operator, TargetA),
            Grant(ApplicationRole.QueryTextReader, TargetA));

        QueryPlanReadResult result = await service.ReadAsync(authorization,
            Request(TargetA), CancellationToken.None);

        Assert.Equal("available", result.Status);
        Assert.Equal("<ShowPlanXML />", result.Xml);
        Assert.Equal(["opened"], repository.Outcomes);
    }

    private static QueryPlanReadRequest Request(MonitoredInstanceId target) =>
        new(target, Run, Plan, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));

    private static AuthorizationContext Active(params RoleAuthorizationGrant[] grants) =>
        new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, grants);

    private static RoleAuthorizationGrant Grant(ApplicationRole role, MonitoredInstanceId target) =>
        new(role, TargetAuthorizationScope.ForTargets([target]));

    private sealed class ProbeRepository : IQueryPlanReadRepositoryPort
    {
        public ProtectedSensitivePayload? Payload { get; init; }
        public int Reads { get; private set; }
        public List<string> Outcomes { get; } = [];
        public ValueTask<ProtectedSensitivePayload?> ReadAsync(
            QueryPlanReadRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Payload);
        }
        public ValueTask AuditAsync(QueryPlanReadRequest request, string actorSid,
            string outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeProtector : IQuerySensitiveContentProtector
    {
        public bool IsAvailable => true;
        public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId,
            SensitivePayloadKind kind, ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> UnprotectAsync(Guid targetId,
            ProtectedSensitivePayload payload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Encoding.UTF8.GetBytes("<ShowPlanXML />"));
    }
}
