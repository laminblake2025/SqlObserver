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
        IOperationalHealthQueryService? operationsService = null,
        IOverviewLatestStatusRepositoryPort? latestStatus = null,
        IHealthProjectionQueryService? healthService = null)
    {
        var targets = OverviewStub.Create<IObservationTargetStatusQueryService>((_,args) =>
        {
            var query=(ListObservationTargetsQuery)args![0]!;
            return ValueTask.FromResult(new ObservationTargetStatusPage(inventory.Where(t=>query.Authorization.CanAccess(t.Target.TargetId)).ToArray(),null));
        });
        return new(targets, healthService??OverviewStub.Create<IHealthProjectionQueryService>(), alertService??OverviewStub.Create<IAlertQueryService>(),
            activityService??OverviewStub.Create<IActivityProjectionQueryService>(),OverviewStub.Create<IDeadlockProjectionQueryService>(),operationsService??OverviewStub.Create<IOperationalHealthQueryService>(),
            OverviewStub.Create<IMetricSeriesQueryService>(),history??OverviewStub.Create<IOverviewHistoryRepositoryPort>(),
            latestStatus??OverviewStub.Create<IOverviewLatestStatusRepositoryPort>(),
            OverviewStub.Create<IObservationTargetGroupingPort>((_, args) =>
            {
                var ids = (IReadOnlyList<Guid>)args![0]!;
                return Task.FromResult<IReadOnlyDictionary<Guid, ObservationTargetGrouping>>(
                    ids.ToDictionary(id => id, id => new ObservationTargetGrouping(id, null, [], 0)));
            }));
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
    public async Task OptInSelectedServerUsesLatestCurrentEvidenceAndStillReadsWindowedHistory()
    {
        var target = Target(1);
        int latestReads = 0, historyReads = 0, agentReads = 0, blockingHistoryReads = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var latest = OverviewStub.Create<IOverviewLatestStatusRepositoryPort>((_, args) =>
        {
            Interlocked.Increment(ref latestReads);
            Assert.Equal(target.Target.TargetId, (MonitoredInstanceId)args![0]!);
            Assert.Equal(1L, args[1]);
            Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)args[2]!).Offset);
            return Task.FromResult<OverviewLatestStatus?>(new(
                target.Target.TargetId.Value, 1, 4, now, "current", now,
                new(3, "current", now), new(2, "current", now),
                new Dictionary<string, OverviewLatestSource>
                {
                    ["alerts"] = new("current", now),
                    ["blocking"] = new("current", now)
                },
                [new(target.Target.TargetId.Value, "Old display name", "Cached alert",
                    "Evidence", "alerts", 1, now)],
                [new(target.Target.TargetId.Value, "Old display name", "TempDB used",
                    50, "%", "current", now)],
                ["Partial wait coverage"]));
        });
        var health = OverviewStub.Create<IHealthProjectionQueryService>((_, _) =>
            throw new InvalidOperationException("Current health must come from the latest row."));
        var alerts = OverviewStub.Create<IAlertQueryService>((_, _) =>
            throw new InvalidOperationException("Current alerts must come from the latest row."));
        var activity = OverviewStub.Create<IActivityProjectionQueryService>((method, _) =>
        {
            if (method!.Name != nameof(IActivityProjectionQueryService.ListBlockingHistoryAsync))
                throw new InvalidOperationException("Current activity must come from the latest row.");
            Interlocked.Increment(ref blockingHistoryReads);
            return ValueTask.FromResult<BlockingHistoryPage?>(null);
        });
        var operations = OverviewStub.Create<IOperationalHealthQueryService>((method, _) =>
        {
            if (method!.Name != nameof(IOperationalHealthQueryService.GetAgentFailuresAsync))
                throw new InvalidOperationException("Current operational health must come from the latest row.");
            Interlocked.Increment(ref agentReads);
            return ValueTask.FromResult<SqlAgentFailureSnapshot?>(null);
        });
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((_, _) =>
        {
            Interlocked.Increment(ref historyReads);
            return Task.FromResult<IReadOnlyList<OverviewSeries>>([]);
        });

        OverviewSnapshot result = await Service([target], alerts, history, activity,
            operations, latest, health).ReadAsync(new(Auth(), target.Target.TargetId.Value,
            At.AddHours(-1), At, true), CancellationToken.None);
        OverviewTargetEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal(1, latestReads);
        Assert.Equal(2, historyReads);
        Assert.Equal(1, agentReads);
        Assert.True(blockingHistoryReads > 0);
        Assert.Equal("current", evidence.CollectionState);
        Assert.Equal(3, evidence.ActiveAlerts.Value);
        Assert.Equal(2, evidence.BlockedSessions.Value);
        Assert.Equal("SQL 01", Assert.Single(evidence.Issues,
            issue => issue.Title == "Cached alert").Server);
        Assert.Equal("SQL 01", Assert.Single(evidence.Resources,
            resource => resource.Label == "TempDB used").Server);
        Assert.Contains("Partial wait coverage", evidence.Gaps);
    }

    [Fact]
    public async Task FleetLatestStatusReadsEachAuthorizedTargetAndKeepsWindowedHistory()
    {
        ObservationTargetStatusSnapshot[] inventory = [Target(1), Target(2)];
        var latestReads = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var historyReads = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var latest = OverviewStub.Create<IOverviewLatestStatusRepositoryPort>((_, args) =>
        {
            var id = (MonitoredInstanceId)args![0]!;
            latestReads.Add(id.Value);
            Assert.Contains(inventory, target => target.Target.TargetId == id);
            return Task.FromResult<OverviewLatestStatus?>(new(
                id.Value, 1, 1, DateTimeOffset.UtcNow, "current", DateTimeOffset.UtcNow,
                new(id == inventory[0].Target.TargetId ? 1 : 2, "current", At),
                new(0, "current", At),
                new Dictionary<string, OverviewLatestSource>
                {
                    ["alerts"] = new("current", At),
                    ["blocking"] = new("current", At)
                }, [], [], []));
        });
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((_, args) =>
        {
            var id = (MonitoredInstanceId)args![0]!;
            historyReads.Add(id.Value);
            return Task.FromResult<IReadOnlyList<OverviewSeries>>([
                new(id.Value, "", "engine.batch_requests_per_second", "batches/sec", "observed", null,
                    [new(At.AddMinutes(-5), 12, 1)])
            ]);
        });
        var health = OverviewStub.Create<IHealthProjectionQueryService>((_, _) =>
            throw new InvalidOperationException("Fleet current health must use the latest row."));
        var alerts = OverviewStub.Create<IAlertQueryService>((_, _) =>
            throw new InvalidOperationException("Fleet current alerts must use the latest row."));

        OverviewSnapshot result = await Service(inventory, alerts, history,
            latestStatus: latest, healthService: health).ReadAsync(
                new(Auth(), null, At.AddHours(-1), At, true), CancellationToken.None);

        Assert.Equal(inventory.Select(target => target.Target.TargetId.Value).Order(), latestReads.Order());
        Assert.Equal(inventory.Select(target => target.Target.TargetId.Value).Order(), historyReads.Order());
        Assert.Equal([1d, 2d], result.Evidence.OrderBy(item => item.DisplayName)
            .Select(item => item.ActiveAlerts.Value));
        Assert.All(result.Evidence, item =>
        {
            Assert.Single(item.Series, series => series.Metric == "engine.batch_requests_per_second");
            Assert.DoesNotContain(item.Gaps, gap => gap is "Collection health unavailable" or "Alerts unavailable");
        });
    }

    [Fact]
    public async Task MissingOptInProjectionFallsBackForSelectedServerAndFleet()
    {
        var target = Target(1);
        int healthReads = 0;
        var latest = OverviewStub.Create<IOverviewLatestStatusRepositoryPort>((_, _) =>
            Task.FromResult<OverviewLatestStatus?>(null));
        var health = OverviewStub.Create<IHealthProjectionQueryService>((method, _) =>
        {
            if (method!.Name == nameof(IHealthProjectionQueryService.GetInstanceAsync))
            {
                Interlocked.Increment(ref healthReads);
                return ValueTask.FromResult<InstanceHealthProjection?>(null);
            }
            return ValueTask.FromResult<DatabaseHealthPage?>(null);
        });
        OverviewQueryService service = Service([target], latestStatus: latest,
            healthService: health);
        OverviewSnapshot fleet = await service.ReadAsync(
            new(Auth(), null, At.AddHours(-1), At, true), CancellationToken.None);
        Assert.Contains("No latest status projection; direct source reads used",
            Assert.Single(fleet.Evidence).Gaps);
        OverviewSnapshot result = await service.ReadAsync(new(Auth(),
            target.Target.TargetId.Value, At.AddHours(-1), At, true), CancellationToken.None);
        Assert.Equal(2, healthReads);
        Assert.Contains("No latest status projection; direct source reads used",
            Assert.Single(result.Evidence).Gaps);
    }

    [Fact]
    public async Task WrongTargetLatestStatusFallsBackWithoutExposingItsIssues()
    {
        var target = Target(1);
        var foreign = Target(2);
        var latest = OverviewStub.Create<IOverviewLatestStatusRepositoryPort>((_, _) =>
            Task.FromResult<OverviewLatestStatus?>(new(
                target.Target.TargetId.Value, 1, 1, DateTimeOffset.UtcNow,
                "current", DateTimeOffset.UtcNow, new(1, "current", At),
                new(0, "current", At), new Dictionary<string, OverviewLatestSource>(),
                [new(foreign.Target.TargetId.Value, "Foreign", "Foreign alert", "Leak",
                    "alerts", 1, At)], [], [])));
        OverviewSnapshot result = await Service([target], latestStatus: latest)
            .ReadAsync(new(Auth(), target.Target.TargetId.Value,
                At.AddHours(-1), At, true), CancellationToken.None);

        OverviewTargetEvidence evidence = Assert.Single(result.Evidence);
        Assert.DoesNotContain(evidence.Issues, issue => issue.Title == "Foreign alert");
        Assert.Contains("Latest status unavailable", evidence.Gaps);
        Assert.Null(evidence.ActiveAlerts.Value);
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
    public async Task FleetUsesTargetSqlHistoryForCpuAndMemoryWithoutReturningFullSeries()
    {
        var target = Target(1);
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((method, _) =>
            Task.FromResult<IReadOnlyList<OverviewSeries>>(method!.Name == nameof(IOverviewHistoryRepositoryPort.ReadAsync)
                ? [
                    new(target.Target.TargetId.Value, "", "engine.sql_scheduler_cpu_percent", "%", "observed", null,
                        [new(At.AddMinutes(-20), 95, 1), new(At.AddMinutes(-5), 68.5, 3)]),
                    new(target.Target.TargetId.Value, "", "engine.os_available_memory_bytes", "GiB", "observed", null,
                        [new(At.AddMinutes(-20), 4.25, 2)])
                  ] : []));

        OverviewSnapshot result = await Service([target], history: history).ReadAsync(
            new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        OverviewTargetEvidence evidence = Assert.Single(result.Evidence);
        OverviewResource cpu = Assert.Single(evidence.Resources, item => item.Label == "SQL scheduler CPU · selected window");
        Assert.Equal(68.5, cpu.Value);
        Assert.Equal("observed", cpu.State);
        Assert.Equal(At.AddMinutes(-5), cpu.ObservedAtUtc);
        OverviewResource memory = Assert.Single(evidence.Resources, item => item.Label == "OS available memory via SQL · selected window");
        Assert.Equal(4.25, memory.Value);
        Assert.Equal("stale", memory.State);
        Assert.DoesNotContain(evidence.Series, item => item.Metric is
            "engine.sql_scheduler_cpu_percent" or "engine.os_available_memory_bytes");
    }

    [Fact]
    public async Task FleetDoesNotReplaceMissingLatestSqlCpuBucketWithAnOlderValue()
    {
        var target = Target(1);
        var history = OverviewStub.Create<IOverviewHistoryRepositoryPort>((_, _) =>
            Task.FromResult<IReadOnlyList<OverviewSeries>>([
                new(target.Target.TargetId.Value, "", "engine.sql_scheduler_cpu_percent", "%", "observed", null,
                    [new(At.AddMinutes(-20), 95, 1), new(At.AddMinutes(-5), null, 0)])
            ]));

        OverviewSnapshot result = await Service([target], history: history).ReadAsync(
            new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        OverviewResource cpu = Assert.Single(Assert.Single(result.Evidence).Resources,
            item => item.Label == "SQL scheduler CPU · selected window");
        Assert.Null(cpu.Value);
        Assert.Equal("no_data", cpu.State);
        Assert.Equal(At.AddMinutes(-5), cpu.ObservedAtUtc);
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
    public async Task AgentJobFailureCountsDeduplicateOutcomesAndExcludeStepDiagnostics()
    {
        var target = Target(1);
        Guid failedJob = Guid.NewGuid(), diagnosticOnlyJob = Guid.NewGuid();
        SqlAgentFailureObservation firstOutcome = AgentObservation(target, failedJob, 1, 0, 0, AgentFailureKind.Failed);
        SqlAgentFailureObservation[] observations =
        [
            firstOutcome,
            firstOutcome,
            AgentObservation(target, failedJob, 2, 0, 0, AgentFailureKind.Failed),
            AgentObservation(target, failedJob, 3, 1, 0, AgentFailureKind.Failed),
            AgentObservation(target, diagnosticOnlyJob, 4, 1, 0, AgentFailureKind.Failed),
            AgentObservation(target, diagnosticOnlyJob, 5, 1, 2, AgentFailureKind.Retry),
            AgentObservation(target, diagnosticOnlyJob, 6, 0, 3, AgentFailureKind.Cancelled),
        ];

        OverviewTargetEvidence evidence = await ReadAgentEvidenceAsync(target, observations);
        OverviewResource failures = Assert.Single(evidence.Resources, resource => resource.Label == "SQL Agent failures");
        Assert.Equal(2d, failures.Value);
        OverviewIssue issue = Assert.Single(evidence.Issues, item => item.Title == "SQL Agent failures");
        Assert.Equal("1 jobs affected in the selected window", issue.Detail);
        Assert.Equal("operations", issue.Destination);
    }

    [Theory]
    [InlineData(1, 0, AgentFailureKind.Failed)]
    [InlineData(1, 2, AgentFailureKind.Retry)]
    [InlineData(0, 3, AgentFailureKind.Cancelled)]
    [InlineData(0, 0, AgentFailureKind.Retry)]
    [InlineData(0, 2, AgentFailureKind.Failed)]
    public async Task AgentJobFailureCountsDoNotPromoteDiagnosticsOrInconsistentOutcomes(int stepId, int runStatus, AgentFailureKind kind)
    {
        var target = Target(1);
        SqlAgentFailureObservation observation = AgentObservation(target, Guid.NewGuid(), 1, stepId, runStatus, kind);

        OverviewTargetEvidence evidence = await ReadAgentEvidenceAsync(target, [observation]);

        Assert.Equal(0d, Assert.Single(evidence.Resources, resource => resource.Label == "SQL Agent failures").Value);
        Assert.DoesNotContain(evidence.Issues, issue => issue.Title == "SQL Agent failures");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "more-agent-records")]
    public async Task AgentJobFailureCountsRetainPartialCoverage(bool truncated, string? nextCursor)
    {
        var target = Target(1);
        SqlAgentFailureObservation stepFailure = AgentObservation(target, Guid.NewGuid(), 1, 1, 0, AgentFailureKind.Failed);

        OverviewTargetEvidence evidence = await ReadAgentEvidenceAsync(target, [stepFailure], truncated, nextCursor);

        OverviewResource failures = Assert.Single(evidence.Resources, resource => resource.Label == "SQL Agent failures");
        Assert.Equal(0d, failures.Value);
        Assert.Equal("partial", failures.State);
        Assert.DoesNotContain(evidence.Issues, issue => issue.Title == "SQL Agent failures");
    }

    private static SqlAgentFailureObservation AgentObservation(ObservationTargetStatusSnapshot target, Guid jobId,
        long historyId, int stepId, int runStatus, AgentFailureKind kind) =>
        new(target.Target.TargetId, target.Target.Revision, jobId, historyId, stepId, runStatus, kind,
            null, null, runStatus == 2 ? 1 : 0, 3, At,
            SqlAgentFailureIdentity.Compute(1, target.Target.TargetId, target.Target.Revision, jobId, historyId, stepId, runStatus));

    private static async Task<OverviewTargetEvidence> ReadAgentEvidenceAsync(ObservationTargetStatusSnapshot target,
        SqlAgentFailureObservation[] observations, bool truncated = false, string? nextCursor = null)
    {
        var snapshot = new SqlAgentFailureSnapshot(target.Target.TargetId, target.Target.Revision, new CollectorRunId(Guid.NewGuid()),
            At, OperationalObservationState.Complete, observations, observations.Length, truncated, At.AddHours(-1), At)
        { NextCursor = nextCursor };
        var operations = OverviewStub.Create<IOperationalHealthQueryService>((method, _) => method!.Name switch
        {
            nameof(IOperationalHealthQueryService.GetAgentFailuresAsync) => ValueTask.FromResult<SqlAgentFailureSnapshot?>(snapshot),
            nameof(IOperationalHealthQueryService.GetBackupsAsync) => ValueTask.FromResult<BackupStatusSnapshot?>(null),
            nameof(IOperationalHealthQueryService.GetTempDbAsync) => ValueTask.FromResult<TempDbSnapshot?>(null),
            nameof(IOperationalHealthQueryService.GetAvailabilityGroupReplicasAsync) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(null),
            _ => throw new InvalidOperationException($"Unexpected operational health method {method.Name}")
        });
        OverviewSnapshot result = await Service([target], operationsService: operations)
            .ReadAsync(new(Auth(), null, At.AddHours(-1), At), CancellationToken.None);
        return Assert.Single(result.Evidence);
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
