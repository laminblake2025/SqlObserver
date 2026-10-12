using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class DatabaseFileIoTrendServiceTests
{
    [Fact]
    public async Task ReadRequiresReadGrantForThisTargetEvenWithAnotherScopedRole()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        var repository = new ProbeRepository();
        var service = new DatabaseFileIoTrendQueryService(repository);
        DateTimeOffset to = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var request = new DatabaseFileIoTrendRequest(target, 6, 2, to.AddHours(-1), to,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([other])),
             new RoleAuthorizationGrant(ApplicationRole.Auditor,
                TargetAuthorizationScope.ForTargets([target]))]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadAsync(authorization, request, CancellationToken.None));
        Assert.Equal(0, repository.Reads);
    }

    private sealed class ProbeRepository : IDatabaseFileIoTrendRepositoryPort
    {
        public int Reads { get; private set; }
        public ValueTask<DatabaseFileIoTrendResult> ReadAsync(
            DatabaseFileIoTrendRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            throw new InvalidOperationException("A denied request reached the repository.");
        }
    }
}
