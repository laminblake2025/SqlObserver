using System.Reflection;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

// DispatchProxy boxes the ValueTask returned to each service; that service awaits it once.
#pragma warning disable CA2012

public sealed class TargetProjectionAuthorizationTests
{
    private static readonly MonitoredInstanceId TargetA = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly MonitoredInstanceId TargetB = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly DateTimeOffset At = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    public enum ReadOperation
    {
        Deadlocks, DeadlockDetail,
        QueryStatus, TopQueries, QueryHistory, QueryPlan,
        Backups, AgentFailures, TempDb, TempDbFiles, AvailabilityGroups, AvailabilityReplicas, AvailabilityDatabases,
        Rollups, RollupPage, Comparison,
        ActiveAlerts, ActiveAlertPage,
    }

    [Theory]
    [InlineData(ReadOperation.Deadlocks, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.DeadlockDetail, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.QueryStatus, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.TopQueries, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.QueryHistory, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.QueryPlan, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.Backups, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.AgentFailures, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.TempDb, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.TempDbFiles, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.AvailabilityGroups, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.AvailabilityReplicas, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.AvailabilityDatabases, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.Rollups, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.RollupPage, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.Comparison, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.ActiveAlerts, ApplicationRole.QueryTextReader)]
    [InlineData(ReadOperation.ActiveAlertPage, ApplicationRole.CollectorService)]
    public async Task MixedGrantCannotReadOtherTargetButRetainsItsViewerTarget(
        ReadOperation operation, ApplicationRole unrelatedRole)
    {
        var probe = new ReadProbe();
        AuthorizationContext authorization = Active(
            Grant(ApplicationRole.Viewer, TargetA), Grant(unrelatedRole, TargetB));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.ReadAsync(operation, authorization, TargetB));
        Assert.Equal(0, probe.RepositoryCalls);

