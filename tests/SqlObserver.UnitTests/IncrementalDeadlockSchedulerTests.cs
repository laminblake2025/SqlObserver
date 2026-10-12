using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed partial class M4SchedulerTests
{
    [Fact]
    public async Task IncrementalDeadlockSchedulerReadsAfterBeginAndCommitsTheSameProof()
    {
        CollectorManifest manifest = IncrementalManifest();
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        var leases = new FakeLeasePort();
        DeadlockReadSourceProof? emitted = null;
        CollectorRegistration registration = IncrementalRegistration(manifest, (request, _) =>
        {
            Assert.Equal(1, repository.DeadlockReads);
            Assert.Equal(Assert.Single(repository.Starts).RunId, request.DeadlockReadContext!.RunId);
            emitted = SuccessProof(request);
            return ValueTask.FromResult(IncrementalResult(request, emitted));
        });
        Assert.Equal(1, (await CreateScheduler(registration, repository, leases).RunCycleAsync(default)).CommittedCount);
        Assert.Same(emitted, Assert.Single(repository.DeadlockProofs));
        Assert.Equal(1, Assert.Single(repository.Commits).Summary.AttemptCount);
        Assert.Equal(1, leases.AssertCalls);
        Assert.Equal(1, leases.ReleaseCalls);
    }

    [Fact]
    public async Task FailedCheckpointReadPreventsTargetIoAndAnyCommit()
    {
        CollectorManifest manifest = IncrementalManifest();
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest)) { FailCheckpointRead = true };
        var leases = new FakeLeasePort();
        int targetCalls = 0;
        CollectorRegistration registration = IncrementalRegistration(manifest, (request, _) =>
        { targetCalls++; return ValueTask.FromResult(IncrementalResult(request, SuccessProof(request))); });
        Assert.Equal(CollectorWorkDisposition.RepositoryFailure, Assert.Single((await CreateScheduler(registration, repository, leases).RunCycleAsync(default)).Dispositions));
        Assert.Equal(0, targetCalls);
        Assert.Empty(repository.DeadlockProofs);
        Assert.Empty(repository.Commits);
        Assert.Equal(1, leases.ReleaseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrForeignSuccessProofBecomesUnavailableWithoutAdvancing(bool foreign)
    {
        CollectorManifest manifest = IncrementalManifest();
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        CollectorRegistration registration = IncrementalRegistration(manifest, (request, _) =>
        {
            DeadlockReadSourceProof? proof = foreign ? new(request.TargetId, request.TargetRevision, 999, request.RunId,
                null, DeadlockReadSourceState.Ready, new(request.TargetId, request.TargetRevision, new string('a', 64)),
                M4TestData.RepositoryTime, M4TestData.RepositoryTime.AddMinutes(-10), [], true, false) : null;
            return ValueTask.FromResult(IncrementalResult(request, proof));
        });
        Assert.Equal(1, (await CreateScheduler(registration, repository, new FakeLeasePort()).RunCycleAsync(default)).CommittedCount);
        CommitCollectorRunRequest commit = Assert.Single(repository.Commits);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, commit.Summary.Outcome);
        Assert.Empty(commit.Payload.Deadlocks.Items);
        DeadlockReadSourceProof saved = Assert.Single(repository.DeadlockProofs);
        Assert.Equal(DeadlockReadSourceState.Failed, saved.State);
        Assert.True(saved.SourceLoss);
        Assert.Empty(saved.Positions);
        Assert.False(saved.ReachedEnd);
    }

    [Fact]
    public async Task RetryUsesTheSameCheckpointAndOnlyPublishesTheFinalAttempt()
    {
        CollectorManifest manifest = IncrementalManifest();
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest));
        DeadlockReadExecutionContext? first = null;
        CollectorRegistration registration = IncrementalRegistration(manifest, (request, _) =>
        {
            if (request.Attempt.Value == 1)
            {
                first = request.DeadlockReadContext;
                return ValueTask.FromResult(new CollectorExecutionResult(request.TargetId, request.TargetRevision, manifest.Id, 2, 2,
                    CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure, CollectorPayload.Empty,
                    new(0, 0, 0, 0), CollectorLossEvidence.None, first!.UnavailableProof()));
            }
            Assert.Same(first, request.DeadlockReadContext);
            return ValueTask.FromResult(IncrementalResult(request, SuccessProof(request)));
        });
        Assert.Equal(1, (await CreateScheduler(registration, repository, new FakeLeasePort()).RunCycleAsync(default)).CommittedCount);
        Assert.Equal(1, repository.DeadlockReads);
        Assert.Equal(2, Assert.Single(repository.Commits).Summary.AttemptCount);
        Assert.Equal(DeadlockReadSourceState.Ready, Assert.Single(repository.DeadlockProofs).State);
    }

    [Fact]
    public async Task IneligibleIncrementalReadStillRecordsUnavailableEvidenceWithoutTargetIo()
    {
        CollectorManifest manifest = IncrementalManifest();
        var repository = new FakeRuntimeRepository(M4TestData.CreateWork(manifest, profile: M4TestData.CreateProfile(PermissionEvidenceOutcome.Denied)));
        int targetCalls = 0;
        CollectorRegistration registration = IncrementalRegistration(manifest, (request, _) =>
        { targetCalls++; return ValueTask.FromResult(IncrementalResult(request, SuccessProof(request))); });
        Assert.Equal(1, (await CreateScheduler(registration, repository, new FakeLeasePort()).RunCycleAsync(default)).CommittedCount);
        Assert.Equal(0, targetCalls);
        Assert.Equal(CollectorRunOutcome.PermissionDenied, Assert.Single(repository.Commits).Summary.Outcome);
        Assert.Equal(DeadlockReadSourceState.Failed, Assert.Single(repository.DeadlockProofs).State);
    }

    private static CollectorManifest IncrementalManifest() => M4TestData.CreateManifest("deadlocks.system-health", outputKind: CollectorOutputKind.Deadlocks, version: 2);
    private static CollectorRegistration IncrementalRegistration(CollectorManifest manifest, Func<CollectorExecutionRequest, CancellationToken, ValueTask<CollectorExecutionResult>> collect)
        => M4TestData.CreateRegistration(manifest, collect, order: 8, outputContract: new(new(2), [], 0, 0, 0, maxDeadlockObservations: 256));
    private static DeadlockReadSourceProof SuccessProof(CollectorExecutionRequest request) => new(request.TargetId, request.TargetRevision,
        request.DeadlockReadContext!.ScheduleRevision, request.RunId, request.DeadlockReadContext.ExpectedCheckpointDigest,
        DeadlockReadSourceState.Ready, new(request.TargetId, request.TargetRevision, new string('a', 64)),
        M4TestData.RepositoryTime, M4TestData.RepositoryTime.AddMinutes(-10), [], true, false);
    private static CollectorExecutionResult IncrementalResult(CollectorExecutionRequest request, DeadlockReadSourceProof? proof)
        => new(request.TargetId, request.TargetRevision, new("deadlocks.system-health"), 2, 2, CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed, CollectorPayload.Empty, new(0, 0, 128, 0), CollectorLossEvidence.None, proof);
}
