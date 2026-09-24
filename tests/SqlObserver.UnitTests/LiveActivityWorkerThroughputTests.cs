using Microsoft.Extensions.Logging.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;

namespace SqlObserver.UnitTests;

public sealed class LiveActivityWorkerThroughputTests
{
    [Fact]
    public async Task CompletedCapturesReleaseSlotsAndDispatchNextDueBatch()
    {
        var repository=new Repository(25);
        var collector=new Collector();
        var leases=new LeasePort();
        using var worker=new LiveActivityWorker(collector,repository,leases,NullLogger<LiveActivityWorker>.Instance);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await repository.Completed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(watch.Elapsed<TimeSpan.FromSeconds(10));
        }
        finally { await worker.StopAsync(CancellationToken.None); }

        Assert.Equal(25,repository.Commits);
        Assert.Equal(25,leases.Releases);
        Assert.InRange(collector.MaximumConcurrent,2,10);
    }

    private sealed class Collector : ILiveActivityCollector
    {
        private int active;
        private int maximum;
        public int MaximumConcurrent=>Volatile.Read(ref maximum);
        public async Task<LiveActivityCapture> CollectAsync(LiveActivityTarget target,CancellationToken cancellationToken)
        {
            int concurrent=Interlocked.Increment(ref active);
            InterlockedMax(ref maximum,concurrent);
            try
            {
                await Task.Delay(20,cancellationToken);
                return new LiveActivityCapture(Guid.NewGuid(),DateTimeOffset.UtcNow,false,[],[]);
            }
            finally { Interlocked.Decrement(ref active); }
        }

        private static void InterlockedMax(ref int destination,int value)
        {
            int observed;
            do
            {
                observed=Volatile.Read(ref destination);
                if(observed>=value) return;
            } while(Interlocked.CompareExchange(ref destination,value,observed)!=observed);
        }
    }

    private sealed class Repository : ILiveActivityRepository
    {
        private readonly LiveActivityTarget[] targets;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,byte> claimed=new();
        private readonly TaskCompletionSource<bool> completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int commits;

        public Repository(int count)
        {
            var policy=new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql01"),tcpPort:1433),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)));
            targets=Enumerable.Range(0,count).Select(_=>new LiveActivityTarget(Guid.NewGuid(),1,policy)).ToArray();
        }

        public Task Completed=>completed.Task;
        public int Commits=>Volatile.Read(ref commits);
        public Task<IReadOnlyList<LiveActivityTarget>> TargetsAsync(CancellationToken cancellationToken)=>
            Task.FromResult<IReadOnlyList<LiveActivityTarget>>(targets.Where(t=>!claimed.ContainsKey(t.Id)).Take(10).ToArray());

        public Task<LeaseAcquisitionResult> ClaimAsync(LiveActivityTarget target,WorkerExecutionId owner,
            WorkerLeaseDuration duration,CancellationToken cancellationToken)
        {
            if(!claimed.TryAdd(target.Id,0)) return Task.FromResult(LeaseAcquisitionResult.Contended(DateTimeOffset.UtcNow));
            var now=DateTimeOffset.UtcNow;
            var identity=new WorkerLeaseIdentity(new WorkerLeaseKey("collector/live-activity/"+target.Id.ToString("D")),owner,new FencingToken(1));
            return Task.FromResult(LeaseAcquisitionResult.Acquired(new WorkerLease(identity,now,now,now+duration.Value),now));
        }

        public Task CommitAsync(LiveActivityTarget target,WorkerLeaseIdentity lease,LiveActivityCapture capture,CancellationToken cancellationToken)
        {
            if(Interlocked.Increment(ref commits)==targets.Length) completed.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task<bool> HasDeadlockSnapshotAsync(Guid targetId,Guid eventId,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task FailedAsync(Guid targetId,WorkerLeaseIdentity lease,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task CleanupAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
        public Task<LiveActivityPage> ReadAsync(LiveActivityRead request,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(Guid targetId,DateTimeOffset fromUtc,DateTimeOffset toUtc,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<ProtectedSensitivePayload?> QueryAsync(Guid targetId,Guid snapshotId,string identity,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task AuditAsync(Guid targetId,Guid snapshotId,string actor,string outcome,CancellationToken cancellationToken)=>throw new NotSupportedException();
    }

    private sealed class LeasePort : IWorkerLeasePort
    {
        private int releases;
        public int Releases=>Volatile.Read(ref releases);
        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request,CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref releases);
            return ValueTask.FromResult(LeaseReleaseStatus.Released);
        }
    }
}
