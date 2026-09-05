using System.Reflection;
using SqlObserver.Domain.Auditing;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

// DispatchProxy requires boxing the ValueTask return; the service awaits each returned instance once.
#pragma warning disable CA2012

public sealed class OverviewQueryServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private static AuthorizationContext Auth(params MonitoredInstanceId[] ids) => new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active,
        [ApplicationRole.Viewer], ids.Length == 0 ? TargetAuthorizationScope.ForAllTargets() : TargetAuthorizationScope.ForTargets(ids));
    private static ObservationTargetStatusSnapshot Target(int index, ObservationTargetLifecycle lifecycle = ObservationTargetLifecycle.Active)
    {
        var guid = new Guid(index, 0, 0, new byte[8]);
        return new(new(new(guid), new($"sql{index}"), new($"SQL {index:D2}"), new(new(new("sql.example.test"), tcpPort:1433), new(TimeSpan.FromSeconds(5))), lifecycle, new(1), At, At, At), null);
    }
    private static OverviewQueryService Service(IReadOnlyList<ObservationTargetStatusSnapshot> inventory, IAlertQueryService? alertService = null,
        IOverviewHistoryRepositoryPort? history = null)
    {
        var targets = OverviewStub.Create<IObservationTargetStatusQueryService>((_,args) =>
        {
            var query=(ListObservationTargetsQuery)args![0]!;
            return ValueTask.FromResult(new ObservationTargetStatusPage(inventory.Where(t=>query.Authorization.CanAccess(t.Target.TargetId)).ToArray(),null));
        });
        return new(targets, OverviewStub.Create<IHealthProjectionQueryService>(), alertService??OverviewStub.Create<IAlertQueryService>(),
            OverviewStub.Create<IActivityProjectionQueryService>(),OverviewStub.Create<IDeadlockProjectionQueryService>(),OverviewStub.Create<IOperationalHealthQueryService>(),
            OverviewStub.Create<IMetricSeriesQueryService>(),history??OverviewStub.Create<IOverviewHistoryRepositoryPort>());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(10)] [InlineData(11)]
    public async Task AllServersHasNoTenServerRegistrationCap(int count)
    {
        var result=await Service(Enumerable.Range(1,count).Select(i=>Target(i)).ToArray()).ReadAsync(new(Auth(),null,At.AddHours(-1),At),CancellationToken.None);
        Assert.Equal(count,result.Targets.Count);Assert.Equal(count,result.Evidence.Count);
        Assert.All(result.Evidence,e=>{Assert.Null(e.ActiveAlerts.Value);Assert.Null(e.BlockedSessions.Value);Assert.Equal("unavailable",e.CollectionState);});
    }

    [Fact]
    public async Task SingleSelectionFiltersEveryEvidenceReadAndRetainsAuthorizedChoices()
    {
        var inventory=Enumerable.Range(1,10).Select(i=>Target(i)).ToArray();
        var result=await Service(inventory).ReadAsync(new(Auth(),inventory[3].Target.TargetId.Value,At.AddHours(-1),At),CancellationToken.None);
        Assert.Equal(10,result.Targets.Count);Assert.Equal(inventory[3].Target.TargetId.Value,Assert.Single(result.Evidence).TargetId);
    }

    [Fact]
    public async Task UnauthorizedSelectionFailsAndDisabledTargetsStaySelectable()
    {
        var first=Target(1);var disabled=Target(2,ObservationTargetLifecycle.Disabled);
        var service=Service([first,disabled]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReadAsync(new(Auth(first.Target.TargetId),disabled.Target.TargetId.Value,At.AddHours(-1),At),CancellationToken.None));
        var result=await service.ReadAsync(new(Auth(),null,At.AddHours(-1),At),CancellationToken.None);
        Assert.Equal(2,result.Targets.Count);Assert.Single(result.Evidence);Assert.Equal(1,result.ExcludedTargets);
    }

    [Fact]
    public async Task AlertsContinueBeyondFirstPageAndDeduplicateStableIdentities()
    {
        var target=Target(1);int calls=0;
        var alerts=OverviewStub.Create<IAlertQueryService>((_,args)=>
        {
            var cursor=(AlertActiveCursor?)args![3];Interlocked.Increment(ref calls);
            var rows=Enumerable.Range(cursor is null?1:7,cursor is null?7:3).Select(i=>new AlertActiveDto(new Guid(i,0,0,new byte[8]),Guid.NewGuid(),target.Target.TargetId,"Test rule",AlertState.Firing,At,null,null,null,"Observed",false)).ToArray();
            return ValueTask.FromResult(new AlertActivePage(rows,At,cursor is null?new(target.Target.TargetId,At,rows[^1].AlertId,At):null));
        });
        var result=await Service([target],alerts).ReadAsync(new(Auth(),null,At.AddHours(-1),At),CancellationToken.None);
        Assert.Equal(2,calls);Assert.Equal(9,Assert.Single(result.Evidence).ActiveAlerts.Value);Assert.Equal("current",result.Evidence[0].ActiveAlerts.State);
    }

    [Fact]
    public async Task WindowBoundsAndCancellationFailBeforeEvidenceIsReturned()
    {
        var service=Service([Target(1)]);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ReadAsync(new(Auth(),null,At,At),CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ReadAsync(new(Auth(),null,At.AddDays(-32),At),CancellationToken.None));
        using var ct=new CancellationTokenSource();ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ReadAsync(new(Auth(),null,At.AddHours(-1),At),ct.Token));
    }
}

public class OverviewStub : DispatchProxy
{
    private Func<MethodInfo?,object?[]?,object?>? handler;
    public static T Create<T>(Func<MethodInfo?,object?[]?,object?>? handler=null) where T:class
    {var value=Create<T,OverviewStub>();((OverviewStub)(object)value).handler=handler;return value;}
    protected override object? Invoke(MethodInfo? targetMethod,object?[]? args)
    {
        if(handler is not null)return handler(targetMethod,args);
        Type type=targetMethod!.ReturnType;
        if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(ValueTask<>))return Activator.CreateInstance(type);
        if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(Task<>))return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GenericTypeArguments[0]).Invoke(null,[null]);
        throw new InvalidOperationException("Unexpected stub method");
    }
}
