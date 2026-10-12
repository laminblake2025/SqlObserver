using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryStorePlanRegressionServiceTests
{
    [Fact]
    public async Task MixedGrantsCannotReadAnotherTarget()
    {
        var targetA = new MonitoredInstanceId(Guid.NewGuid());
        var targetB = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var request = new QueryStorePlanRegressionRequest(targetB, to.AddHours(-1), to,
            25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var repository = new ProbeRepository();
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-912"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([targetA])),
             new RoleAuthorizationGrant(ApplicationRole.Auditor,
                TargetAuthorizationScope.ForTargets([targetB]))]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await new QueryStorePlanRegressionQueryService(repository).ReadAsync(
                authorization, request, CancellationToken.None));
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task RoundedAveragesAtTheThresholdRemainValid()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var request = new QueryStorePlanRegressionRequest(target, to.AddHours(-1), to,
            25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var regression = new QueryStorePlanRegression(
            new QueryOpaqueIdentity(6, new string('a', 64)),
            new string('b', 64), new string('c', 64),
            "50.01", "100.01", "2", "1000", "1000", 2, 2, 2, 3,
            to.AddMinutes(-5));
        var repository = new ProbeRepository { Result = new QueryStorePlanRegressionResult(
            target, request.FromUtc, request.ToUtc, [regression]) };
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-913"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([target]))]);

        QueryStorePlanRegressionResult result = await new QueryStorePlanRegressionQueryService(repository)
            .ReadAsync(authorization, request, CancellationToken.None);
        Assert.Single(result.Items);
    }

    private sealed class ProbeRepository : IQueryStorePlanRegressionRepositoryPort
    {
        public QueryStorePlanRegressionResult? Result { get; init; }
        public int Reads { get; private set; }
        public ValueTask<QueryStorePlanRegressionResult> ReadAsync(
            QueryStorePlanRegressionRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Result ?? new QueryStorePlanRegressionResult(
                request.TargetId, request.FromUtc, request.ToUtc, []));
        }
    }
}
