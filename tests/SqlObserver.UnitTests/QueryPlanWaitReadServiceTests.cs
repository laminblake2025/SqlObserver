using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryPlanWaitReadServiceTests
{
    private static readonly MonitoredInstanceId TargetA = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly MonitoredInstanceId TargetB = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly Guid Run = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly PlanOpaqueIdentity Plan = new(
        new QueryOpaqueIdentity(5, new string('a', 64)), new string('b', 64));

    [Fact]
    public async Task ReadGrantOnAnotherTargetCannotFetchPlanWaits()
    {
        var repository = new ProbeRepository();
        var service = new QueryPlanWaitReadService(repository);
        var authorization = Active(Grant(ApplicationRole.Viewer, TargetB));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadAsync(authorization, Request(TargetA), CancellationToken.None));

        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task ViewerCanReadOnlyTargetBoundWaitCategories()
    {
        var capturedAt = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var repository = new ProbeRepository
        {
            Snapshot = new QueryPlanWaitSnapshot([new QueryWaitCategory(3, 3049)], capturedAt),
        };
        var service = new QueryPlanWaitReadService(repository);

        QueryPlanWaitReadResult result = await service.ReadAsync(
            Active(Grant(ApplicationRole.Viewer, TargetA)), Request(TargetA), CancellationToken.None);

        Assert.Equal("available", result.Status);
        Assert.Equal(capturedAt, result.CapturedAtUtc);
        Assert.Equal(3, Assert.Single(result.Categories!).Category);
        Assert.Equal(1, repository.Reads);
    }

    private static QueryPlanReadRequest Request(MonitoredInstanceId target) =>
        new(target, Run, Plan, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));

    private static AuthorizationContext Active(params RoleAuthorizationGrant[] grants) =>
        new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, grants);

    private static RoleAuthorizationGrant Grant(ApplicationRole role, MonitoredInstanceId target) =>
        new(role, TargetAuthorizationScope.ForTargets([target]));

    private sealed class ProbeRepository : IQueryPlanWaitReadRepositoryPort
    {
        public QueryPlanWaitSnapshot? Snapshot { get; init; }
        public int Reads { get; private set; }

        public ValueTask<QueryPlanWaitSnapshot?> ReadAsync(
            QueryPlanReadRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Snapshot);
        }
    }
}
