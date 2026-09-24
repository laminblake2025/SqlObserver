using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

// DispatchProxy boxes ValueTasks, which the actual application services await once.
#pragma warning disable CA2012

public sealed class OverviewAuthorizationTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly ObservationTarget TargetA = Target(1);
    private static readonly ObservationTarget TargetB = Target(2);
    private static readonly ObservationTarget TargetC = Target(3);

    [Theory]
    [InlineData(ApplicationRole.Auditor)]
    [InlineData(ApplicationRole.SecurityAdministrator)]
    public async Task OverviewNarrowsScopeBeforeEveryInventoryPageAndProfileRead(ApplicationRole inventoryRole)
    {
        var probe = new InventoryProbe([TargetA, TargetB, TargetC])
        {
            PageSize = 1,
            BeforeList = request =>
            {
                Assert.False(request.TargetScope.AllTargets);
                Assert.Equal(new[] { TargetA.TargetId, TargetC.TargetId }, request.TargetScope.TargetIds);
            },
        };
        AuthorizationContext authorization = Active(
            new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([TargetA.TargetId, TargetC.TargetId])),
            new(inventoryRole, TargetAuthorizationScope.ForAllTargets()));

        OverviewSnapshot result = await probe.Overview.ReadAsync(new(authorization, null, At.AddHours(-1), At), CancellationToken.None);

        Assert.Equal(new[] { TargetA.TargetId.Value, TargetC.TargetId.Value }, result.Targets.Select(target => target.TargetId));
        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(2, probe.ListCalls.Count);
        Assert.Equal(new[] { TargetA.TargetId, TargetC.TargetId }, probe.ProfileTargets);
    }

    [Theory]
    [InlineData(ApplicationRole.Auditor)]
    [InlineData(ApplicationRole.SecurityAdministrator)]
    public async Task StandaloneInventoryRetainsItsBroaderReadRoles(ApplicationRole inventoryRole)
    {
        var probe = new InventoryProbe([TargetA, TargetB]);
        AuthorizationContext authorization = Active(
            Grant(ApplicationRole.Viewer, TargetA), Grant(inventoryRole, TargetB));
        var query = new ListObservationTargetsQuery(authorization, 100, null, false, Timeout);

        ObservationTargetPage inventory = await new ObservationTargetQueryService(probe.TargetRepository).ListAsync(query, CancellationToken.None);
        ObservationTargetStatusPage status = await probe.Status.ListAsync(query, CancellationToken.None);

        Assert.Equal(new[] { TargetA.TargetId, TargetB.TargetId }, inventory.Targets.Select(target => target.TargetId));
        Assert.Equal(new[] { TargetA.TargetId, TargetB.TargetId }, status.Targets.Select(target => target.Target.TargetId));
        Assert.Equal(new[] { TargetA.TargetId, TargetB.TargetId }, probe.ProfileTargets);
    }

    [Fact]
    public async Task ExplicitNonOperationalTargetIsDeniedBeforeInventoryAndProfiles()
    {
        var probe = new InventoryProbe([TargetA, TargetB]);
        AuthorizationContext authorization = Active(Grant(ApplicationRole.Viewer, TargetA), Grant(ApplicationRole.Auditor, TargetB));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.Overview.ReadAsync(
            new(authorization, TargetB.TargetId.Value, At.AddHours(-1), At), CancellationToken.None));

        Assert.Empty(probe.ListCalls);
        Assert.Empty(probe.ProfileTargets);
    }

    [Fact]
    public async Task EmptyOperationalScopeReturnsAnEmptyFleetWithoutReadingUnrelatedProfiles()
    {
        var probe = new InventoryProbe([TargetB])
        {
            BeforeList = request =>
            {
                Assert.False(request.TargetScope.AllTargets);
                Assert.Empty(request.TargetScope.TargetIds);
            },
        };
        AuthorizationContext authorization = Active(
            new(ApplicationRole.Viewer, TargetAuthorizationScope.None()),
            new(ApplicationRole.Auditor, TargetAuthorizationScope.ForAllTargets()));

        OverviewSnapshot result = await probe.Overview.ReadAsync(new(authorization, null, At.AddHours(-1), At), CancellationToken.None);

        Assert.Empty(result.Targets);
        Assert.Empty(result.Evidence);
        Assert.Empty(probe.ProfileTargets);
    }

    [Fact]
    public async Task DisabledCallerIsDeniedBeforeInventoryAndProfiles()
    {
        var probe = new InventoryProbe([TargetA]);
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-100"),
            AuthorizationPrincipalState.Disabled,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForAllTargets())]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.Overview.ReadAsync(
            new(authorization, null, At.AddHours(-1), At), CancellationToken.None));

        Assert.Empty(probe.ListCalls);
        Assert.Empty(probe.ProfileTargets);
    }

    [Fact]
    public async Task InventoryRoleAloneCannotOpenEvenAnEmptyOverview()
    {
        var probe = new InventoryProbe([]);
        AuthorizationContext authorization = Active(new RoleAuthorizationGrant(ApplicationRole.Auditor, TargetAuthorizationScope.ForAllTargets()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.Overview.ReadAsync(
            new(authorization, null, At.AddHours(-1), At), CancellationToken.None));

        Assert.Empty(probe.ListCalls);
        Assert.Empty(probe.ProfileTargets);
    }

    [Fact]
    public async Task OverviewCombinesOnlyTheScopesOfItsAllowedRoles()
    {
        var probe = new InventoryProbe([TargetA, TargetB, TargetC])
        {
            BeforeList = request => Assert.Equal(new[] { TargetA.TargetId, TargetB.TargetId }, request.TargetScope.TargetIds),
        };
        AuthorizationContext authorization = Active(
            Grant(ApplicationRole.Viewer, TargetA), Grant(ApplicationRole.Operator, TargetB), Grant(ApplicationRole.Auditor, TargetC));

        OverviewSnapshot result = await probe.Overview.ReadAsync(new(authorization, null, At.AddHours(-1), At), CancellationToken.None);

        Assert.Equal(new[] { TargetA.TargetId.Value, TargetB.TargetId.Value }, result.Targets.Select(target => target.TargetId));
        Assert.Equal(new[] { TargetA.TargetId, TargetB.TargetId }, probe.ProfileTargets);
    }

    [Fact]
    public async Task GlobalOperationalGrantPreservesTheFullFleet()
    {
        var probe = new InventoryProbe([TargetA, TargetB])
        {
            BeforeList = request => Assert.True(request.TargetScope.AllTargets),
        };
        AuthorizationContext authorization = Active(new RoleAuthorizationGrant(ApplicationRole.TargetAdministrator, TargetAuthorizationScope.ForAllTargets()));

        OverviewSnapshot result = await probe.Overview.ReadAsync(new(authorization, null, At.AddHours(-1), At), CancellationToken.None);

        Assert.Equal(2, result.Targets.Count);
        Assert.Equal(2, result.Evidence.Count);
    }

    private static AuthorizationContext Active(params RoleAuthorizationGrant[] grants) =>
        new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, grants);

    private static RoleAuthorizationGrant Grant(ApplicationRole role, ObservationTarget target) =>
        new(role, TargetAuthorizationScope.ForTargets([target.TargetId]));

    private static ObservationTarget Target(int index) => new(
        new MonitoredInstanceId(new Guid(index, 0, 0, new byte[8])),
        new ObservationTargetKey($"sql{index}"), new ObservationTargetDisplayName($"SQL {index}"),
        new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql.example.test"), tcpPort: 1433),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))),
        ObservationTargetLifecycle.Active, new ObservationTargetRevision(1), At, At, At);

    private sealed class InventoryProbe
    {
        public InventoryProbe(IReadOnlyList<ObservationTarget> inventory)
        {
            TargetRepository = OverviewStub.Create<IObservationTargetRepositoryPort>((method, arguments) =>
            {
                Assert.Equal(nameof(IObservationTargetRepositoryPort.ListObservationTargetsAsync), method!.Name);
                var request = Assert.IsType<ListObservationTargetsRequest>(arguments![0]);
                ListCalls.Add(request);
                BeforeList?.Invoke(request);
                var remaining = inventory.Where(target => request.TargetScope.Contains(target.TargetId) &&
                    (request.Cursor is null || string.CompareOrdinal(target.Key.Value, request.Cursor.LastKey.Value) > 0)).ToArray();
                ObservationTarget[] page = remaining.Take(PageSize).ToArray();
                ObservationTargetListCursor? cursor = remaining.Length > PageSize ? new(page[^1].Key, page[^1].TargetId) : null;
                return ValueTask.FromResult(new ObservationTargetPage(page, cursor));
            });
            var profiles = OverviewStub.Create<ICapabilityProfileRepositoryPort>((method, arguments) =>
            {
                Assert.Equal(nameof(ICapabilityProfileRepositoryPort.GetLatestForTargetsAsync), method!.Name);
                var request = Assert.IsType<GetLatestCapabilityProfilesRequest>(arguments![0]);
                ProfileTargets.AddRange(request.TargetIds);
                return ValueTask.FromResult(new CapabilityProfileBatch([]));
            });
            Status = new ObservationTargetStatusQueryService(TargetRepository, profiles);
            Overview = new OverviewQueryService(Status,
                OverviewStub.Create<IHealthProjectionQueryService>(), OverviewStub.Create<IAlertQueryService>(),
                OverviewStub.Create<IActivityProjectionQueryService>(), OverviewStub.Create<IDeadlockProjectionQueryService>(),
                OverviewStub.Create<IOperationalHealthQueryService>(), OverviewStub.Create<IMetricSeriesQueryService>(),
                OverviewStub.Create<IOverviewHistoryRepositoryPort>());
        }

        public IObservationTargetRepositoryPort TargetRepository { get; }
        public ObservationTargetStatusQueryService Status { get; }
        public OverviewQueryService Overview { get; }
        public int PageSize { get; init; } = 100;
        public Action<ListObservationTargetsRequest>? BeforeList { get; init; }
        public List<ListObservationTargetsRequest> ListCalls { get; } = [];
        public List<MonitoredInstanceId> ProfileTargets { get; } = [];
    }
}
