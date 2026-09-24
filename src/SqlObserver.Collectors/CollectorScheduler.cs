using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collectors;

public sealed class CollectorSchedulerOptions
{
    public CollectorSchedulerOptions(
        int maxItemsPerCycle,
        int maxConcurrency,
        WorkerLeaseDuration leaseDuration,
        RepositoryCallTimeout repositoryTimeout)
    {
        if (maxItemsPerCycle is <= 0 or > ListDueCollectorWorkRequest.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItemsPerCycle));
        }

        if (maxConcurrency is <= 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        ArgumentNullException.ThrowIfNull(leaseDuration);
        ArgumentNullException.ThrowIfNull(repositoryTimeout);
        MaxItemsPerCycle = maxItemsPerCycle;
        MaxConcurrency = maxConcurrency;
        LeaseDuration = leaseDuration;
        RepositoryTimeout = repositoryTimeout;
    }

    public int MaxItemsPerCycle { get; }
    public int MaxConcurrency { get; }
    public WorkerLeaseDuration LeaseDuration { get; }
    public RepositoryCallTimeout RepositoryTimeout { get; }
}

public enum CollectorWorkDisposition
{
    Committed = 1,
    CommitRejected = 2,
    LeaseContended = 3,
    LeaseLost = 4,
    LocallyOverlapping = 5,
    CollectorNotRegistered = 6,
    RepositoryFailure = 7,
}

public sealed class CollectorSchedulerCycleResult
{
    public CollectorSchedulerCycleResult(
        int dueCount,
        bool hasMore,
        IReadOnlyList<CollectorWorkDisposition> dispositions)
    {
        if (dueCount is < 0 or > ListDueCollectorWorkRequest.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(dueCount));
        }

        ArgumentNullException.ThrowIfNull(dispositions);
        if (dispositions.Count != dueCount)
        {
            throw new ArgumentException("Every due work item must have one visible scheduler disposition.", nameof(dispositions));
        }

        DueCount = dueCount;
        HasMore = hasMore;
        Dispositions = Array.AsReadOnly(dispositions.ToArray());
    }

    public int DueCount { get; }
    public bool HasMore { get; }
    public IReadOnlyList<CollectorWorkDisposition> Dispositions { get; }
    public int CommittedCount => Dispositions.Count(static value => value == CollectorWorkDisposition.Committed);
}

/// <summary>Bounded cross-process scheduling with local non-overlap and renewable fenced leases.</summary>
public sealed class CollectorScheduler
{
    private const string MeterName = "SqlObserver.Collectors.Scheduler";
    private static readonly Meter Metrics = new(MeterName);
    private static readonly Counter<long> LeaseContentionCount = Metrics.CreateCounter<long>(
        "sqlobserver.collector.lease.contention");
    private static readonly Counter<long> LeaseLossCount = Metrics.CreateCounter<long>(
        "sqlobserver.collector.lease.loss");
    private static readonly Histogram<double> SchedulingLagMilliseconds = Metrics.CreateHistogram<double>(
        "sqlobserver.collector.schedule.lag",
        unit: "ms");

    private readonly CollectorRegistry _registry;
    private readonly ICollectorRuntimeRepositoryPort _repository;
    private readonly IWorkerLeasePort _leases;
    private readonly CollectorExecutionEngine _engine;
    private readonly WorkerExecutionId _workerExecutionId;
    private readonly CollectorSchedulerOptions _options;
    private readonly IDeadlockActivitySnapshotTrigger? _deadlockActivitySnapshotTrigger;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly object _dispatchGate = new();
    private readonly HashSet<Task<CollectorWorkDisposition>> _activeDispatches = [];

    public int ActiveDispatchCount
    {
        get { lock (_dispatchGate) return _activeDispatches.Count; }
    }