        await probe.ReadAsync(operation, authorization, TargetA);
        Assert.True(probe.RepositoryCalls > 0);
    }

    [Theory]
    [InlineData(ReadOperation.Deadlocks, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.TopQueries, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.Backups, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.RollupPage, ApplicationRole.SecurityAdministrator)]
    [InlineData(ReadOperation.ActiveAlerts, ApplicationRole.QueryTextReader)]
    public async Task GlobalUnrelatedRoleCannotWidenTheReadRole(ReadOperation operation, ApplicationRole unrelatedRole)
    {
        var probe = new ReadProbe();
        AuthorizationContext authorization = Active(
            Grant(ApplicationRole.Viewer, TargetA), new(unrelatedRole, TargetAuthorizationScope.ForAllTargets()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.ReadAsync(operation, authorization, TargetB));
        Assert.Equal(0, probe.RepositoryCalls);
    }

    [Theory]
    [InlineData(ReadOperation.Deadlocks)]
    [InlineData(ReadOperation.TopQueries)]
    [InlineData(ReadOperation.Backups)]
    [InlineData(ReadOperation.RollupPage)]
    [InlineData(ReadOperation.ActiveAlerts)]
    public async Task EmptyReadScopeCannotBorrowACollectorTarget(ReadOperation operation)
    {
        var probe = new ReadProbe();
        AuthorizationContext authorization = Active(
            new(ApplicationRole.Viewer, TargetAuthorizationScope.None()), Grant(ApplicationRole.CollectorService, TargetB));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.ReadAsync(operation, authorization, TargetB));
        Assert.Equal(0, probe.RepositoryCalls);
    }

    [Theory]
    [InlineData(ReadOperation.Deadlocks)]
    [InlineData(ReadOperation.TopQueries)]
    [InlineData(ReadOperation.Backups)]
    [InlineData(ReadOperation.RollupPage)]
    [InlineData(ReadOperation.ActiveAlerts)]
    public async Task DisabledCallerCannotUseEvenAGlobalReaderGrant(ReadOperation operation)
    {
        var probe = new ReadProbe();
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-100"),
            AuthorizationPrincipalState.Disabled, [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForAllTargets())]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.ReadAsync(operation, authorization, TargetA));
        Assert.Equal(0, probe.RepositoryCalls);
    }

    [Theory]
    [InlineData(ReadOperation.Deadlocks, ApplicationRole.Viewer)]
    [InlineData(ReadOperation.TopQueries, ApplicationRole.Operator)]
    [InlineData(ReadOperation.Backups, ApplicationRole.TargetAdministrator)]
    [InlineData(ReadOperation.RollupPage, ApplicationRole.Viewer)]
    [InlineData(ReadOperation.ActiveAlerts, ApplicationRole.Auditor)]
    [InlineData(ReadOperation.ActiveAlertPage, ApplicationRole.SecurityAdministrator)]
    public async Task AnAllowedGlobalGrantStillReadsAnyTarget(ReadOperation operation, ApplicationRole role)
    {
        var probe = new ReadProbe();
        await probe.ReadAsync(operation, Active(new RoleAuthorizationGrant(role, TargetAuthorizationScope.ForAllTargets())), TargetB);
        Assert.True(probe.RepositoryCalls > 0);
    }

    [Theory]
    [InlineData(ReadOperation.Deadlocks)]
    [InlineData(ReadOperation.TopQueries)]
    [InlineData(ReadOperation.Backups)]
    [InlineData(ReadOperation.RollupPage)]
    [InlineData(ReadOperation.ActiveAlertPage)]
    public async Task EitherAllowedRoleCanAuthorizeItsOwnTarget(ReadOperation operation)
    {
        var probe = new ReadProbe();
        AuthorizationContext authorization = Active(Grant(ApplicationRole.Viewer, TargetA), Grant(ApplicationRole.Operator, TargetB));

        await probe.ReadAsync(operation, authorization, TargetA);
        int firstCalls = probe.RepositoryCalls;
        await probe.ReadAsync(operation, authorization, TargetB);
        Assert.True(firstCalls > 0);
        Assert.True(probe.RepositoryCalls > firstCalls);
    }

    private static AuthorizationContext Active(params RoleAuthorizationGrant[] grants) =>
        new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, grants);

    private static RoleAuthorizationGrant Grant(ApplicationRole role, MonitoredInstanceId target) =>
        new(role, TargetAuthorizationScope.ForTargets([target]));

    private sealed class ReadProbe
    {
        public int RepositoryCalls { get; private set; }

        public async Task ReadAsync(ReadOperation operation, AuthorizationContext authorization, MonitoredInstanceId target)
        {
            var deadlocks = new DeadlockProjectionQueryService(Repository<IDeadlockProjectionRepositoryPort>());
            var queries = new QueryPerformanceApiQueryService(Repository<IQueryPerformanceApiRepositoryPort>());
            var operational = new OperationalHealthQueryService(Repository<IOperationalHealthRepositoryPort>());
            var analytics = new AnalyticsQueryService(Repository<IAnalyticsRepositoryPort>());
            var alerts = new AlertQueryService(Repository<IAlertRepositoryPort>());
            var operationalRequest = new OperationalHealthRequest(target, At.AddHours(-1), At, 10, null, Timeout);
            var analyticsRequest = new AnalyticsQueryRequest(target, "engine.user_connections", At.AddHours(-1), At, 10, Timeout);
            var queryIdentity = new QueryOpaqueIdentity(1, new string('a', 64));
            switch (operation)
            {
                case ReadOperation.Deadlocks:
                    await deadlocks.ListDeadlocksAsync(new(authorization, target, At.AddHours(-1), At, 10, null, Timeout), CancellationToken.None);
                    break;
                case ReadOperation.DeadlockDetail:
                    await deadlocks.GetDeadlockAsync(authorization, target, Guid.Parse("00000000-0000-0000-0000-000000000003"), Timeout, CancellationToken.None);
                    break;
                case ReadOperation.QueryStatus:
                    await queries.GetStatusAsync(authorization, new(target, At.AddHours(-1), At, Timeout), CancellationToken.None);
                    break;
                case ReadOperation.TopQueries:
                    await queries.GetTopAsync(authorization, new(target, At.AddHours(-1), At, QueryPerformanceMetric.CpuMilliseconds, 10, null, Timeout), CancellationToken.None);
                    break;
                case ReadOperation.QueryHistory:
                    await queries.GetHistoryAsync(authorization, new(target, queryIdentity, At.AddHours(-1), At, 10, null, Timeout), CancellationToken.None);
                    break;
                case ReadOperation.QueryPlan:
                    await queries.GetPlanAsync(authorization, new(target, new(queryIdentity, new string('b', 64)), Timeout), CancellationToken.None);
                    break;
                case ReadOperation.Backups:
                    await operational.GetBackupsAsync(authorization, operationalRequest, CancellationToken.None);
                    break;
                case ReadOperation.AgentFailures:
                    await operational.GetAgentFailuresAsync(authorization, operationalRequest, CancellationToken.None);
                    break;
                case ReadOperation.TempDb:
                    await operational.GetTempDbAsync(authorization, operationalRequest, CancellationToken.None);
                    break;
                case ReadOperation.TempDbFiles:
                    await operational.GetTempDbFilesAsync(authorization, operationalRequest, CancellationToken.None);
                    break;
                case ReadOperation.AvailabilityGroups:
                    await operational.GetAvailabilityGroupsAsync(authorization, operationalRequest, CancellationToken.None);
                    break;
                case ReadOperation.AvailabilityReplicas:
                    await operational.GetAvailabilityGroupReplicasAsync(authorization, operationalRequest, CancellationToken.None);
                    break;
                case ReadOperation.AvailabilityDatabases:
                    await operational.GetAvailabilityGroupDatabasesAsync(authorization, operationalRequest, CancellationToken.None);
                    break;
                case ReadOperation.Rollups:
                    await analytics.GetRollupsAsync(authorization, analyticsRequest, RollupInterval.Hour, CancellationToken.None);
                    break;
                case ReadOperation.RollupPage:
                    await analytics.GetRollupPageAsync(authorization, analyticsRequest, RollupInterval.Hour, null, CancellationToken.None);
                    break;
                case ReadOperation.Comparison:
                    await analytics.CompareAsync(authorization, analyticsRequest, At.AddHours(-2), At.AddHours(-1), At.AddHours(-1), At, CancellationToken.None);
                    break;
                case ReadOperation.ActiveAlerts:
                    await alerts.ListActiveAsync(authorization, target, 10, CancellationToken.None);
                    break;
                case ReadOperation.ActiveAlertPage:
                    await alerts.ListActivePageAsync(authorization, target, 10, null, CancellationToken.None);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private T Repository<T>() where T : class => OverviewStub.Create<T>(EmptyResult);

        private object? EmptyResult(MethodInfo? method, object?[]? arguments)
        {
            RepositoryCalls++;
            Type result = method!.ReturnType;
            if (result == typeof(ValueTask<TopQueryPage>)) return ValueTask.FromResult(new TopQueryPage([], false, At));
            if (result == typeof(ValueTask<QueryHistoryPage>)) return ValueTask.FromResult(new QueryHistoryPage([], false, At));
            if (result == typeof(ValueTask<AnalyticsRollupPage>)) return ValueTask.FromResult(new AnalyticsRollupPage([], false, null, 1, 1, At, At));
            if (result == typeof(ValueTask<IReadOnlyList<AlertActiveDto>>)) return ValueTask.FromResult<IReadOnlyList<AlertActiveDto>>([]);
            if (result == typeof(ValueTask<AlertActivePage>)) return ValueTask.FromResult(new AlertActivePage([], At, null));
            // All other reads are nullable snapshot/detail contracts: null means no collected data.
            return Activator.CreateInstance(result);
        }
    }
}
