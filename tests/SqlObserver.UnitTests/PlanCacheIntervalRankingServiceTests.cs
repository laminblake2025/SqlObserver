using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class PlanCacheIntervalRankingServiceTests
{
    [Fact]
    public async Task MixedRoleScopesDenyBeforeReadingCacheEvidence()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var other = new MonitoredInstanceId(Guid.NewGuid());
        var repository = new Probe(); var request = Request(target);
        var authorization = Access(new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([other])),
            new RoleAuthorizationGrant(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([target])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await new PlanCacheIntervalRankingQueryService(repository).ReadAsync(authorization, request, default));
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task ExactLargeCounterStringsSurviveWithIncompleteCoverage()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var request = Request(target);
        var row = new PlanCacheIntervalRankedQuery(6, new('a', 64), new('b', 64), new('c', 64),
            "9007199254740993.001", "9007199254740993.002", "9007199254740993", "0", "0", "0", 1,
            request.FromUtc.AddMinutes(1), request.ToUtc.AddMinutes(-1));
        var expected = new PlanCacheIntervalRankingResult(target.Value, request.FromUtc, request.ToUtc,
            "plan_cache_delta", "cpu", "observed_entries_only", 2, 1, 1, 3, 1, false, [row]);
        var repository = new Probe { Result = expected };
        var service = new PlanCacheIntervalRankingQueryService(repository);
        Assert.Equal(expected, await service.ReadAsync(Access(new RoleAuthorizationGrant(ApplicationRole.Viewer,
            TargetAuthorizationScope.ForTargets([target]))), request, default));
        foreach (var invalid in new[] { expected with { TargetId = Guid.NewGuid() },
            expected with { ComparableRows = 0 }, expected with { SourceFrames = 0 },
            expected with { Semantics = "plan_cache_cumulative" }, expected with { HasMore = true },
            expected with { Items = [row with { Executions = "0.5" }] },
            expected with { Items = [row with { FirstObservedAtUtc = request.FromUtc.AddSeconds(-1) }] },
            expected with { Items = [row, row] } })
        {
            repository.Result = invalid;
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await service.ReadAsync(Access(new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target]))), request, default));
        }
    }
    private static PlanCacheIntervalRankingRequest Request(MonitoredInstanceId target) =>
        new(target, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1),
            QueryPerformanceMetric.CpuMilliseconds, 25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
    private static AuthorizationContext Access(params RoleAuthorizationGrant[] grants) =>
        new(new ActorSecurityIdentifier("S-1-5-21-910"), AuthorizationPrincipalState.Active, grants);
    private sealed class Probe : IPlanCacheIntervalRankingRepositoryPort
    {
        public int Reads { get; private set; }
        public PlanCacheIntervalRankingResult? Result { get; set; }
        public ValueTask<PlanCacheIntervalRankingResult> ReadAsync(PlanCacheIntervalRankingRequest request, CancellationToken cancellationToken)
        { Reads++; return ValueTask.FromResult(Result ?? throw new InvalidOperationException("Unexpected cache read.")); }
    }
}
