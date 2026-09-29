using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

// DispatchProxy boxes the ValueTask returned by the audit port; the service awaits it once.
#pragma warning disable CA2012
public sealed class ObservationTargetGroupingServiceTests
{
    [Fact]
    public async Task MixedGrantsCannotEditGroupingOutsideTargetAdministratorScope()
    {
        var viewerTarget = new MonitoredInstanceId(Guid.NewGuid());
        var adminTarget = new MonitoredInstanceId(Guid.NewGuid());
        var auth = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-100"),
            AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([viewerTarget])),
             new RoleAuthorizationGrant(ApplicationRole.TargetAdministrator, TargetAuthorizationScope.ForTargets([adminTarget]))]);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(5));
        var denials = new List<AdministrativeAuditRecord>();
        int mutations = 0;
        var audit = OverviewStub.Create<IAdministrativeAuditPort>((_, args) =>
        {
            denials.Add(((AppendAdministrativeAuditRequest)args![0]!).Record);
            return ValueTask.FromResult(new AdministrativeAuditReceipt(
                new AdministrativeAuditId(Guid.NewGuid()), new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)));
        });
        var port = OverviewStub.Create<IObservationTargetGroupingPort>((method, args) =>
        {
            Assert.Equal(nameof(IObservationTargetGroupingPort.SetAsync), method!.Name);
            mutations++;
            var request = Assert.IsType<SetObservationTargetGroupingRequest>(args![0]);
            Assert.Equal(adminTarget, request.TargetId);
            Assert.Equal("production", request.Environment);
            Assert.Equal(["critical", "east"], request.Tags);
            return Task.FromResult(new ObservationTargetGroupingMutationResult(
                ObservationTargetGroupingMutationStatus.Applied, 1));
        });
        var service = new ObservationTargetGroupingService(port, audit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetAsync(
            new SetObservationTargetGroupingCommand(auth, viewerTarget, 0, "production", ["east"],
                new AuditCorrelationId(Guid.NewGuid()), timeout), CancellationToken.None));
        Assert.Equal(0, mutations);
        Assert.Equal(AdministrativeAuditReason.TargetOutOfScope, Assert.Single(denials).Reason);

        var applied = await service.SetAsync(new SetObservationTargetGroupingCommand(auth, adminTarget,
            0, "Production", ["East", "Critical"], new AuditCorrelationId(Guid.NewGuid()), timeout),
            CancellationToken.None);
        Assert.Equal(ObservationTargetGroupingMutationStatus.Applied, applied.Status);
        Assert.Equal(1, mutations);
        Assert.Single(denials);
    }
}
