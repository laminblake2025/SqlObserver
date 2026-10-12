using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class ServerConfigurationSnapshotAuthorizationTests
{
    [Fact]
    public async Task MixedGrantCannotBorrowAnotherTargetsViewerRole()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        var repository = new Repository();
        var service = new ServerConfigurationSnapshotQueryService(repository);
        var request = new ServerConfigurationSnapshotRequest(target, 10,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var mixed = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"),
            AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([other])),
             new RoleAuthorizationGrant(ApplicationRole.Auditor,
                TargetAuthorizationScope.ForTargets([target]))]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadAsync(mixed, request, CancellationToken.None));
        Assert.Equal(0, repository.Reads);

        var authorized = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"),
            AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([target]))]);
        ServerConfigurationSnapshotPage page = await service.ReadAsync(authorized, request,
            CancellationToken.None);
        Assert.Same(repository.Page, page);
        Assert.Equal(1, repository.Reads);
    }

    private sealed class Repository : IServerConfigurationSnapshotRepositoryPort
    {
        public int Reads { get; private set; }
        public ServerConfigurationSnapshotPage? Page { get; private set; }

        public ValueTask<ServerConfigurationSnapshotPage> ReadAsync(
            ServerConfigurationSnapshotRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            Page = new ServerConfigurationSnapshotPage(request.TargetId, 1, null,
                null, null, false, null, [], null);
            return ValueTask.FromResult(Page);
        }
    }
}
