using Microsoft.Extensions.Logging.Abstractions;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;

namespace SqlObserver.SecurityTests;

public sealed class M10AnalyticsBackfillWorkerTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(30)]
    public async Task CompletedRangeRecordsSuccessWithValidProgressAndReplay(int hours)
    {
        DateTimeOffset from = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
        var job = new AnalyticsBackfillJob(Guid.NewGuid(), new MonitoredInstanceId(Guid.NewGuid()), new ObservationTargetRevision(2), from, from.AddHours(hours), null);
        var store = new FakeStore(job);
        var repository = new FakeAnalyticsRepository();
        using var worker = new AnalyticsBackfillWorker(store, new FakeLeases(), new WorkerExecutionId(Guid.NewGuid()), repository, NullLogger<AnalyticsBackfillWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AnalyticsBackfillCompletion.Succeeded, store.Completion);
            Assert.Equal((int)Math.Ceiling(hours / 24d), repository.RollupsStored);
            Assert.Equal(repository.RollupsStored + 1, store.ReplayCount);
            Assert.NotNull(store.CompletedJob);
            store.CompletedJob!.Validate();
            Assert.Null(store.CompletedJob.Cursor);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class FakeStore(AnalyticsBackfillJob original) : IAnalyticsBackfillStore
    {
        private int claimed;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AnalyticsBackfillCompletion? Completion { get; private set; }
        public AnalyticsBackfillJob? CompletedJob { get; private set; }
        public int ReplayCount { get; private set; }
        public ValueTask<IReadOnlyList<AnalyticsBackfillJob>> ClaimAsync(WorkerLeaseIdentity lease, int maximumJobs, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AnalyticsBackfillJob>>(Interlocked.Exchange(ref claimed, 1) == 0 ? [original] : []);
        public ValueTask<AnalyticsBackfillPage> ReadPageAsync(AnalyticsBackfillJob job, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, int maximumRows, int maximumBytes, CancellationToken cancellationToken)
        {
            job.Validate();
            return ValueTask.FromResult(new AnalyticsBackfillPage([new MetricPoint(dayStartUtc.AddHours(1), "host.cpu.percent", 20)], null, false));
        }
        public ValueTask SaveCursorAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, DateTimeOffset dayStartUtc, string? cursor, CancellationToken cancellationToken)
        { job.Validate(); return ValueTask.CompletedTask; }
        public ValueTask CompleteAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, AnalyticsBackfillCompletion completion, string? failureDetail, CancellationToken cancellationToken)
        { job.Validate(); CompletedJob = job; Completion = completion; Completed.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask RecordReplayAsync(Guid operationId, AnalyticsBackfillJob job, WorkerLeaseIdentity lease, ReadOnlyMemory<byte> requestDigest, ReadOnlyMemory<byte> resultDigest, string resultJson, CancellationToken cancellationToken)
        { job.Validate(); ReplayCount++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeAnalyticsRepository : IAnalyticsRepositoryPort
    {
        public int RollupsStored { get; private set; }
        public bool BaselineStored { get; private set; }
        public bool ForecastStored { get; private set; }
        public bool EvidenceStored { get; private set; }
        public bool IncidentStored { get; private set; }
        public bool GenerationStored { get; private set; }
        public ValueTask<IReadOnlyList<MetricPoint>> ReadMetricPointsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<RollupResult>> ReadRollupsAsync(AnalyticsQueryRequest request, RollupInterval interval, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AnalyticsRollupPage> ReadRollupPageAsync(AnalyticsQueryRequest request, RollupInterval interval, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<BaselineResult>> ReadBaselinesAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ForecastResult>> ReadForecastsAsync(AnalyticsQueryRequest request, TimeSpan horizon, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<IncidentThread>> ReadIncidentsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask StoreRollupsAsync(AnalyticsJobRequest request, IReadOnlyList<RollupResult> rollups, CancellationToken cancellationToken) { RollupsStored += rollups.Count; return ValueTask.CompletedTask; }
        public ValueTask StoreBaselineAsync(AnalyticsJobRequest request, IReadOnlyList<BaselineResult> baselines, CancellationToken cancellationToken) { BaselineStored = true; return ValueTask.CompletedTask; }
        public ValueTask StoreForecastAsync(AnalyticsJobRequest request, ForecastResult forecast, CancellationToken cancellationToken) { ForecastStored = true; return ValueTask.CompletedTask; }
        public ValueTask StoreEvidenceAsync(AnalyticsJobRequest request, EvidencePacket packet, CancellationToken cancellationToken) { EvidenceStored = true; return ValueTask.CompletedTask; }
        public ValueTask StoreIncidentAsync(AnalyticsJobRequest request, IncidentThread thread, CancellationToken cancellationToken) { IncidentStored = true; return ValueTask.CompletedTask; }
        public ValueTask StoreIncidentGenerationAsync(AnalyticsJobRequest request, IncidentGeneration generation, CancellationToken cancellationToken) { GenerationStored = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeLeases : IWorkerLeasePort
    {
        public WorkerLeaseIdentity Identity { get; } = new(new WorkerLeaseKey("analytics/backfill"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        public bool ReleaseCalled { get; private set; }
        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(LeaseAcquisitionResult.Acquired(new WorkerLease(Identity, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), DateTimeOffset.UtcNow));
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(LeaseRenewalResult.Renewed(new WorkerLease(Identity, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), DateTimeOffset.UtcNow));
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(LeaseOwnershipStatus.Current);
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken cancellationToken) { ReleaseCalled = true; return ValueTask.FromResult(LeaseReleaseStatus.Released); }
    }
}
