using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryStoreWaitRankingServiceTests
{
    [Fact]
    public async Task MixedGrantsDoNotAuthorizeTheWrongTarget()
    {
        var targetA = new MonitoredInstanceId(Guid.NewGuid());
        var targetB = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var request = new QueryStoreWaitRankingRequest(targetB, to.AddHours(-1), to,
            25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var repository = new ProbeRepository();
        var service = new QueryStoreWaitRankingQueryService(repository);
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-909"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([targetA])),
             new RoleAuthorizationGrant(ApplicationRole.Auditor,
                TargetAuthorizationScope.ForTargets([targetB]))]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadAsync(authorization, request, CancellationToken.None));
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task AuthorizedReadRetainsCoverageForComparableGroups()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var request = new QueryStoreWaitRankingRequest(target, to.AddHours(-1), to,
            25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var plan = new PlanOpaqueIdentity(
            new QueryOpaqueIdentity(6, new string('a', 64)), new string('b', 64));
        var repository = new ProbeRepository
        {
            Result = new QueryStoreWaitRankingResult(target, request.FromUtc, request.ToUtc,
                [new QueryStoreWaitRankedPlan(plan, "9007199254740993", 1, 3, to.AddMinutes(-5))]),
        };
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-910"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([target]))]);

        QueryStoreWaitRankingResult result = await new QueryStoreWaitRankingQueryService(repository)
            .ReadAsync(authorization, request, CancellationToken.None);
        Assert.Equal("9007199254740993", Assert.Single(result.Items).WaitMilliseconds);
        Assert.Equal(1, result.Items[0].ComparableRows);
        Assert.Equal(3, result.Items[0].SourceRows);
    }

    [Fact]
    public async Task RepositoryCannotReturnAnUnrankedPage()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var request = new QueryStoreWaitRankingRequest(target, to.AddHours(-1), to,
            25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var query = new QueryOpaqueIdentity(6, new string('a', 64));
        var repository = new ProbeRepository
        {
            Result = new QueryStoreWaitRankingResult(target, request.FromUtc, request.ToUtc,
                [new QueryStoreWaitRankedPlan(new PlanOpaqueIdentity(query, new string('b', 64)),
                    "10", 1, 1, to.AddMinutes(-10)),
                 new QueryStoreWaitRankedPlan(new PlanOpaqueIdentity(query, new string('c', 64)),
                    "20", 1, 1, to.AddMinutes(-5))]),
        };
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-911"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([target]))]);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new QueryStoreWaitRankingQueryService(repository).ReadAsync(
                authorization, request, CancellationToken.None));
    }

    [Fact]
    public async Task CategoryMustMatchRepositoryResult()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var request = new QueryStoreWaitRankingRequest(target, to.AddHours(-1), to,
            25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), 3);
        var repository = new ProbeRepository
        {
            Result = new QueryStoreWaitRankingResult(target, request.FromUtc, request.ToUtc, [], 6),
        };
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-914"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([target]))]);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new QueryStoreWaitRankingQueryService(repository).ReadAsync(
                authorization, request, CancellationToken.None));
    }

    private sealed class ProbeRepository : IQueryStoreWaitRankingRepositoryPort
    {
        public QueryStoreWaitRankingResult? Result { get; init; }
        public int Reads { get; private set; }
        public ValueTask<QueryStoreWaitRankingResult> ReadAsync(
            QueryStoreWaitRankingRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Result ?? throw new InvalidDataException());
        }
    }
}
