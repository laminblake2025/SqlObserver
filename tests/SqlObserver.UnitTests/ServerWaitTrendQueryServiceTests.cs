using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class ServerWaitTrendQueryServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task MixedGrantsDoNotAuthorizeAWaitTrendOnTheAuditorTarget()
    {
        var viewerTarget = new MonitoredInstanceId(Guid.NewGuid());
        var auditorTarget = new MonitoredInstanceId(Guid.NewGuid());
        AuthorizationContext authorization = Active(
            new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([viewerTarget])),
            new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([auditorTarget])));
        var repository = new Probe();
        var service = new ServerWaitTrendQueryService(repository);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(
            new ServerWaitTrendQuery(authorization, auditorTarget, At.AddHours(-1), At, Timeout),
            CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Calls);

        ServerWaitTrendPage? page = await service.ReadAsync(new ServerWaitTrendQuery(
            authorization, viewerTarget, At.AddHours(-1), At, Timeout), CancellationToken.None);
        Assert.Equal(viewerTarget, page?.TargetId);
        Assert.Equal(1, repository.Calls);
    }

    [Fact]
    public void TrendRejectsAValueThatClaimsCompleteEvidenceAfterLoss()
    {
        Assert.Throws<ArgumentException>(() => new ServerWaitTrendPoint(At, "Lock",
            25m, 1, 0, 1, 0, 1));
        Assert.Null(new ServerWaitTrendPoint(At, "Lock", null,
            1, 0, 1, 0, 1).WaitMilliseconds);
    }

    private static AuthorizationContext Active(params RoleAuthorizationGrant[] grants) =>
        new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, grants);

    private sealed class Probe : IServerWaitTrendRepositoryPort
    {
        public int Calls { get; private set; }

        public ValueTask<ServerWaitTrendPage?> ReadAsync(ServerWaitTrendRepositoryRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<ServerWaitTrendPage?>(new ServerWaitTrendPage(
                request.TargetId, request.FromUtc, request.ToUtc, At,
                [new ServerWaitTrendPoint(At.AddMinutes(-5), "Lock", 25m,
                    1, 0, 0, 0, 1)]));
        }
    }
}
