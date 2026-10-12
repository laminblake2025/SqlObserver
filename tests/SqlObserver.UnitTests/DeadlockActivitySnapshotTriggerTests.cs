using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.UnitTests;

public sealed class DeadlockActivitySnapshotTriggerTests
{
    [Fact]
    public async Task CapturesRecentDeadlocksOnceAndLeavesCadenceLeaseIndependent()
    {
        CollectorManifest manifest = M4TestData.CreateManifest("deadlocks.system-health");
        CollectorDueWorkItem work = M4TestData.CreateWork(manifest);
        DeadlockObservation recent = CreateObservation("a", work.RepositoryTimeUtc.AddSeconds(-30));
        DeadlockObservation alreadyCaptured = CreateObservation("b", work.RepositoryTimeUtc.AddSeconds(-20));
        DeadlockObservation old = CreateObservation("c", work.RepositoryTimeUtc.AddMinutes(-6));
        var repository = new FakeRepository(alreadyCaptured.EventId);
        var leases = new FakeLeasePort();
        var collector = new FakeCollector();
        var trigger = new DeadlockActivitySnapshotTrigger(
            collector,
            repository,
            leases,
            new WorkerExecutionId(Guid.Parse("82cacb79-e644-4804-a392-8050b91e99a7")));

        await trigger.TriggerAsync(
            work,
            new DeadlockObservationBatch([recent, alreadyCaptured, old]),
            CancellationToken.None);
        await trigger.TriggerAsync(
            work,
            new DeadlockObservationBatch([recent, alreadyCaptured, old]),
            CancellationToken.None);

        LiveActivityCapture capture = Assert.Single(repository.Captures);
        Assert.Equal(recent.EventId, capture.DeadlockEventId);
        Assert.Equal(recent.OccurredAtUtc, capture.DeadlockOccurredUtc);
        Assert.Equal("collector/live-activity-deadlock/" + work.TargetId.Value.ToString("D"), leases.LastAcquire!.Value);
        Assert.Equal(1, leases.AcquireCalls);
        Assert.Equal(1, leases.ReleaseCalls);
        Assert.Equal(4, repository.HasCalls);
        Assert.Equal(1, collector.Calls);
    }

    private static DeadlockObservation CreateObservation(string token, DateTimeOffset occurredAtUtc) =>
        new(
            M4TestData.TargetId,
            M4TestData.TargetRevision,
            new DeadlockFingerprint(token.PadRight(64, token[0])),
            occurredAtUtc,
            [],
            []);

    private sealed class FakeCollector : ILiveActivityCollector
    {
        internal int Calls { get; private set; }

        public Task<LiveActivityCapture> CollectAsync(LiveActivityTarget target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new LiveActivityCapture(
                Guid.NewGuid(),
                M4TestData.RepositoryTime,
                false,
                [],
                []));
        }
    }

    private sealed class FakeRepository(Guid existingEvent) : ILiveActivityRepository
    {
        private readonly HashSet<Guid> _existing = [existingEvent];
        internal List<LiveActivityCapture> Captures { get; } = [];
        internal int HasCalls { get; private set; }

        public Task<bool> HasDeadlockSnapshotAsync(Guid targetId, Guid eventId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HasCalls++;
            return Task.FromResult(_existing.Contains(eventId));
        }

        public Task CommitAsync(LiveActivityTarget target, WorkerLeaseIdentity lease, LiveActivityCapture capture, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Captures.Add(capture);
            _existing.Add(capture.DeadlockEventId!.Value);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LiveActivityTarget>> TargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LeaseAcquisitionResult> ClaimAsync(LiveActivityTarget target,WorkerExecutionId owner,WorkerLeaseDuration duration,CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailedAsync(Guid targetId, WorkerLeaseIdentity lease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CleanupAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LiveActivityPage> ReadAsync(LiveActivityRead request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(Guid targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProtectedSensitivePayload?> QueryAsync(Guid targetId, Guid snapshotId, string identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AuditAsync(Guid targetId, Guid snapshotId, string actor, string outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeLeasePort : IWorkerLeasePort
    {
        private readonly WorkerExecutionId _owner = new(Guid.Parse("82cacb79-e644-4804-a392-8050b91e99a7"));
        internal int AcquireCalls { get; private set; }
        internal int ReleaseCalls { get; private set; }
        internal WorkerLeaseKey? LastAcquire { get; private set; }

        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCalls++;
            LastAcquire = request.Key;
            var identity = new WorkerLeaseIdentity(request.Key, _owner, new FencingToken(1));
            var lease = new WorkerLease(identity, M4TestData.RepositoryTime, M4TestData.RepositoryTime, M4TestData.RepositoryTime.AddMinutes(2));
            return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(lease, M4TestData.RepositoryTime));
        }

        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return ValueTask.FromResult(LeaseReleaseStatus.Released);
        }
    }
}
