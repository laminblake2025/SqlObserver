using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class AlertDestinationQueryAuthorizationTests
{
    [Fact]
    public async Task MixedGrantsCannotReadAnotherTargetsDestinations()
    {
        var secured = new MonitoredInstanceId(Guid.NewGuid());
        var viewed = new MonitoredInstanceId(Guid.NewGuid());
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-7005"), AuthorizationPrincipalState.Active,
            [
                new RoleAuthorizationGrant(ApplicationRole.SecurityAdministrator,
                    TargetAuthorizationScope.ForTargets([secured])),
                new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([viewed])),
            ]);
        var repository = new RecordingDestinationRead();
        var service = new AlertDestinationQueryService(repository);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListDestinationsForAdminAsync(
            authorization, viewed, 10, null, CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Calls);

        AlertDestinationListPage page = await service.ListDestinationsForAdminAsync(
            authorization, secured, 10, null, CancellationToken.None);
        Assert.Equal(secured, page.TargetId);
        Assert.Equal(1, repository.Calls);
    }

    private sealed class RecordingDestinationRead : IAlertDestinationReadPort
    {
        public int Calls { get; private set; }

        public ValueTask<AlertDestinationListPage> ListDestinationsForAdminAsync(
            MonitoredInstanceId targetId, int limit, AlertDestinationListCursor? cursor,
            RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new AlertDestinationListPage(targetId, DateTimeOffset.UtcNow,
                [], null));
        }
    }
}
