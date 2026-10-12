using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.UnitTests;

public sealed partial class M4SchedulerTests
{
    [Fact]
    public async Task IncrementalReadYieldsOtherDispatchSlotsAndPublishesOnlyOneBoundedPage()
    {
        CollectorManifest manifest = IncrementalManifest();
        CollectorManifest fastManifest = M4TestData.CreateManifest("engine.fast");
        CollectorManifest nextManifest = M4TestData.CreateManifest("engine.next");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sourceCalls = 0;
        CollectorRegistration incremental = IncrementalRegistration(manifest, async (request, token) =>
        {
            Interlocked.Increment(ref sourceCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            var proof = new DeadlockReadSourceProof(request.TargetId, request.TargetRevision,
                request.DeadlockReadContext!.ScheduleRevision, request.RunId, request.DeadlockReadContext.ExpectedCheckpointDigest,
                DeadlockReadSourceState.Ready, new(request.TargetId, request.TargetRevision, new string('a', 64)),
                M4TestData.RepositoryTime, M4TestData.RepositoryTime.AddMinutes(-10),
                [new(1000, 0, 100), new(1000, 0, 200)], false, false);
            return new(request.TargetId, request.TargetRevision, manifest.Id, 2, 2,
                CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, CollectorPayload.Empty,
                new(2, 0, 256, 0), CollectorLossEvidence.None, proof);
        });
        CollectorRegistration fast = M4TestData.CreateRegistration(fastManifest,
            (request, _) => ValueTask.FromResult(M4TestData.CreateSuccessResult(fastManifest, request)), order: 2);
        CollectorRegistration next = M4TestData.CreateRegistration(nextManifest, (request, _) =>
        {
            nextEntered.TrySetResult();
            return ValueTask.FromResult(M4TestData.CreateSuccessResult(nextManifest, request));
        }, order: 3);
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        var owner = new WorkerExecutionId(Guid.NewGuid());
        foreach (CollectorManifest item in new[] { manifest, fastManifest, nextManifest })
        {
            CollectorDueWorkItem work = M4TestData.CreateWork(item);
            var identity = new WorkerLeaseIdentity(new("collector/run/" + item.Id.Value + "/" + work.TargetId.Value.ToString("N")), owner, new(1));
            repository.Claims.Enqueue(new(work, new(identity, M4TestData.RepositoryTime, M4TestData.RepositoryTime,
                M4TestData.RepositoryTime.AddSeconds(5)), M4TestData.RepositoryTime));
        }
        var leases = new FakeLeasePort();
        var scheduler = new CollectorScheduler(new([incremental, fast, next]), repository, leases, new(), owner,
            new(1, 2, new(TimeSpan.FromSeconds(5)), RepositoryTimeout));
        try
        {
            Assert.Equal(2, await scheduler.DispatchAvailableAsync(default));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForCommitCountAsync(repository, 1);
            using var refill = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (await scheduler.DispatchAvailableAsync(refill.Token) == 0) await Task.Delay(10, refill.Token);
            await nextEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForCommitCountAsync(repository, 2);
            Assert.Empty(repository.DeadlockProofs);
            Assert.Equal(1, sourceCalls);
        }
        finally
        {
            release.TrySetResult();
            await scheduler.DrainAsync();
        }
        DeadlockReadSourceProof saved = Assert.Single(repository.DeadlockProofs);
        Assert.False(saved.ReachedEnd);
        Assert.False(saved.SourceLoss);
        Assert.Equal(2, saved.Positions.Count);
        Assert.Equal(1, repository.DeadlockReads);
        Assert.Equal(1, sourceCalls);
        Assert.Equal(3, repository.Commits.Count);
        Assert.Equal(3, leases.ReleaseCalls);
        Assert.Equal(0, leases.AcquireCalls);
    }
}
