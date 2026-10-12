using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class RetentionMaintenanceWorkerTests
{
    private static readonly string[] ExpectedCalls = ["partition", "graph", "copy", "text", "plan", "partition", "graph", "copy", "text", "plan"];
    private static readonly long[] ExpectedFences = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    [Theory]
    [InlineData(0, 1)] // continuous partition work and held graph/copy work
    [InlineData(1, 60)] // returned failure
    [InlineData(2, 60)] // thrown failure
    [InlineData(3, 900)] // complete idle sweep
    [InlineData(4, 1)] // oversize graph cannot starve payload pruning
    [InlineData(5, 60)] // text pruning failure cannot starve plan pruning
    public async Task EveryFamilyRunsUnderItsOwnLeaseBeforeSweepBackoff(int scenario, int backoffSeconds)
    {
        var ports = new Ports(scenario);
        var clock = new ManualClock();
        using var worker = CreateWorker(ports, clock);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            for (int iteration = 0; iteration < 10; iteration++)
            {
                var timer = await clock.NextTimerAsync();
                Assert.Equal(iteration + 1, ports.Releases);
                Assert.Null(ports.ActiveLease);
                Assert.Equal(TimeSpan.FromSeconds(iteration % 5 == 4 ? backoffSeconds : 1), timer.DueTime);
                if (iteration < 9) timer.Fire();
            }
            Assert.Equal(ExpectedCalls, ports.Calls.Select(call => call.Name));
            Assert.Equal(ExpectedFences, ports.Calls.Select(call => call.Fence));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ContendedLeaseDoesNotConsumeTheNextFamily()
    {
        var ports = new Ports(3) { ContendFirst = true };
        var clock = new ManualClock();
        using var worker = CreateWorker(ports, clock);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var timer = await clock.NextTimerAsync();
            Assert.Empty(ports.Calls);
            Assert.Equal(0, ports.Releases);
            timer.Fire();
            await clock.NextTimerAsync();
            Assert.Equal("partition", Assert.Single(ports.Calls).Name);
            Assert.Equal(1, ports.Releases);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellationDuringWorkReleasesTheLeaseWithoutRunningAnotherFamily()
    {
        var ports = new Ports(3) { BlockPartition = true };
        var clock = new ManualClock();
        using var worker = CreateWorker(ports, clock);
        await worker.StartAsync(CancellationToken.None);
        try { await ports.PartitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Equal("partition", Assert.Single(ports.Calls).Name);
        Assert.Equal(1, ports.Releases);
        Assert.Null(ports.ActiveLease);
    }

    [Fact]
    public async Task ProductionUsesTheSameMaintenancePortForPartitionsAndRetention()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Same(provider.GetRequiredService<PostgreSqlPartitionMaintenancePort>(), provider.GetRequiredService<IRetentionMaintenancePort>());
    }

    private static RetentionMaintenanceWorker CreateWorker(Ports ports, TimeProvider clock) =>
        new(ports, ports, new(Guid.NewGuid()), clock, NullLogger<RetentionMaintenanceWorker>.Instance);

    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> timers = Channel.CreateUnbounded<ManualTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(callback, state, dueTime);
            Assert.True(timers.Writer.TryWrite(timer));
            return timer;
        }
        public Task<ManualTimer> NextTimerAsync() => timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int disposed;
        public TimeSpan DueTime { get; } = dueTime;
        public void Fire() { if (Volatile.Read(ref disposed) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Ports(int scenario) : IRetentionMaintenancePort, IWorkerLeasePort
    {
        private long fence;
        private bool contended;
        public bool ContendFirst { get; init; }
        public bool BlockPartition { get; init; }
        public TaskCompletionSource PartitionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Name, long Fence)> Calls { get; } = [];
        public WorkerLeaseIdentity? ActiveLease { get; private set; }
        public int Releases { get; private set; }

        private void Record(string name, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout)
        {
            Assert.Equal(ActiveLease, lease);
            Assert.Equal("retention/maintenance", lease.Key.Value);
            Assert.Equal(TimeSpan.FromSeconds(name is "text" or "plan" ? 5 : 15), timeout.Value);
            Calls.Add((name, lease.FencingToken.Value));
        }
        public async ValueTask<SystemRetentionStepOutcome> RunSystemRetentionStepAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Record("partition", lease, timeout);
            PartitionEntered.TrySetResult();
            if (BlockPartition) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (scenario == 2) throw new IOException("Synthetic retention failure");
            return scenario == 0 ? SystemRetentionStepOutcome.Detached : scenario == 1 ? SystemRetentionStepOutcome.Failed : SystemRetentionStepOutcome.Idle;
        }
        public ValueTask<QueryRunRetentionStepOutcome> RunQueryRunRetentionStepAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Record("graph", lease, timeout);
            return ValueTask.FromResult(scenario == 0 ? QueryRunRetentionStepOutcome.Held : scenario == 4 ? QueryRunRetentionStepOutcome.Oversize : QueryRunRetentionStepOutcome.Idle);
        }
        public ValueTask<OriginalCopyRetentionStepOutcome> RunOriginalCopyRetentionStepAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Record("copy", lease, timeout);
            return ValueTask.FromResult(scenario is 0 or 4 ? OriginalCopyRetentionStepOutcome.Blocked : OriginalCopyRetentionStepOutcome.Idle);
        }
        public ValueTask<int> PruneOrphanQueryTextPayloadsAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Record("text", lease, timeout);
            if (scenario == 5) throw new IOException("Synthetic text pruning failure");
            return ValueTask.FromResult(scenario is 0 or 4 ? 2 : 0);
        }
        public ValueTask<int> PruneOrphanQueryPlanPayloadsAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        { Record("plan", lease, timeout); return ValueTask.FromResult(scenario is 0 or 4 ? 1 : 0); }

        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            Assert.Null(ActiveLease);
            Assert.Equal(TimeSpan.FromSeconds(30), request.Duration.Value);
            var now = DateTimeOffset.UtcNow;
            if (ContendFirst && !contended) { contended = true; return ValueTask.FromResult(LeaseAcquisitionResult.Contended(now)); }
            var lease = new WorkerLease(new(request.Key, request.Owner, new(++fence)), now, now, now + request.Duration.Value);
            ActiveLease = lease.Identity;
            return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(lease, now));
        }
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Assert.Equal(ActiveLease, request.Identity);
            ActiveLease = null; Releases++;
            return ValueTask.FromResult(LeaseReleaseStatus.Released);
        }
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
