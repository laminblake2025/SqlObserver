using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class MaintenanceWindowQueryAuthorizationTests
{
    [Fact]
    public async Task MixedGrantsCannotReadAnotherTargetsMaintenance()
    {
        var administered = new MonitoredInstanceId(Guid.NewGuid());
        var viewed = new MonitoredInstanceId(Guid.NewGuid());
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-7004"), AuthorizationPrincipalState.Active,
            [
                new RoleAuthorizationGrant(ApplicationRole.TargetAdministrator,
                    TargetAuthorizationScope.ForTargets([administered])),
                new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([viewed])),
            ]);
        var repository = new RecordingMaintenanceRead();
        var service = new MaintenanceWindowQueryService(repository);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListMaintenanceForAdminAsync(
            authorization, viewed, 10, null, CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Calls);

        MaintenanceWindowListPage page = await service.ListMaintenanceForAdminAsync(
            authorization, administered, 10, null, CancellationToken.None);
        Assert.Equal(administered, page.TargetId);
        Assert.Equal(1, repository.Calls);
    }

    private sealed class RecordingMaintenanceRead : IMaintenanceWindowReadPort
    {
        public int Calls { get; private set; }

        public ValueTask<MaintenanceWindowListPage> ListMaintenanceForAdminAsync(
            MonitoredInstanceId targetId, int limit, MaintenanceWindowListCursor? cursor,
            RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new MaintenanceWindowListPage(targetId, DateTimeOffset.UtcNow,
                [], null));
        }
    }
}
