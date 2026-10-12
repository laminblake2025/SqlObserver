using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class PlanCacheIntervalHistoryServiceTests
{
    [Fact]
    public async Task HistoryRejectsMixedGrantsBeforeIoAndUnknownCountersAfterIo()
    {
        var request = new PlanCacheIntervalHistoryRequest(new(Guid.NewGuid()), 6, new('a', 64), new('b', 64), new('c', 64),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), 100, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var row = new PlanCacheIntervalHistoryItem(Guid.NewGuid(), new('d', 64), new('e', 64), request.FromUtc.AddMinutes(1),
            request.FromUtc.AddMinutes(1), false, "reset", null, null, null, null, null, null, null, null);
        var expected = new PlanCacheIntervalHistoryResult(request.TargetId.Value, 6, new('a', 64), new('b', 64), new('c', 64), request.FromUtc,
            request.ToUtc, "plan_cache_delta", "observed_entries_only", 1, "2", request.ToUtc, 1, false, [row]);
        var repository = new Probe { Result = expected }; var service = new PlanCacheIntervalHistoryQueryService(repository);
        var mixed = Access(new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Guid.NewGuid())])),
            new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([request.TargetId])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.ReadAsync(mixed, request, default));
        Assert.Equal(0, repository.Reads);
        var allowed = Access(new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([request.TargetId])));
        Assert.Equal(expected, await service.ReadAsync(allowed, request, default));
        foreach (var invalid in new[] { expected with { Items = [row with { Executions = "0" }] },
            expected with { Items = [row with { Transition = "comparable" }] }, expected with { DatabaseIncarnation = new('f',64) },
            expected with { Items = [row,row],TotalRows=2 }, expected with { HasMore=true }, expected with { Snapshot="invalid" } })
        {
            repository.Result = invalid;
            await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ReadAsync(allowed, request, default));
        }
        var measured = row with
        {
            Transition = "comparable",
            IntervalFromUtc = request.FromUtc,
            IntervalToUtc = row.ObservedAtUtc,
            CpuMilliseconds = "9223372036854775.807",
            DurationMilliseconds = "0.001",
            Executions = "9007199254740993",
            LogicalReads = "0",
            Writes = "0",
            Rows = "0"
        };
        repository.Result = expected with { Items = [measured] };
        Assert.Equal(measured, Assert.Single((await service.ReadAsync(allowed, request, default)).Items));
        foreach (string invalid in new[] { "0.0001", "0.000000000000000000000000000000001", "0.000000000000000000000001", "00.001" })
        {
            repository.Result = expected with { Items = [measured with { DurationMilliseconds = invalid }] };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ReadAsync(allowed, request, default));
        }
        repository.Result = expected with { Items = [measured with { DurationMilliseconds = "0.0001" }] };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ReadAsync(allowed, request, default));
    }
    private static AuthorizationContext Access(params RoleAuthorizationGrant[] grants) => new(new ActorSecurityIdentifier("S-1-5-21-910"), AuthorizationPrincipalState.Active, grants);
    private sealed class Probe : IPlanCacheIntervalHistoryRepositoryPort
    {
        public int Reads { get; private set; }
        public required PlanCacheIntervalHistoryResult Result { get; set; }
        public ValueTask<PlanCacheIntervalHistoryResult> ReadAsync(PlanCacheIntervalHistoryRequest request, CancellationToken cancellationToken)
        { Reads++; return ValueTask.FromResult(Result); }
    }
}