    public CollectorScheduler(
        CollectorRegistry registry,
        ICollectorRuntimeRepositoryPort repository,
        IWorkerLeasePort leases,
        CollectorExecutionEngine engine,
        WorkerExecutionId workerExecutionId,
        CollectorSchedulerOptions options,
        IDeadlockActivitySnapshotTrigger? deadlockActivitySnapshotTrigger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _workerExecutionId = workerExecutionId ?? throw new ArgumentNullException(nameof(workerExecutionId));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _deadlockActivitySnapshotTrigger = deadlockActivitySnapshotTrigger;
    }

    public ValueTask<CollectorCatalogReconcileResult> ReconcileCatalogAsync(
        WorkerLeaseIdentity catalogLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogLease);
        return _repository.ReconcileCatalogAsync(
            new ReconcileCollectorCatalogRequest(
                _registry.CatalogEntries,
                catalogLease,
                _options.RepositoryTimeout),
            cancellationToken);
    }

    public async ValueTask<CollectorSchedulerCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        CollectorDueWorkBatch batch = await _repository.ListDueAsync(
                new ListDueCollectorWorkRequest(_options.MaxItemsPerCycle, _options.RepositoryTimeout),
                cancellationToken)
            .ConfigureAwait(false);
        var dispositions = new CollectorWorkDisposition[batch.Items.Count];
        await Parallel.ForEachAsync(
                batch.Items.Select(static (work, index) => (Work: work, Index: index)),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = _options.MaxConcurrency,
                },
                async (indexedWork, token) =>
                {
                    dispositions[indexedWork.Index] = await ProcessWorkAsync(indexedWork.Work, token)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
        return new CollectorSchedulerCycleResult(batch.Items.Count, batch.HasMore, dispositions);
    }

    /// <summary>Fill free slots with repository-claimed work without awaiting slower active collectors.</summary>
    public async ValueTask<int> DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        int freeSlots;
        lock (_dispatchGate)
        {
            _activeDispatches.RemoveWhere(static task =>
            {
                if (!task.IsCompleted) return false;
                _ = task.Exception;
                return true;
            });
            freeSlots = _options.MaxConcurrency - _activeDispatches.Count;
        }

        int dispatched = 0;
        var request = new ClaimDueCollectorWorkRequest(
            _workerExecutionId, _options.LeaseDuration, _options.RepositoryTimeout);
        for (int index = 0; index < freeSlots; index++)
        {
            CollectorClaimedWork? claimed = await _repository.ClaimDueAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (claimed is null) break;

            Task<CollectorWorkDisposition> task = Task.Run(
                async () => await ProcessWorkAsync(claimed.Work, cancellationToken, claimed)
                    .ConfigureAwait(false), CancellationToken.None);
            lock (_dispatchGate) _activeDispatches.Add(task);
            dispatched++;
        }
        return dispatched;
    }

    public async Task DrainAsync()
    {
        Task<CollectorWorkDisposition>[] active;
        lock (_dispatchGate) active = [.. _activeDispatches];
        try { await Task.WhenAll(active).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async ValueTask<CollectorWorkDisposition> ProcessWorkAsync(
        CollectorDueWorkItem work,
        CancellationToken cancellationToken,
        CollectorClaimedWork? claimed = null)
    {
        if (!_registry.TryGet(work.CollectorId, out CollectorRegistration? registration) || registration is null)
        {
            if (claimed is not null) await ReleaseWithoutMaskingAsync(claimed.Lease.Identity).ConfigureAwait(false);
            return CollectorWorkDisposition.CollectorNotRegistered;
        }

        string localKey = string.Concat(work.CollectorId.Value, "/", work.TargetId.Value.ToString("N"));
        if (!_inFlight.TryAdd(localKey, 0))
        {
            if (claimed is not null) await ReleaseWithoutMaskingAsync(claimed.Lease.Identity).ConfigureAwait(false);
            return CollectorWorkDisposition.LocallyOverlapping;
        }

        WorkerLeaseIdentity? leaseIdentity = claimed?.Lease.Identity;
        try
        {
            SchedulingLagMilliseconds.Record(
                Math.Max(0, (work.RepositoryTimeUtc - work.ScheduledAtUtc).TotalMilliseconds),
                new KeyValuePair<string, object?>("collector.id", work.CollectorId.Value));
            LeaseAcquisitionResult acquisition = claimed is not null
                ? LeaseAcquisitionResult.Acquired(claimed.Lease, claimed.LeaseRepositoryTimeUtc)
                : await _leases.AcquireAsync(
                    new AcquireWorkerLeaseRequest(
                        new WorkerLeaseKey(string.Concat("collector/run/", localKey)),
                        _workerExecutionId,
                        _options.LeaseDuration,
                        _options.RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (acquisition.Status == LeaseAcquisitionStatus.Contended)
            {
                LeaseContentionCount.Add(
                    1,
                    new KeyValuePair<string, object?>("collector.id", work.CollectorId.Value));
                return CollectorWorkDisposition.LeaseContended;
            }

            WorkerLease lease = acquisition.Lease ?? throw new InvalidDataException(
                "An acquired collector lease returned no ownership evidence.");
            leaseIdentity = lease.Identity;
            var runId = new CollectorRunId(Guid.NewGuid());
            CollectorRunStartResult start = await _repository.BeginRunAsync(
                    new BeginCollectorRunRequest(
                        work,
                        runId,
                        leaseIdentity,
                        _options.RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (start.Status == CollectorRunStartStatus.CommittedReplay)
            {
                return CollectorWorkDisposition.Committed;
            }

            if (start.Status == CollectorRunStartStatus.LeaseLost)
            {
                LeaseLossCount.Add(
                    1,
                    new KeyValuePair<string, object?>("collector.id", work.CollectorId.Value));
                return CollectorWorkDisposition.LeaseLost;
            }

            if (start.Status is not (CollectorRunStartStatus.Started or CollectorRunStartStatus.RunningReplay))
            {
                return CollectorWorkDisposition.CommitRejected;
            }

            using var renewalStop = new CancellationTokenSource();
            using var ownershipLost = new CancellationTokenSource();
            var renewalState = new LeaseRenewalState(lease, acquisition.RepositoryTimeUtc);
            Task renewal = RenewLeaseAsync(
                renewalState,
                ownershipLost,
                renewalStop.Token);
            using var ownershipCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                ownershipLost.Token);
            try
            {
                CollectorEligibilityResult eligibility = CollectorEligibilityEvaluator.Evaluate(
                    registration.Manifest,
                    work);
                CollectorEngineResult engineResult = eligibility.IsEligible
                    ? await _engine.ExecuteAsync(
                            registration,
                            work,
                            runId,
                            ownershipCancellation.Token)
                        .ConfigureAwait(false)
                    : CollectorExecutionEngine.CreateIneligibleResult(registration, work, runId, eligibility);

                if (renewalState.OwnershipLost)
                {
                    RecordLeaseLoss(work);
                    return CollectorWorkDisposition.LeaseLost;
                }

                LeaseOwnershipStatus ownership = await _leases.AssertOwnershipAsync(
                        new AssertWorkerLeaseRequest(leaseIdentity, _options.RepositoryTimeout),
                        ownershipCancellation.Token)
                    .ConfigureAwait(false);
                if (ownership != LeaseOwnershipStatus.Current)
                {
                    RecordLeaseLoss(work);
                    return CollectorWorkDisposition.LeaseLost;
                }

                CollectorRunCommitResult committed = await _repository.CommitRunAsync(
                        new CommitCollectorRunRequest(
                            work,
                            engineResult.Summary,
                            engineResult.Payload,
                            engineResult.NextCircuit,
                            leaseIdentity,
                            _options.RepositoryTimeout),
                        ownershipCancellation.Token)
                    .ConfigureAwait(false);
                if (committed.Status == CollectorRunCommitStatus.LeaseLost)
                {
                    RecordLeaseLoss(work);
                    return CollectorWorkDisposition.LeaseLost;
                }

                if ((committed.Status is CollectorRunCommitStatus.Committed or CollectorRunCommitStatus.Replayed) &&
                    _deadlockActivitySnapshotTrigger is not null &&
                    string.Equals(work.CollectorId.Value, "deadlocks.system-health", StringComparison.Ordinal) &&
                    engineResult.Payload.Deadlocks.Items.Count > 0)
                {
                    await _deadlockActivitySnapshotTrigger.TriggerAsync(
                            work,
                            engineResult.Payload.Deadlocks,
                            ownershipCancellation.Token)
                        .ConfigureAwait(false);
                }

                return committed.Status is CollectorRunCommitStatus.Committed or CollectorRunCommitStatus.Replayed
                    ? CollectorWorkDisposition.Committed
                    : CollectorWorkDisposition.CommitRejected;
            }
            catch (OperationCanceledException) when (
                ownershipLost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                RecordLeaseLoss(work);
                return CollectorWorkDisposition.LeaseLost;
            }
            finally
            {
                renewalStop.Cancel();
                await AwaitRenewalStopAsync(renewal).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CollectorWorkDisposition.RepositoryFailure;
        }
        finally
        {
            if (leaseIdentity is not null)
            {
                await ReleaseWithoutMaskingAsync(leaseIdentity).ConfigureAwait(false);
            }

            _inFlight.TryRemove(localKey, out _);
        }
    }

    private async Task RenewLeaseAsync(
        LeaseRenewalState state,
        CancellationTokenSource ownershipLost,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan remainingAtRepository = state.Lease.ExpiresAtUtc - state.RepositoryTimeUtc;
                TimeSpan delay = TimeSpan.FromTicks(Math.Max(
                    TimeSpan.FromMilliseconds(100).Ticks,
                    remainingAtRepository.Ticks / 2));
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                LeaseRenewalResult result = await _leases.RenewAsync(
                        new RenewWorkerLeaseRequest(
                            state.Lease.Identity,
                            _options.LeaseDuration,
                            _options.RepositoryTimeout),
                        stoppingToken)
                    .ConfigureAwait(false);
                if (result.Status != LeaseRenewalStatus.Renewed || result.Lease is null)
                {
                    state.MarkOwnershipLost();
                    ownershipLost.Cancel();
                    return;
                }

                state.Update(result.Lease, result.RepositoryTimeUtc);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            state.MarkOwnershipLost();
            ownershipLost.Cancel();
        }
    }

    private async ValueTask ReleaseWithoutMaskingAsync(WorkerLeaseIdentity identity)
    {
        try
        {
            await _leases.ReleaseAsync(
                    new ReleaseWorkerLeaseRequest(identity, _options.RepositoryTimeout),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Expiry is the recovery path; release failure must not mask the bounded run outcome.
        }
    }

    private static void RecordLeaseLoss(CollectorDueWorkItem work) => LeaseLossCount.Add(
        1,
        new KeyValuePair<string, object?>("collector.id", work.CollectorId.Value));

    private static async Task AwaitRenewalStopAsync(Task renewal)
    {
        try
        {
            await renewal.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class LeaseRenewalState
    {
        internal LeaseRenewalState(WorkerLease lease, DateTimeOffset repositoryTimeUtc)
        {
            Lease = lease;
            RepositoryTimeUtc = repositoryTimeUtc;
        }

        internal WorkerLease Lease { get; private set; }
        internal DateTimeOffset RepositoryTimeUtc { get; private set; }
        internal bool OwnershipLost { get; private set; }

        internal void Update(WorkerLease lease, DateTimeOffset repositoryTimeUtc)
        {
            Lease = lease;
            RepositoryTimeUtc = repositoryTimeUtc;
        }

        internal void MarkOwnershipLost() => OwnershipLost = true;
    }
}
