using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.UnitTests;

public sealed class M4SchedulerTests
{
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(1));

    [Fact]
    public async Task SchedulerAcquiresTargetCollectorLeaseValidatesAndCommitsAtomically()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            (request, _) => ValueTask.FromResult(M4TestData.CreateSuccessResult(manifest, request)));
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        var leases = new FakeLeasePort();
        CollectorScheduler scheduler = CreateScheduler(registration, repository, leases);

        CollectorSchedulerCycleResult result = await scheduler.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.CommittedCount);
        BeginCollectorRunRequest start = Assert.Single(repository.Starts);
        CommitCollectorRunRequest commit = Assert.Single(repository.Commits);
        Assert.Equal("collector/run/engine.core/8e1b51c7f8124321a41ed46a21ca7641", leases.LastAcquire?.Key.Value);
        Assert.Equal(start.RunId, commit.Summary.RunId);
        Assert.Equal(commit.Work.TargetId, commit.Summary.TargetId);
        Assert.Equal(commit.Payload.ItemCount, commit.Summary.Accounting.OutputItemsProduced);
        Assert.Equal(1, leases.AssertCalls);
        Assert.Equal(1, leases.ReleaseCalls);
    }

    [Fact]
    public async Task RejectedDurableStartPreventsTargetIoAndCommit()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        int targetCalls = 0;
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            (request, _) =>
            {
                targetCalls++;
                return ValueTask.FromResult(M4TestData.CreateSuccessResult(manifest, request));
            });
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest))
        {
            StartStatus = CollectorRunStartStatus.ScheduleConflict,
        };
        var leases = new FakeLeasePort();

        CollectorSchedulerCycleResult result = await CreateScheduler(
            registration,
            repository,
            leases).RunCycleAsync(CancellationToken.None);

        Assert.Equal(CollectorWorkDisposition.CommitRejected, Assert.Single(result.Dispositions));
        Assert.Single(repository.Starts);
        Assert.Equal(0, targetCalls);
        Assert.Empty(repository.Commits);
        Assert.Equal(1, leases.ReleaseCalls);
    }

    [Fact]
    public async Task SchedulerPreventsLocalOverlapBeforeASecondLeaseCall()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            async (request, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return M4TestData.CreateSuccessResult(manifest, request);
            });
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        var leases = new FakeLeasePort();
        CollectorScheduler scheduler = CreateScheduler(registration, repository, leases);
        Task<CollectorSchedulerCycleResult> first = scheduler.RunCycleAsync(CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        CollectorSchedulerCycleResult second = await scheduler.RunCycleAsync(CancellationToken.None);
        release.TrySetResult();
        CollectorSchedulerCycleResult completed = await first;

        Assert.Equal(CollectorWorkDisposition.LocallyOverlapping, Assert.Single(second.Dispositions));
        Assert.Equal(CollectorWorkDisposition.Committed, Assert.Single(completed.Dispositions));
        Assert.Equal(1, leases.AcquireCalls);
        Assert.Single(repository.Commits);
    }

    [Fact]
    public async Task RenewalOwnershipLossCancelsTargetAndPreventsCommit()
    {
        CollectorManifest manifest = M4TestData.CreateManifest(commandTimeout: TimeSpan.FromSeconds(5));
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            });
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        var leases = new FakeLeasePort
        {
            LoseOnRenewal = true,
            AcquireNearExpiry = true,
        };
        CollectorScheduler scheduler = CreateScheduler(registration, repository, leases);

        CollectorSchedulerCycleResult result = await scheduler.RunCycleAsync(CancellationToken.None);

        Assert.Equal(CollectorWorkDisposition.LeaseLost, Assert.Single(result.Dispositions));
        Assert.True(leases.RenewCalls >= 1);
        Assert.Equal(0, leases.AssertCalls);
        Assert.Empty(repository.Commits);
    }

    [Fact]
    public async Task LeaseRenewalContinuesUntilAtomicCommitCompletes()
    {
        CollectorManifest manifest = M4TestData.CreateManifest(commandTimeout: TimeSpan.FromSeconds(5));
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            (request, _) => ValueTask.FromResult(M4TestData.CreateSuccessResult(manifest, request)));
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest))
        {
            BlockCommit = true,
        };
        var leases = new FakeLeasePort { AcquireNearExpiry = true };
        Task<CollectorSchedulerCycleResult> cycle = CreateScheduler(
            registration,
            repository,
            leases).RunCycleAsync(CancellationToken.None).AsTask();

        await repository.CommitEntered.WaitAsync(TimeSpan.FromSeconds(2));
        await leases.RenewalObserved.WaitAsync(TimeSpan.FromSeconds(2));
        repository.AllowCommit();
        CollectorSchedulerCycleResult result = await cycle;

        Assert.Equal(CollectorWorkDisposition.Committed, Assert.Single(result.Dispositions));
        Assert.True(leases.RenewCalls >= 1);
        Assert.Single(repository.Commits);
    }

    [Fact]
    public async Task LeaseContentionLeavesDueWorkVisibleWithoutTargetExecution()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        int targetCalls = 0;
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            (request, _) =>
            {
                targetCalls++;
                return ValueTask.FromResult(M4TestData.CreateSuccessResult(manifest, request));
            });
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        var leases = new FakeLeasePort { Contended = true };

        CollectorSchedulerCycleResult result = await CreateScheduler(
            registration,
            repository,
            leases).RunCycleAsync(CancellationToken.None);

        Assert.Equal(CollectorWorkDisposition.LeaseContended, Assert.Single(result.Dispositions));
        Assert.Equal(0, targetCalls);
        Assert.Empty(repository.Commits);
        Assert.Equal(0, leases.ReleaseCalls);
    }

    private static CollectorScheduler CreateScheduler(
        CollectorRegistration registration,
        ICollectorRuntimeRepositoryPort repository,
        IWorkerLeasePort leases) =>
        new(
            new CollectorRegistry([registration]),
            repository,
            leases,
            new CollectorExecutionEngine(),
            new WorkerExecutionId(Guid.Parse("82cacb79-e644-4804-a392-8050b91e99a7")),
            new CollectorSchedulerOptions(
                maxItemsPerCycle: 1,
                maxConcurrency: 1,
                new WorkerLeaseDuration(TimeSpan.FromSeconds(5)),
                RepositoryTimeout));

    private sealed class FakeRuntimeRepository : ICollectorRuntimeRepositoryPort
    {
        private readonly CollectorDueWorkItem _work;

        internal FakeRuntimeRepository(CollectorDueWorkItem work) => _work = work;

        internal List<CommitCollectorRunRequest> Commits { get; } = [];
        internal List<BeginCollectorRunRequest> Starts { get; } = [];
        internal CollectorRunStartStatus StartStatus { get; init; } = CollectorRunStartStatus.Started;
        internal bool BlockCommit { get; init; }
        internal Task CommitEntered => _commitEntered.Task;

        private readonly TaskCompletionSource _commitEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _commitRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void AllowCommit() => _commitRelease.TrySetResult();

        public ValueTask<CollectorCatalogReconcileResult> ReconcileCatalogAsync(
            ReconcileCollectorCatalogRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CollectorCatalogReconcileResult(
                request.Entries.Count,
                updatedCount: 0,
                unchangedCount: 0,
                M4TestData.RepositoryTime));

        public ValueTask<CollectorDueWorkBatch> ListDueAsync(
            ListDueCollectorWorkRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CollectorDueWorkBatch([_work], hasMore: false));
        }

        public ValueTask<CollectorRunStartResult> BeginRunAsync(
            BeginCollectorRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Starts)
            {
                Starts.Add(request);
            }

            bool accepted = StartStatus is CollectorRunStartStatus.Started or
                CollectorRunStartStatus.RunningReplay or
                CollectorRunStartStatus.CommittedReplay;
            return ValueTask.FromResult(new CollectorRunStartResult(
                StartStatus,
                accepted ? M4TestData.RepositoryTime : null,
                M4TestData.RepositoryTime));
        }

        public async ValueTask<CollectorRunCommitResult> CommitRunAsync(
            CommitCollectorRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commitEntered.TrySetResult();
            if (BlockCommit)
            {
                await _commitRelease.Task.WaitAsync(cancellationToken);
            }

            lock (Commits)
            {
                Commits.Add(request);
            }

            return new CollectorRunCommitResult(
                CollectorRunCommitStatus.Committed,
                insertedCount: request.Payload.ItemCount,
                duplicateCount: 0,
                rejectedCount: 0,
                persistedBytes: request.Payload.EstimatedSizeBytes,
                M4TestData.RepositoryTime);
        }
    }

    private sealed class FakeLeasePort : IWorkerLeasePort
    {
        internal bool Contended { get; init; }
        internal bool LoseOnRenewal { get; init; }
        internal bool AcquireNearExpiry { get; init; }
        internal int AcquireCalls { get; private set; }
        internal int RenewCalls { get; private set; }
        internal int AssertCalls { get; private set; }
        internal int ReleaseCalls { get; private set; }
        internal AcquireWorkerLeaseRequest? LastAcquire { get; private set; }
        internal Task RenewalObserved => _renewalObserved.Task;

        private readonly TaskCompletionSource _renewalObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<LeaseAcquisitionResult> AcquireAsync(
            AcquireWorkerLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCalls++;
            LastAcquire = request;
            if (Contended)
            {
                return ValueTask.FromResult(LeaseAcquisitionResult.Contended(M4TestData.RepositoryTime));
            }

            var identity = new WorkerLeaseIdentity(request.Key, request.Owner, new FencingToken(1));
            var lease = new WorkerLease(
                identity,
                M4TestData.RepositoryTime,
                M4TestData.RepositoryTime,
                M4TestData.RepositoryTime.AddSeconds(5));
            DateTimeOffset repositoryTime = AcquireNearExpiry
                ? M4TestData.RepositoryTime.AddMilliseconds(4_900)
                : M4TestData.RepositoryTime;
            return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(lease, repositoryTime));
        }

        public ValueTask<LeaseRenewalResult> RenewAsync(
            RenewWorkerLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenewCalls++;
            _renewalObserved.TrySetResult();
            if (LoseOnRenewal)
            {
                return ValueTask.FromResult(LeaseRenewalResult.OwnershipLost(
                    M4TestData.RepositoryTime.AddMilliseconds(4_950)));
            }

            var lease = new WorkerLease(
                request.Identity,
                M4TestData.RepositoryTime,
                M4TestData.RepositoryTime.AddSeconds(1),
                M4TestData.RepositoryTime.AddSeconds(6));
            return ValueTask.FromResult(LeaseRenewalResult.Renewed(
                lease,
                M4TestData.RepositoryTime.AddSeconds(1)));
        }

        public ValueTask<LeaseReleaseStatus> ReleaseAsync(
            ReleaseWorkerLeaseRequest request,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return ValueTask.FromResult(LeaseReleaseStatus.Released);
        }

        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(
            AssertWorkerLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertCalls++;
            return ValueTask.FromResult(LeaseOwnershipStatus.Current);
        }
    }
}
