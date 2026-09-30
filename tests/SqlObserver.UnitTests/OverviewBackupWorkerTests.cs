using Microsoft.Extensions.Logging.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.UnitTests;

public sealed class OverviewBackupWorkerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)] // core failure cannot prevent backup materialization
    [InlineData(2)] // backup read failure cannot stop the next target
    [InlineData(3)] // publication failure cannot stop the next target
    [InlineData(4)] // lost fence stops remaining work
    [InlineData(5)] // rejected stale context is retried by a later cycle
    public async Task WorkerIsolatesSourcesAndTargetsAndStopsAfterOwnershipLoss(int failure)
    {
        var ports = new Ports(failure);
        using var worker = new OverviewLatestStatusWorker(ports, ports, ports, ports, new(Guid.NewGuid()), NullLogger<OverviewLatestStatusWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try { await ports.Completed.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Equal(1, ports.Releases);
        Assert.Equal(failure == 4 ? 1 : 2, ports.CoreTargets.Count);
        Assert.Equal(ports.CoreTargets, ports.BackupTargets);
        Assert.Equal(failure == 2 ? 1 : failure == 4 ? 1 : 2, ports.PublishedTargets.Count);
        if (failure != 4) Assert.Contains(ports.Targets[1].TargetId.Value, ports.PublishedTargets);
    }

    private sealed class Ports(int failure) : IOverviewLatestMaterializationPort, IBackupFleetSummaryPort, IBackupFleetCachePort, IWorkerLeasePort
    {
        public OverviewRefreshTarget[] Targets { get; } = [new(new(Guid.NewGuid()), 1), new(new(Guid.NewGuid()), 1)];
        public List<Guid> CoreTargets { get; } = [];
        public List<Guid> BackupTargets { get; } = [];
        public List<Guid> PublishedTargets { get; } = [];
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completed => completed.Task;
        public int Releases { get; private set; }

        public ValueTask<IReadOnlyList<OverviewRefreshTarget>> ListDueAsync(WorkerLeaseIdentity lease, int limit, CancellationToken token)
        { Assert.Equal("overview/latest-status", lease.Key.Value); return ValueTask.FromResult<IReadOnlyList<OverviewRefreshTarget>>(Targets); }
        public ValueTask<bool> RefreshCoreAsync(OverviewRefreshTarget target, WorkerLeaseIdentity lease, CancellationToken token)
        {
            CoreTargets.Add(target.TargetId.Value);
            if (failure == 1 && target == Targets[0]) throw new IOException("Synthetic core failure");
            return ValueTask.FromResult(true);
        }
        public ValueTask<BackupFleetSummary> ReadAsync(OverviewRefreshTarget target, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken token)
        {
            BackupTargets.Add(target.TargetId.Value);
            if (failure == 2 && target == Targets[0]) throw new IOException("Synthetic backup failure");
            var at = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
            var context = new BackupEvidenceHeader(target.TargetId, at, target.TargetRevision, null, null, null, null, null, null,
                BackupFreshnessCoverage.NotCollected, BackupEvidenceStatus.NotCollected, 0, 0, 0, 0, false);
            var counts = new BackupFleetCheckCounts(0, 0, 0, 0, 0, []);
            return ValueTask.FromResult(new BackupFleetSummary(context, at.AddMinutes(2), BackupFleetStatus.Indeterminate, counts, counts, 0));
        }
        public ValueTask<bool> PublishAsync(BackupFleetSummary summary, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken token)
        {
            Guid target = summary.Context.TargetId.Value; PublishedTargets.Add(target);
            if (failure == 3 && target == Targets[0].TargetId.Value) throw new IOException("Synthetic publication failure");
            return ValueTask.FromResult(failure != 5 || target != Targets[0].TargetId.Value);
        }
        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken token)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var lease = new WorkerLease(new(request.Key, request.Owner, new(1)), now, now, now + request.Duration.Value);
            return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(lease, now));
        }
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken token)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(failure == 4 ? LeaseRenewalResult.OwnershipLost(now)
                : LeaseRenewalResult.Renewed(new(request.Identity, now, now, now + request.Duration.Value), now));
        }
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken token)
        { Releases++; completed.TrySetResult(); return ValueTask.FromResult(LeaseReleaseStatus.Released); }
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
