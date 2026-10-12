using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryStoreGroupPlanTrendServiceTests
{
    private static readonly MonitoredInstanceId TargetA = new(Guid.NewGuid());
    private static readonly MonitoredInstanceId TargetB = new(Guid.NewGuid());
    private static readonly PlanOpaqueIdentity Plan = new(
        new QueryOpaqueIdentity(6, new string('a', 64)), new string('b', 64));

    [Fact]
    public async Task ReadRequiresAReadGrantOnTheRequestedTarget()
    {
        var repository = new ProbeRepository();
        var service = new QueryStoreGroupPlanTrendQueryService(repository);
        QueryStoreGroupPlanTrendRequest request = Request(TargetA);
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-100"),
            AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([TargetB]))]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadAsync(authorization, request, CancellationToken.None));
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task ComparableAndMissingCoverageStayDistinct()
    {
        QueryStoreGroupPlanTrendRequest request = Request(TargetA);
        var point = new QueryStoreGroupPlanTrendPoint(request.FromUtc, 3,
            1, 1, 1, 0, "10", "20", "1", "30", "4", "5",
            1, 1, 1, 0, 0, new Dictionary<int, string> { [3] = "5" });
        var repository = new ProbeRepository
        {
            Result = new QueryStoreGroupPlanTrendResult(TargetA, Plan,
                request.FromUtc, request.ToUtc, [point]),
        };
        var service = new QueryStoreGroupPlanTrendQueryService(repository);
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-101"),
            AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([TargetA]))]);

        QueryStoreGroupPlanTrendResult result = await service.ReadAsync(
            authorization, request, CancellationToken.None);

        Assert.Equal("10", Assert.Single(result.Points).CpuMilliseconds);
        Assert.Equal(1, repository.Reads);
    }

    private static QueryStoreGroupPlanTrendRequest Request(MonitoredInstanceId target)
    {
        DateTimeOffset to = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        return new QueryStoreGroupPlanTrendRequest(target, Plan, to.AddHours(-1), to,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
    }

    private sealed class ProbeRepository : IQueryStoreGroupPlanTrendRepositoryPort
    {
        public QueryStoreGroupPlanTrendResult? Result { get; init; }
        public int Reads { get; private set; }
        public ValueTask<QueryStoreGroupPlanTrendResult> ReadAsync(
            QueryStoreGroupPlanTrendRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Result ?? throw new InvalidDataException());
        }
    }
}
