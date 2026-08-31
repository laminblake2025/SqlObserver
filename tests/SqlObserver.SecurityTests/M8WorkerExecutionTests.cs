using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.SecurityTests;

public sealed class M8WorkerExecutionTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("3d4dd8f4-8f24-43ef-b3bb-ff603b1520b1"));
    private static readonly Guid RuleId = Guid.Parse("93df5d63-bd4c-472a-a0b0-0f7f2cc4e4a4");
    private static readonly WorkerLeaseIdentity Lease = new(new WorkerLeaseKey("alerts/evaluation/reconcile"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));

    [Fact]
    public async Task AlertEvaluationWorkerRunsProductionCycleAndPersistsDecision()
    {
        DateTimeOffset observed = MicrosecondUtcNow();
        var repository = new WorkerRepository { EvaluationWork = [new AlertEvaluationWork(Guid.NewGuid(), [new AlertObservation(Target, RuleId, observed, 90, null, "sample")], observed)] };
        using var worker = new AlertEvaluationWorker(new FakeEvaluationSource(repository), repository, new FakeLeases(Lease), new WorkerExecutionId(Guid.NewGuid()), NullLogger<AlertEvaluationWorker>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cancellation.Token);
        await repository.EvaluationPersisted.Task.WaitAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, repository.PersistCount);
    }

    [Fact]
    public async Task AlertDeliveryWorkerRunsProductionCycleAndCompletesOutboxItem()
    {
        var repository = new WorkerRepository { DeliveryWork = [new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", "SqlObserver Alerts", [1], 0, MicrosecondUtcNow(), Target)] };
        using var worker = new AlertDeliveryWorker(repository, new FakeDispatcher(), new FakeLeases(Lease), new WorkerExecutionId(Guid.NewGuid()), NullLogger<AlertDeliveryWorker>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cancellation.Token);
        await repository.DeliveryCompleted.Task.WaitAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, repository.CompleteCount);
    }

    [Fact]
    public async Task AlertDeliveryWorkerReleasesWhenLeaseRenewalFaults()
    {
        var repository = new WorkerRepository { DeliveryWork = [] };
        var leases = new FakeLeases(Lease, throwOnRenew: true);
        using var worker = new AlertDeliveryWorker(repository, new FakeDispatcher(), leases, new WorkerExecutionId(Guid.NewGuid()), NullLogger<AlertDeliveryWorker>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cancellation.Token);
        await leases.Released.Task.WaitAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);
        Assert.True(leases.ReleaseCount > 0);
    }

    [Fact]
    public async Task AlertDeliveryWorkerHoldsDispatchPermitAcrossAdapterSideEffect()
    {
        var repository = new WorkerRepository { DeliveryWork = [new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", "SqlObserver Alerts", [1], 0, MicrosecondUtcNow(), Target)] };
        using var worker = new AlertDeliveryWorker(repository, new PermitDispatcher(repository), new FakeLeases(Lease), new WorkerExecutionId(Guid.NewGuid()), NullLogger<AlertDeliveryWorker>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cancellation.Token);
        await repository.DeliveryCompleted.Task.WaitAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(repository.PermitObserved);
        Assert.False(repository.PermitHeld);
    }

    [Fact]
    public async Task AlertDeliveryWorkerFinalizesAdapterFailureBeforePermitRelease()
    {
        var repository = new WorkerRepository { DeliveryWork = [new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", "SqlObserver Alerts", [1], 0, MicrosecondUtcNow(), Target)] };
        using var worker = new AlertDeliveryWorker(repository, new ThrowingDispatcher(), new FakeLeases(Lease), new WorkerExecutionId(Guid.NewGuid()), NullLogger<AlertDeliveryWorker>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cancellation.Token);
        await repository.DeliveryCompleted.Task.WaitAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);
        Assert.True(repository.CompletionBeforePermitRelease);
        Assert.False(repository.PermitHeld);
    }

    [Fact]
    public async Task AlertDeliveryWorkerPreservesAdapterFailureWhenLeaseReleaseFails()
    {
        var repository = new WorkerRepository { DeliveryWork = [new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", "SqlObserver Alerts", [1], 0, MicrosecondUtcNow(), Target)], CompletionException = new InvalidOperationException("recovery fault") };
        var leases = new FakeLeases(Lease, releaseException: new InvalidOperationException("release transport fault"), completionProbe: () => repository.CompleteCount > 0);
        var logger = new CapturingLogger<AlertDeliveryWorker>();
        using var worker = new AlertDeliveryWorker(repository, new ThrowingDispatcher(), leases, new WorkerExecutionId(Guid.NewGuid()), logger);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cancellation.Token);
        await repository.DeliveryCompleted.Task.WaitAsync(cancellation.Token);
        await leases.Released.Task.WaitAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);
        Assert.True(leases.ReleaseSawCompletion);
        Assert.True(repository.CompleteCount > 0);
        int[] eventIds = logger.EventIds.ToArray();
        Assert.Contains(8102, eventIds);
        Assert.Contains(8103, eventIds);
        int primaryIndex = Array.IndexOf(eventIds, 8102);
        int releaseIndex = Array.IndexOf(eventIds, 8103);
        Assert.True(primaryIndex < releaseIndex);
    }

    [Fact]
    public async Task AlertDeliveryWorkerTracksReleaseOnlyCancellationWithoutUsingShutdownToken()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new WorkerRepository { DeliveryWork = [new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "windows-event-log", "SqlObserver Alerts", [1], 0, MicrosecondUtcNow(), Target)] };
        var leases = new FakeLeases(Lease, releaseException: new OperationCanceledException("release cancellation"));
        var logger = new CapturingLogger<AlertDeliveryWorker>();
        using var worker = new AlertDeliveryWorker(repository, new CancellationDispatcher(started), leases, new WorkerExecutionId(Guid.NewGuid()), logger);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cancellation.Token);
        await started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await leases.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await worker.StopAsync(CancellationToken.None);
        Assert.True(leases.ReleaseReceivedLiveToken);
        Assert.True(leases.AcquireToken is { IsCancellationRequested: true });
        Assert.True(leases.ReleaseToken is { CanBeCanceled: true, IsCancellationRequested: false });
        Assert.NotEqual(leases.AcquireToken!.Value, leases.ReleaseToken!.Value);
        Assert.DoesNotContain(8102, logger.EventIds);
        Assert.Contains(8103, logger.EventIds);
    }

    [Fact]
    public async Task CollectorProductionDiPinsConfiguredDestinationBeforeSocketConnect()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            ["AlertDestinations:primary"] = "https://alerts.example.test/hook",
            ["AlertDestinations:primary:Kind"] = "https-webhook",
            ["AlertDestinations:primary:Allowlist"] = "93.184.216.0/24",
        }).Build();
        var dns = new SequenceDns(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.4"));
        var services = new ServiceCollection();
        services.AddSqlObserverCollectorRuntime(configuration);
        services.AddSingleton<IAlertDnsResolver>(dns);
        using ServiceProvider provider = services.BuildServiceProvider();
        HttpsWebhookAlertDestination adapter = provider.GetRequiredService<HttpsWebhookAlertDestination>();

        AlertDeliveryResult result = await adapter.DeliverAsync(new AlertDeliveryWork(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "https-webhook", "primary", [1], 0, DateTimeOffset.UtcNow, Target), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("transport_failure", result.Reason);
    }

    private static DateTimeOffset MicrosecondUtcNow() { DateTimeOffset now = DateTimeOffset.UtcNow; return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero); }
    private sealed class FakeEvaluationSource(WorkerRepository repository) : IAlertEvaluationSource
    { private int _reads; public ValueTask<IReadOnlyList<AlertEvaluationWork>> ReadAsync(WorkerLeaseIdentity lease, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertEvaluationWork>>(Interlocked.Exchange(ref _reads, 1) == 0 ? repository.EvaluationWork : []); }
    private sealed class FakeLeases(WorkerLeaseIdentity identity, bool throwOnRenew = false, Exception? releaseException = null, Func<bool>? completionProbe = null) : IWorkerLeasePort
    {
        private readonly bool _throwOnRenew = throwOnRenew;
        private readonly Exception? _releaseException = releaseException;
        private readonly Func<bool>? _completionProbe = completionProbe;
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseCount { get; private set; }
        public bool ReleaseSawCompletion { get; private set; }
        public bool ReleaseReceivedLiveToken { get; private set; }
        public CancellationToken? AcquireToken { get; private set; }
        public CancellationToken? ReleaseToken { get; private set; }
        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken cancellationToken) { AcquireToken = cancellationToken; return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(new WorkerLease(identity, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), DateTimeOffset.UtcNow)); }
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken cancellationToken) => _throwOnRenew ? throw new InvalidOperationException("renewal fault") : ValueTask.FromResult(LeaseRenewalResult.Renewed(new WorkerLease(identity, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), DateTimeOffset.UtcNow));
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            ReleaseCount++;
            ReleaseToken = cancellationToken;
            ReleaseSawCompletion = _completionProbe?.Invoke() ?? false;
            ReleaseReceivedLiveToken = !cancellationToken.IsCancellationRequested;
            Released.TrySetResult();
            if (_releaseException is not null) throw _releaseException;
            return ValueTask.FromResult(LeaseReleaseStatus.Released);
        }
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(LeaseOwnershipStatus.Current);
    }
    private sealed class FakeDispatcher : IAlertDestinationPort { public ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken) => ValueTask.FromResult(new AlertDeliveryResult(work.DeliveryId, true, false, "delivered", DateTimeOffset.UtcNow, work.TargetId, 202, 1)); }
    private sealed class ThrowingDispatcher : IAlertDestinationPort { public ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken) => throw new InvalidOperationException("adapter failure"); }
    private sealed class CancellationDispatcher(TaskCompletionSource started) : IAlertDestinationPort
    {
        public async ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation dispatcher should not complete normally.");
        }
    }
    private sealed class SequenceDns(IPAddress first, IPAddress second) : IAlertDnsResolver
    {
        private int calls;
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([Interlocked.Increment(ref calls) == 1 ? first : second]);
    }
    private sealed class PermitDispatcher(WorkerRepository repository) : IAlertDestinationPort
    {
        public ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken)
        {
            repository.PermitObserved = repository.PermitHeld;
            return ValueTask.FromResult(new AlertDeliveryResult(work.DeliveryId, true, false, "delivered", DateTimeOffset.UtcNow, work.TargetId, 202, 1));
        }
    }
    private sealed class WorkerRepository : IAlertRepositoryPort
    {
        public IReadOnlyList<AlertEvaluationWork> EvaluationWork { get; init; } = [];
        public IReadOnlyList<AlertDeliveryWork> DeliveryWork { get; init; } = [];
        public TaskCompletionSource EvaluationPersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeliveryCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PersistCount { get; private set; }
        public int CompleteCount { get; private set; }
        public Exception? CompletionException { get; init; }
        public bool PermitHeld { get; set; }
        public bool PermitObserved { get; set; }
        public bool CompletionBeforePermitRelease { get; private set; }
        public ValueTask<IReadOnlyList<AlertEvaluationWork>> ClaimDueEvaluationsAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(EvaluationWork);
        public ValueTask<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(MonitoredInstanceId targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertRuleDefinition>>([new AlertRuleDefinition(RuleId, "cpu.high", AlertRuleKind.MetricThreshold, new MetricId("cpu.percent"), AlertComparison.GreaterThanOrEqual, 80, 5, 1, TimeSpan.Zero, TimeSpan.FromSeconds(15))]);
        public ValueTask<AlertRuleState?> GetStateAsync(MonitoredInstanceId targetId, Guid ruleId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<AlertRuleState?>(new AlertRuleState(ruleId, targetId));
        public ValueTask<AlertEvaluationOutcome> EvaluateAndPersistAsync(AlertEvaluationBatch request, CancellationToken cancellationToken) { PersistCount++; EvaluationPersisted.TrySetResult(); return ValueTask.FromResult(new AlertEvaluationOutcome(request.Observations.Count, request.Observations.Count, 0, DateTimeOffset.UtcNow)); }
        public ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(MonitoredInstanceId targetId, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertActiveDto>>([]);
        public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(MaintenanceWriteRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AlertAcknowledgeRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAdminAsync(AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(DeliveryWork);
        public ValueTask<AlertDeliveryResult> CompleteDeliveryAsync(AlertDeliveryResult result, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            CompleteCount++;
            DeliveryCompleted.TrySetResult();
            if (CompletionException is not null) throw CompletionException;
            return ValueTask.FromResult(result);
        }
        public ValueTask<AlertDeliveryLeaseOutcome> RenewDeliveryStateAsync(AlertDeliveryWork work, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(AlertDeliveryLeaseOutcome.Active);
        public ValueTask<IAlertDeliveryDispatchPermit> AcquireDeliveryDispatchPermitAsync(AlertDeliveryWork work, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            PermitHeld = true;
            return ValueTask.FromResult<IAlertDeliveryDispatchPermit>(new TestPermit(this));
        }
        private sealed class TestPermit(WorkerRepository repository) : IAlertDeliveryDispatchPermit
        {
            public ValueTask DisposeAsync() { repository.CompletionBeforePermitRelease = repository.CompleteCount > 0; repository.PermitHeld = false; return ValueTask.CompletedTask; }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<int> EventIds { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => EventIds.Enqueue(eventId.Id);
        private sealed class NoopScope : IDisposable
        {
            public static NoopScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
