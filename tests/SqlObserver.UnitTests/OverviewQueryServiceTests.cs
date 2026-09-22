using System.Reflection;
using SqlObserver.Domain.Auditing;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

// DispatchProxy requires boxing the ValueTask return; the service awaits each returned instance once.
#pragma warning disable CA2012

public sealed class OverviewQueryServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] BackgroundWaitTypes =
    [
        "SOS_WORK_DISPATCHER", "LOGMGR_QUEUE", "BROKER_TASK_STOP", "SQLTRACE_INCREMENTAL_FLUSH_SLEEP",
        "HADR_FILESTREAM_IOMGR_IOCOMPLETION", "DISPATCHER_QUEUE_SEMAPHORE", "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP",
        "BROKER_TO_FLUSH", "CHECKPOINT_QUEUE", "DIRTY_PAGE_POLL", "REQUEST_FOR_DEADLOCK_SEARCH"
    ];
    private static AuthorizationContext Auth(params MonitoredInstanceId[] ids) => new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active,
        [ApplicationRole.Viewer], ids.Length == 0 ? TargetAuthorizationScope.ForAllTargets() : TargetAuthorizationScope.ForTargets(ids));
    private static ObservationTargetStatusSnapshot Target(int index, ObservationTargetLifecycle lifecycle = ObservationTargetLifecycle.Active)
    {
        var guid = new Guid(index, 0, 0, new byte[8]);
        return new(new(new(guid), new($"sql{index}"), new($"SQL {index:D2}"), new(new(new("sql.example.test"), tcpPort:1433), new(TimeSpan.FromSeconds(5))), lifecycle, new(1), At, At, At), null);
    }
    private static OverviewQueryService Service(IReadOnlyList<ObservationTargetStatusSnapshot> inventory, IAlertQueryService? alertService = null,
        IOverviewHistoryRepositoryPort? history = null, IActivityProjectionQueryService? activityService = null,
        IObservationTargetStatusQueryService? targetService = null, IMetricSeriesQueryService? metricService = null)
    {
        var targets = OverviewStub.Create<IObservationTargetStatusQueryService>((_,args) =>
        {
            var query=(ListObservationTargetsQuery)args![0]!;
            return ValueTask.FromResult(new ObservationTargetStatusPage(inventory.Where(t=>query.Authorization.CanAccess(t.Target.TargetId)).ToArray(),null));
        });
        return new(targetService ?? targets, OverviewStub.Create<IHealthProjectionQueryService>(), alertService??OverviewStub.Create<IAlertQueryService>(),
            activityService??OverviewStub.Create<IActivityProjectionQueryService>(),OverviewStub.Create<IDeadlockProjectionQueryService>(),OverviewStub.Create<IOperationalHealthQueryService>(),
            metricService??OverviewStub.Create<IMetricSeriesQueryService>(),history??OverviewStub.Create<IOverviewHistoryRepositoryPort>());
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
    public async Task DatabaseActivityHistoryIsReadOnlyForAnExplicitServerSelection()
    {
        var target = Target(1);
        int databaseReads = 0;
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((method, _) =>
        {
            if (method!.Name == nameof(IOverviewHistoryRepositoryPort.ReadDatabaseActivityAsync))
            {
                Interlocked.Increment(ref databaseReads);
                return Task.FromResult<IReadOnlyList<OverviewSeries>>([
                    new(target.Target.TargetId.Value, "", "activity.user_sessions", "sessions", "observed", "Orders · database 5", [new(At, 2, 1)])
                ]);
            }

            return Task.FromResult<IReadOnlyList<OverviewSeries>>([]);
        });

        var selected = await Service([target], history: history).ReadAsync(
            new(Auth(), target.Target.TargetId.Value, At.AddHours(-1), At), CancellationToken.None);
        Assert.Equal(1, databaseReads);
        Assert.Contains(Assert.Single(selected.Evidence).Series, item => item.Metric == "activity.user_sessions");

        await Service([target], history: history).ReadAsync(
            new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        Assert.Equal(1, databaseReads);
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
    public async Task SlowSourceDoesNotPreventIndependentHistoryAndReturnsUnavailableEvidence()
    {
        var historyRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool canceled = false;
        async ValueTask<AlertActivePage> SlowAlerts(CancellationToken token)
        {
            // History must be dispatched while the earlier alert source is still pending.
            await historyRead.Task.WaitAsync(token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled = true; throw; }
            throw new InvalidOperationException();
        }
        var alerts = OverviewStub.Create<IAlertQueryService>((_, args) => SlowAlerts((CancellationToken)args![4]!));
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((_, _) =>
        {
            historyRead.TrySetResult();
            return Task.FromResult<IReadOnlyList<OverviewSeries>>([]);
        });
        var result = await Service([Target(1)], alerts, history).ReadAsync(new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        Assert.True(canceled);
        var evidence = Assert.Single(result.Evidence);
        Assert.Null(evidence.ActiveAlerts.Value);
        Assert.Contains("Alerts unavailable", evidence.Gaps);
        Assert.DoesNotContain("SQL workload history unavailable", evidence.Gaps);
    }

    [Fact]
    public async Task CallerCancellationStillEscapesComposition()
    {
        using var caller = new CancellationTokenSource();
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((_, _) =>
        {
            caller.Cancel();
            return Task.FromCanceled<IReadOnlyList<OverviewSeries>>(caller.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service([Target(1)], history: history).ReadAsync(new(Auth(), null, At.AddHours(-1), At), caller.Token));
    }

    [Fact]
    public async Task InventoryHasOneFiveSecondBudgetAcrossPages()
    {
        int calls = 0;
        bool cancelled = false;
        async ValueTask<ObservationTargetStatusPage> ReadPage(CancellationToken token)
        {
            calls++;
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            var target = Target(calls);
            return new([target], new ObservationTargetListCursor(target.Target.Key, target.Target.TargetId));
        }
        var targets = OverviewStub.Create<IObservationTargetStatusQueryService>((_, args) => ReadPage((CancellationToken)args![1]!));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service([], targetService: targets).ReadAsync(new(Auth(), null, At.AddHours(-1), At), CancellationToken.None));
        Assert.True(cancelled);
        Assert.Equal(2, calls);
        Assert.InRange(timer.Elapsed.TotalSeconds, 4, 8);
    }

    [Fact]
    public async Task TenTargetEvidenceBudgetReturnsPartialResultsAndCancelsAllInFlightReads()
    {
        int active = 0, cancelled = 0;
        async ValueTask<MetricSeriesPage> ReadSlowMetrics(CancellationToken token)
        {
            Interlocked.Increment(ref active);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            finally { Interlocked.Decrement(ref active); }
            throw new InvalidOperationException();
        }
        var metrics = OverviewStub.Create<IMetricSeriesQueryService>((_, args) => ReadSlowMetrics((CancellationToken)args![1]!));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await Service(Enumerable.Range(1, 10).Select(index => Target(index)).ToArray(), metricService: metrics)
            .ReadAsync(new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        Assert.InRange(timer.Elapsed.TotalSeconds, 19, 27);
        Assert.Equal(10, result.Evidence.Count);
        Assert.Equal(0, active);
        Assert.True(cancelled > 0);
        Assert.All(result.Evidence, evidence => Assert.Contains("host.cpu.percent unavailable", evidence.Gaps));
    }

    [Fact]
    public async Task TenServerScopeSharesOneEightReadConcurrencyLimit()
    {
        var reachedLimit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new System.Collections.Concurrent.ConcurrentBag<int>();
        int active = 0, calls = 0;
        async Task<IReadOnlyList<OverviewSeries>> HoldHistory(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            int concurrent = Interlocked.Increment(ref active);
            observed.Add(concurrent);
            if (concurrent == 8) reachedLimit.TrySetResult();
            try { await release.Task.WaitAsync(token); return []; }
            finally { Interlocked.Decrement(ref active); }
        }
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((_, args) => HoldHistory((CancellationToken)args![5]!));
        var read = Service(Enumerable.Range(1, 10).Select(i => Target(i)).ToArray(), history: history)
            .ReadAsync(new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        try { await reachedLimit.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { release.TrySetResult(); }
        var result = await read;
        Assert.Equal(10, calls);
        Assert.Equal(8, observed.Max());
        Assert.Equal(10, result.Evidence.Count);
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

    [Fact]
    public async Task KnownBackgroundWaitsAreExcludedBeforeTopFiveRanking()
    {
        var target = Target(1);
        var run = new CollectorRunId(Guid.NewGuid());
        var revision = new ObservationTargetRevision(1);
        var evidence = new ActivitySnapshotEvidence(target.Target.TargetId, run, revision, new CollectorId("waits.server"),
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, CollectorLossEvidence.None, At);
        static ServerWaitSummaryItem Wait(string waitType, long delta) => new(new SqlServerWaitType(waitType), 1, delta, delta, delta,
            true, false, 1, delta, 1, At);
        var background = BackgroundWaitTypes.Select((name, index) => Wait(name, 100_000 - index)).ToArray();
        var page = new ServerWaitSummaryPage(target.Target.TargetId, evidence, null,
            [.. background, Wait("LCK_M_X", 500), Wait("PAGEIOLATCH_SH", 400), Wait("XE_FILE_TARGET_TVF", 300)], null, At);
        var activity = OverviewStub.Create<IActivityProjectionQueryService>((method, _) => method!.Name switch
        {
            nameof(IActivityProjectionQueryService.ListWaitSummaryAsync) => ValueTask.FromResult<ServerWaitSummaryPage?>(page),
            nameof(IActivityProjectionQueryService.ListCurrentBlockingAsync) => ValueTask.FromResult<CurrentBlockingPage?>(null),
            _ => throw new InvalidOperationException($"Unexpected activity method {method.Name}")
        });

        var result = await Service([target], activityService: activity).ReadAsync(new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        var resources = Assert.Single(result.Evidence).Resources.Where(resource => resource.Label.StartsWith("Wait ·", StringComparison.Ordinal)).ToArray();

        Assert.Contains(resources, resource => resource.Label == "Wait · LCK_M_X");
        Assert.Contains(resources, resource => resource.Label == "Wait · PAGEIOLATCH_SH");
        Assert.Contains(resources, resource => resource.Label == "Wait · XE_FILE_TARGET_TVF");
        Assert.DoesNotContain(resources, resource => BackgroundWaitTypes.Any(wait => resource.Label == $"Wait · {wait}"));
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
