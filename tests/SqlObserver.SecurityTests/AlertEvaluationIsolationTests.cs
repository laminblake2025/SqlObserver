using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.SecurityTests;

public sealed class AlertEvaluationIsolationTests
{
    private static readonly MonitoredInstanceId FailedTarget = new(Guid.Parse("67000000-0000-4000-8000-000000000001"));
    private static readonly MonitoredInstanceId SecondTarget = new(Guid.Parse("67000000-0000-4000-8000-000000000002"));
    private static readonly MonitoredInstanceId ThirdTarget = new(Guid.Parse("67000000-0000-4000-8000-000000000003"));
    private static readonly Guid RuleId = Guid.Parse("68000000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Observed = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkerLeaseIdentity Lease = new(new WorkerLeaseKey("alerts/evaluation/reconcile"),
        new WorkerExecutionId(Guid.Parse("69000000-0000-4000-8000-000000000001")), new FencingToken(73));

    public enum FailurePoint { None, Rules, Maintenance, State, Persist }
    public enum RenewalBehavior { Healthy, OwnershipLost, Fault }

    [Theory]
    [InlineData(FailurePoint.Rules, false)]
    [InlineData(FailurePoint.Maintenance, false)]
    [InlineData(FailurePoint.State, false)]
    [InlineData(FailurePoint.Persist, false)]
    [InlineData(FailurePoint.Rules, true)]
    [InlineData(FailurePoint.Maintenance, true)]
    [InlineData(FailurePoint.State, true)]
    [InlineData(FailurePoint.Persist, true)]
    public async Task FirstTargetFailureDoesNotDiscardHealthyTargetsInTheSameClaimCycle(FailurePoint failurePoint, bool timeout)
    {
        var repository = new EvaluationRepository(failurePoint, timeout);
        var source = new OneCycleSource(CreateWork());
        var leases = new CapturingLeases();
        using var worker = CreateWorker(source, repository, leases);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await worker.StartAsync(deadline.Token);
            // Both the old abort-on-first-error path and the intended complete
            // first cycle release the lease. This makes RED immediate rather
            // than waiting for the worker's 15-second next-cycle delay.
            await leases.Released.Task.WaitAsync(deadline.Token);
        }
        finally { await worker.StopAsync(CancellationToken.None); }

        Assert.Single(repository.Faults);
        Assert.False(repository.Faults.Single().TokenWasCancelled);
        Assert.Equal(new[] { FailedTarget, SecondTarget, ThirdTarget }, repository.RuleLookups.ToArray());
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(Lease, Assert.Single(source.Leases));
        Assert.Equal(1, leases.AcquireCount);
        AssertCleanRelease(leases);

        AlertEvaluationBatch[] persisted = repository.Persisted.ToArray();
        Assert.Equal(2, persisted.Length);
        AssertBatch(persisted[0], SecondTarget, source.Work);
        AssertBatch(persisted[1], ThirdTarget, source.Work);
        Assert.DoesNotContain(persisted, batch => batch.Observations.Any(observation => observation.TargetId == FailedTarget));

