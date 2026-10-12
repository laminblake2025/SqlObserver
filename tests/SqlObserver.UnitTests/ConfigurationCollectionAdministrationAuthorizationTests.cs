using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class ConfigurationCollectionAdministrationAuthorizationTests
{
    [Fact]
    public async Task MixedGrantCannotEnableAnotherTargetsCollector()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        var repository = new Repository();
        var audit = new Audit();
        var service = new ConfigurationCollectionAdministrationService(repository, audit);
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"),
            AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.TargetAdministrator,
                TargetAuthorizationScope.ForTargets([other])),
             new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([target]))]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.SetAsync(authorization, target, 1, false, true,
                new AuditCorrelationId(Guid.NewGuid()),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), CancellationToken.None));
        Assert.Equal(0, repository.Calls);
        Assert.Equal(1, audit.Calls);
        Assert.Equal(AdministrativeAuditReason.TargetOutOfScope, audit.Reason);
    }

    private sealed class Repository : IConfigurationCollectionAdministrationPort
    {
        public int Calls { get; private set; }

        public ValueTask<ConfigurationCollectionMutationResult> SetAsync(
            SetConfigurationCollectionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new ConfigurationCollectionMutationResult(
                ConfigurationCollectionMutationStatus.Applied, 2, true));
        }
    }

    private sealed class Audit : IAdministrativeAuditPort
    {
        public int Calls { get; private set; }
        public AdministrativeAuditReason? Reason { get; private set; }

        public ValueTask<AdministrativeAuditReceipt> AppendAsync(
            AppendAdministrativeAuditRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Reason = request.Record.Reason;
            return ValueTask.FromResult(new AdministrativeAuditReceipt(
                new AdministrativeAuditId(Guid.NewGuid()),
                new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        }
    }
}