        // A shared rule ID exercises both within-target evolution and separation
        // between targets. The second target gets two ordered observations;
        // the third target must start its own Pending state.
        Assert.Equal(new[] { AlertState.Pending, AlertState.Firing }, persisted[0].Decisions!.Select(decision => decision.State.State));
        Assert.Equal(AlertState.Pending, Assert.Single(persisted[1].Decisions!).State.State);
        Assert.Equal(1, Assert.Single(persisted[1].Decisions!).State.ConsecutiveMatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationStopsLaterTargetsAndReleasesExactLease(bool returnAfterCancellation)
    {
        var repository = new EvaluationRepository(blockFirstTarget: true, returnAfterCancellation: returnAfterCancellation);
        var source = new OneCycleSource(CreateWork());
        var leases = new CapturingLeases(() => repository.BlockedCallExited);
        using var worker = CreateWorker(source, repository, leases);
        using var caller = new CancellationTokenSource();
        try
        {
            await worker.StartAsync(caller.Token);
            await repository.FirstTargetBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await leases.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await worker.StopAsync(CancellationToken.None); }

        AssertCancelledCycle(repository, source, leases);
        Assert.Empty(leases.Renewals);
    }

    [Theory]
    [InlineData(RenewalBehavior.OwnershipLost)]
    [InlineData(RenewalBehavior.Fault)]
    public async Task RenewalLossCancelsCurrentTargetAndDoesNotEvaluateLaterTargets(RenewalBehavior renewalBehavior)
    {
        var repository = new EvaluationRepository(blockFirstTarget: true);
        var source = new OneCycleSource(CreateWork());
        var leases = new CapturingLeases(() => repository.BlockedCallExited, renewalBehavior);
        using var worker = CreateWorker(source, repository, leases);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await repository.FirstTargetBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Production currently owns a real 10-second renewal timer. Wait on
            // release, not a fixed sleep; no scheduling/clock redesign is needed.
            await leases.Released.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { await worker.StopAsync(CancellationToken.None); }

        AssertCancelledCycle(repository, source, leases);
        Assert.Equal(Lease, Assert.Single(leases.Renewals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceClaimFailureStillAbortsTheWholeCycle(bool timeout)
    {
        Exception failure = timeout ? new OperationCanceledException("synthetic claim timeout", new CancellationToken(canceled: true))
            : new InvalidOperationException("synthetic claim failure");
        var repository = new EvaluationRepository();
        var source = new OneCycleSource(CreateWork(), failure);
        var leases = new CapturingLeases();
        using var worker = CreateWorker(source, repository, leases);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await leases.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await worker.StopAsync(CancellationToken.None); }

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(Lease, Assert.Single(source.Leases));
        Assert.Empty(repository.RuleLookups);
        Assert.Empty(repository.PersistAttempts);
        Assert.Empty(repository.Persisted);
        AssertCleanRelease(leases);
    }

    private static AlertEvaluationWorker CreateWorker(OneCycleSource source, EvaluationRepository repository, CapturingLeases leases) =>
        new(source, repository, leases, Lease.Owner, NullLogger<AlertEvaluationWorker>.Instance);

    private static AlertEvaluationWork[] CreateWork() =>
    [
        Work(FailedTarget, Observed, Guid.Parse("6a000000-0000-4000-8000-000000000001")),
        Work(SecondTarget, Observed.AddSeconds(15), Guid.Parse("6a000000-0000-4000-8000-000000000002")),
        Work(ThirdTarget, Observed, Guid.Parse("6a000000-0000-4000-8000-000000000003")),
        Work(SecondTarget, Observed, Guid.Parse("6a000000-0000-4000-8000-000000000004")),
    ];

    private static AlertEvaluationWork Work(MonitoredInstanceId target, DateTimeOffset at, Guid operationId) =>
        new(operationId, [new AlertObservation(target, RuleId, at, 90, null, "sample", operationId)], at,
            Lease.Key.Value, Lease.Owner, Lease.FencingToken);

    private static void AssertBatch(AlertEvaluationBatch batch, MonitoredInstanceId target, IReadOnlyList<AlertEvaluationWork> work)
    {
        AlertEvaluationWork[] expected = work.Where(item => item.Observations[0].TargetId == target).ToArray();
        Assert.Equal(Lease, batch.Lease);
        Assert.Equal(Lease.Owner, batch.Lease.Owner);
        Assert.Equal(Lease.FencingToken, batch.Lease.FencingToken);
        Assert.Equal(expected.Min(item => item.DueAtUtc), batch.DueAtUtc);
        Assert.NotNull(batch.ClaimedWork);
        Assert.Equal(expected.Length, batch.ClaimedWork.Count);
        for (int index = 0; index < expected.Length; index++) Assert.Same(expected[index], batch.ClaimedWork[index]);
        Assert.All(batch.ClaimedWork, item =>
        {
            Assert.Equal(Lease.Key.Value, item.WorkKey);
            Assert.Equal(Lease.Owner, item.OwnerExecutionId);
            Assert.Equal(Lease.FencingToken, item.LeaseFencing);
        });
        Assert.Equal(expected.SelectMany(item => item.Observations).OrderBy(observation => observation.ObservedAtUtc)
            .ThenBy(observation => observation.RuleId).ThenBy(observation => observation.OperationId)
            .Select(observation => observation.OperationId), batch.Observations.Select(observation => observation.OperationId));
        Assert.All(batch.Observations, observation => Assert.Equal(target, observation.TargetId));
        Assert.All(batch.Decisions!, decision => Assert.Equal(target, decision.State.TargetId));
    }

    private static void AssertCancelledCycle(EvaluationRepository repository, OneCycleSource source, CapturingLeases leases)
    {
        Assert.True(repository.BlockedCallExited);
        Assert.True(repository.BlockedToken is { IsCancellationRequested: true });
        Assert.Equal(new[] { FailedTarget }, repository.RuleLookups.ToArray());
        Assert.Empty(repository.PersistAttempts);
        Assert.Empty(repository.Persisted);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(Lease, Assert.Single(source.Leases));
        Assert.Equal(1, leases.AcquireCount);
        AssertCleanRelease(leases);
        Assert.True(Assert.Single(leases.Releases).BlockedCallHadExited);
    }

    private static void AssertCleanRelease(CapturingLeases leases)
    {
        ReleaseObservation release = Assert.Single(leases.Releases);
        Assert.Equal(Lease, release.Identity);
        Assert.Equal(CancellationToken.None, release.Token);
        Assert.False(release.Token.IsCancellationRequested);
    }

    private sealed class OneCycleSource(IReadOnlyList<AlertEvaluationWork> work, Exception? failure = null) : IAlertEvaluationSource
    {
        private int _reads;
        public IReadOnlyList<AlertEvaluationWork> Work { get; } = work;
        public ConcurrentQueue<WorkerLeaseIdentity> Leases { get; } = new();
        public int ReadCount => Volatile.Read(ref _reads);
        public ValueTask<IReadOnlyList<AlertEvaluationWork>> ReadAsync(WorkerLeaseIdentity lease, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Leases.Enqueue(lease);
            int reads = Interlocked.Increment(ref _reads);
            return failure is null ? ValueTask.FromResult<IReadOnlyList<AlertEvaluationWork>>(reads == 1 ? Work : [])
                : ValueTask.FromException<IReadOnlyList<AlertEvaluationWork>>(failure);
        }
    }

    private sealed record ReleaseObservation(WorkerLeaseIdentity Identity, bool BlockedCallHadExited, CancellationToken Token);

    private sealed class CapturingLeases(Func<bool>? blockedCallExited = null, RenewalBehavior renewalBehavior = RenewalBehavior.Healthy) : IWorkerLeasePort
    {
        private int _acquires;
        public int AcquireCount => Volatile.Read(ref _acquires);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<WorkerLeaseIdentity> Renewals { get; } = new();
        public ConcurrentQueue<ReleaseObservation> Releases { get; } = new();
        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _acquires);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(new WorkerLease(Lease, now.AddSeconds(-1), now, now.AddSeconds(30)), now));
        }
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Renewals.Enqueue(request.Identity);
            if (renewalBehavior == RenewalBehavior.Fault) throw new InvalidOperationException("synthetic renewal failure");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(renewalBehavior == RenewalBehavior.OwnershipLost
                ? LeaseRenewalResult.OwnershipLost(now)
                : LeaseRenewalResult.Renewed(new WorkerLease(Lease, now.AddSeconds(-1), now, now.AddSeconds(30)), now));
        }
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            Releases.Enqueue(new ReleaseObservation(request.Identity, blockedCallExited?.Invoke() ?? true, cancellationToken));
            Released.TrySetResult();
            return ValueTask.FromResult(LeaseReleaseStatus.Released);
        }
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LeaseOwnershipStatus.Current);
    }

    private sealed record FaultObservation(FailurePoint Point, bool TokenWasCancelled);

    private sealed class EvaluationRepository(FailurePoint failurePoint = FailurePoint.None, bool simulateTimeout = false,
        bool blockFirstTarget = false, bool returnAfterCancellation = false) : IAlertRepositoryPort
    {
        private int _blockedCallExited;
        public bool BlockedCallExited => Volatile.Read(ref _blockedCallExited) == 1;
        public CancellationToken? BlockedToken { get; private set; }
        public TaskCompletionSource FirstTargetBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<MonitoredInstanceId> RuleLookups { get; } = new();
        public ConcurrentQueue<AlertEvaluationBatch> PersistAttempts { get; } = new();
        public ConcurrentQueue<AlertEvaluationBatch> Persisted { get; } = new();
        public ConcurrentQueue<FaultObservation> Faults { get; } = new();

        public async ValueTask<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(MonitoredInstanceId targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            RuleLookups.Enqueue(targetId);
            if (blockFirstTarget && targetId == FailedTarget)
            {
                BlockedToken = cancellationToken;
                FirstTargetBlocked.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (returnAfterCancellation && cancellationToken.IsCancellationRequested)
                {
                    // A non-cooperative adapter completing concurrently with
                    // cancellation must not allow a stale decision to persist.
                }
                finally { Volatile.Write(ref _blockedCallExited, 1); }
            }
            At(FailurePoint.Rules, targetId, cancellationToken);
            return [new AlertRuleDefinition(RuleId, "cpu.high", AlertRuleKind.MetricThreshold,
                new MetricId("cpu.percent"), AlertComparison.GreaterThanOrEqual, 80, 5, 2,
                TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15))];
        }
        public ValueTask<MaintenanceWindow?> GetMaintenanceAsync(MonitoredInstanceId targetId, DateTimeOffset atUtc, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            At(FailurePoint.Maintenance, targetId, cancellationToken);
            return ValueTask.FromResult<MaintenanceWindow?>(null);
        }
        public ValueTask<AlertRuleState?> GetStateAsync(MonitoredInstanceId targetId, Guid ruleId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            At(FailurePoint.State, targetId, cancellationToken);
            return ValueTask.FromResult<AlertRuleState?>(new AlertRuleState(ruleId, targetId));
        }
        public ValueTask<AlertEvaluationOutcome> EvaluateAndPersistAsync(AlertEvaluationBatch request, CancellationToken cancellationToken)
        {
            PersistAttempts.Enqueue(request);
            At(FailurePoint.Persist, request.Observations[0].TargetId, cancellationToken);
            Persisted.Enqueue(request);
            return ValueTask.FromResult(new AlertEvaluationOutcome(request.Observations.Count, request.Observations.Count, 0, Observed));
        }
        private void At(FailurePoint point, MonitoredInstanceId target, CancellationToken cancellationToken)
        {
            if (!returnAfterCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (target != FailedTarget || point != failurePoint) return;
            Faults.Enqueue(new FaultObservation(point, cancellationToken.IsCancellationRequested));
            if (simulateTimeout) throw new OperationCanceledException("synthetic target timeout", new CancellationToken(canceled: true));
            throw new InvalidOperationException("synthetic target repository failure");
        }

        public ValueTask<IReadOnlyList<AlertEvaluationWork>> ClaimDueEvaluationsAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(MonitoredInstanceId targetId, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(MaintenanceWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AlertAcknowledgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAdminAsync(AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AlertDeliveryResult> CompleteDeliveryAsync(AlertDeliveryResult result, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
