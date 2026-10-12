using Microsoft.Extensions.Logging.Abstractions;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;

namespace SqlObserver.SecurityTests;

public sealed class M10AnalyticsDerivationWorkerTests
{
    [Fact]
    public void PostgreSqlAnalyticsWritesCarryJobIdSeparatelyFromDerivedReplayOperation()
    {
        string source = FindRepositorySource();
        Assert.Contains("@operation_id,@job_id,@instance_id", source, StringComparison.Ordinal);
        Assert.Contains("AddWithValue(\"job_id\", request.JobId)", source, StringComparison.Ordinal);
        var jobId = Guid.NewGuid();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("analytics/derivation"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        var request = new AnalyticsJobRequest(jobId, target, lease.Key.Value, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), new ObservationTargetRevision(1));
        AnalyticsReplayEnvelope replay = AnalyticsReplayContract.Create("forecast", request, "{\"metricKey\":\"host.volume.free_bytes\"}");
        Assert.NotEqual(jobId, replay.OperationId);
    }

    private static string FindRepositorySource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "SqlObserver.Infrastructure.PostgreSql", "PostgreSqlAnalyticsRepositoryPort.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("PostgreSQL analytics adapter source was not found.");
    }

    [Fact]
    public async Task WorkerClaimsAndPersistsBaselineThroughTheFencedRepositoryPort()
    {
        (FakeStore store, FakeAnalyticsRepository repository) = await RunAsync("baseline", "host.cpu.percent");
        Assert.Equal(AnalyticsDerivationCompletion.Succeeded, store.Completion);
        Assert.True(repository.BaselineStored);
        Assert.True(store.ClaimCount > 0);
        Assert.Equal(["schedule", "claim"], store.ControlCalls.Take(2));
    }

    [Fact]
    public async Task WorkerSchedulesDueWorkBeforeClaimingIt()
    {
        (FakeStore store, _) = await RunAsync("evidence", "host.cpu.percent");
        Assert.True(store.ScheduleCount > 0);
        Assert.Equal(["schedule", "claim"], store.ControlCalls.Take(2));
        Assert.NotNull(store.ScheduledLease);
        Assert.Equal("analytics/derivation", store.ScheduledLease!.Key.Value);
    }

    [Fact]
    public async Task WorkerDoesNotClaimWhenSchedulingFails()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var job = new AnalyticsDerivationJob(Guid.NewGuid(), target, new ObservationTargetRevision(1), "evidence", to.AddDays(-1), to, to, MetricKey: null);
        var store = new FakeStore(job) { ScheduleFailure = true };
        var leases = new FakeLeases();
        using var worker = new AnalyticsDerivationWorker(store, leases, new WorkerExecutionId(Guid.NewGuid()), new FakeAnalyticsRepository(), NullLogger<AnalyticsDerivationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await store.ScheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, store.ClaimCount);
        Assert.Equal(["schedule"], store.ControlCalls.Take(1));
        Assert.True(leases.ReleaseCalled);
    }

    [Fact]
    public async Task WorkerDoesNotClaimWhenScheduleCountExceedsBound()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var job = new AnalyticsDerivationJob(Guid.NewGuid(), target, new ObservationTargetRevision(1), "evidence", to.AddDays(-1), to, to, MetricKey: null);
        var store = new FakeStore(job) { ScheduleResult = AnalyticsJobBounds.MaximumRows + 1 };
        var leases = new FakeLeases();
        using var worker = new AnalyticsDerivationWorker(store, leases, new WorkerExecutionId(Guid.NewGuid()), new FakeAnalyticsRepository(), NullLogger<AnalyticsDerivationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await store.ScheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, store.ClaimCount);
        Assert.True(leases.ReleaseCalled);
    }

    [Fact]
    public async Task WorkerCancellationCancelsInFlightScheduleAndReleasesExactLease()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset to = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var job = new AnalyticsDerivationJob(Guid.NewGuid(), target, new ObservationTargetRevision(1), "evidence", to.AddDays(-1), to, to, MetricKey: null);
        var store = new FakeStore(job) { BlockSchedule = true };
        var leases = new FakeLeases();
        using var worker = new AnalyticsDerivationWorker(store, leases, new WorkerExecutionId(Guid.NewGuid()), new FakeAnalyticsRepository(), NullLogger<AnalyticsDerivationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await store.ScheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, store.ClaimCount);
        Assert.True(store.ScheduleCancellationObserved);
        Assert.True(leases.ReleaseCalled);
        Assert.Same(leases.Identity, store.ScheduledLease);
    }

    [Fact]
    public async Task WorkerPersistsAvailableForecastWithExplicitPositiveCapacity()
    {
        (FakeStore store, FakeAnalyticsRepository repository) = await RunAsync("forecast", "host.volume.free_bytes");
        Assert.Equal(AnalyticsDerivationCompletion.Succeeded, store.Completion);
        Assert.True(repository.ForecastStored);
        Assert.True(store.ClaimCount > 0);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    public async Task WorkerUsesThirtyDayHorizonForLegacyJobsAndPreservesExplicitHorizons(int? requestedDays, int expectedDays)
    {
        (FakeStore store, FakeAnalyticsRepository repository) = await RunAsync(
            "forecast", "host.volume.free_bytes", forecastHorizonDays: requestedDays);

        Assert.Equal(AnalyticsDerivationCompletion.Succeeded, store.Completion);
        ForecastResult forecast = Assert.IsType<ForecastResult>(repository.Forecast);
        Assert.True(forecast.Available);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero), forecast.HorizonStartUtc);
        Assert.Equal(TimeSpan.FromDays(expectedDays), forecast.HorizonEndUtc - forecast.HorizonStartUtc);
    }

    [Fact]
    public async Task WorkerPersistsEvidenceAndIncidentGenerations()
    {
        (FakeStore evidenceStore, FakeAnalyticsRepository evidenceRepository) = await RunAsync("evidence", "host.cpu.percent");
        Assert.Equal(AnalyticsDerivationCompletion.Succeeded, evidenceStore.Completion);
        Assert.True(evidenceRepository.EvidenceStored);

        (FakeStore incidentStore, FakeAnalyticsRepository incidentRepository) = await RunAsync("correlation", "host.cpu.percent");
        Assert.Equal(AnalyticsDerivationCompletion.Succeeded, incidentStore.Completion);
        Assert.True(incidentRepository.IncidentStored);
        Assert.True(incidentRepository.GenerationStored);
        Assert.True(evidenceStore.ClaimCount > 0);
        Assert.True(incidentStore.ClaimCount > 0);
    }

    [Fact]
    public async Task WorkerCompletesTimedOutInputAsPartialWithinBoundedCompletionAttempt()
    {
        (FakeStore store, _) = await RunAsync("baseline", "host.cpu.percent", delayInput: true, jobDuration: TimeSpan.FromMilliseconds(100));
        Assert.Equal(AnalyticsDerivationCompletion.Partial, store.Completion);
        Assert.True(store.CompletionTokenObserved);
    }

    private static async Task<(FakeStore Store, FakeAnalyticsRepository Repository)> RunAsync(string kind, string metric, bool delayInput = false, TimeSpan? jobDuration = null, int? forecastHorizonDays = 1)
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        DateTimeOffset from = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), to = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var job = new AnalyticsDerivationJob(Guid.NewGuid(), target, new ObservationTargetRevision(1), kind, from, to, to.AddHours(1), MetricKey: metric, ForecastHorizon: forecastHorizonDays is { } days ? TimeSpan.FromDays(days) : null);
        var store = new FakeStore(job) { DelayInput = delayInput };
        var repository = new FakeAnalyticsRepository();
        using var worker = new AnalyticsDerivationWorker(store, new FakeLeases(), new WorkerExecutionId(Guid.NewGuid()), repository, NullLogger<AnalyticsDerivationWorker>.Instance, jobDuration);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await worker.StartAsync(cancellation.Token);
        await store.Completed.Task.WaitAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);
        return (store, repository);
    }

    private sealed class FakeStore(AnalyticsDerivationJob job) : IAnalyticsDerivationStore
    {
        private int claimed;
        private readonly List<string> controlCalls = [];
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ScheduleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AnalyticsDerivationCompletion? Completion { get; private set; }
        public int ClaimCount { get; private set; }
        public int ScheduleCount { get; private set; }
        public IReadOnlyList<string> ControlCalls => controlCalls;
        public WorkerLeaseIdentity? ScheduledLease { get; private set; }
        public bool ScheduleFailure { get; init; }
        public int ScheduleResult { get; init; } = 1;
        public bool BlockSchedule { get; init; }
        public bool ScheduleCancellationObserved { get; private set; }
        public bool DelayInput { get; init; }
        public bool CompletionTokenObserved { get; private set; }
        public ValueTask<int> ScheduleAsync(WorkerLeaseIdentity lease, CancellationToken cancellationToken)
        {
            controlCalls.Add("schedule"); ScheduleCount++; ScheduledLease = lease; ScheduleStarted.TrySetResult();
            if (ScheduleFailure) return ValueTask.FromException<int>(new InvalidOperationException("schedule failed"));
            return BlockSchedule ? WaitForScheduleCancellationAsync(cancellationToken) : ValueTask.FromResult(ScheduleResult);
        }
        private async ValueTask<int> WaitForScheduleCancellationAsync(CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            catch (OperationCanceledException) { ScheduleCancellationObserved = true; throw; }
        }
        public ValueTask<IReadOnlyList<AnalyticsDerivationJob>> ClaimAsync(WorkerLeaseIdentity lease, int maximumJobs, CancellationToken cancellationToken)
        { controlCalls.Add("claim"); ClaimCount++; return ValueTask.FromResult<IReadOnlyList<AnalyticsDerivationJob>>(Interlocked.Exchange(ref claimed, 1) == 0 ? [job] : []); }
        public ValueTask<IReadOnlyList<RollupResult>> ReadBaselineInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken) =>
            ReadBaselineAsync(job, cancellationToken);
        private async ValueTask<IReadOnlyList<RollupResult>> ReadBaselineAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
        { if (DelayInput) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return [new RollupResult { BucketStartUtc = job.SourceCutoffUtc.AddHours(-1), BucketEndUtc = job.SourceCutoffUtc, MetricKey = "host.cpu.percent", DimensionsSha256 = CanonicalDimensions.Sha256(null), Count = 1, Expected = 1, Mean = 10 }]; }
        public async ValueTask<AnalyticsForecastInput> ReadForecastInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
        { DateTimeOffset start = job.FromUtc; var points = Enumerable.Range(0, 30).Select(i => new MetricPoint(start.AddDays(i), job.MetricKey!, i * 100, dimensions: new Dictionary<string, string> { ["volume"] = "C:" })); return new AnalyticsForecastInput(points.ToArray(), 5000); }
        public ValueTask<IReadOnlyList<AnalyticsEvidenceInput>> ReadEvidenceInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AnalyticsEvidenceInput>>([new AnalyticsEvidenceInput(job.SourceCutoffUtc.AddMinutes(-1), [new EvidenceReference("metric", "cpu", job.SourceCutoffUtc.AddMinutes(-1))], [])]);
        public ValueTask<IReadOnlyList<EvidencePacket>> ReadIncidentInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<EvidencePacket>>([EvidenceV1.Build(Guid.NewGuid(), job.SourceCutoffUtc.AddMinutes(-1), [new EvidenceReference("metric", "cpu", job.SourceCutoffUtc.AddMinutes(-1))], sourceCutoffUtc: job.SourceCutoffUtc)]);
        public ValueTask CompleteAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, AnalyticsDerivationCompletion completion, string? failureDetail, CancellationToken cancellationToken) { Completion = completion; CompletionTokenObserved = cancellationToken.CanBeCanceled; Completed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeAnalyticsRepository : IAnalyticsRepositoryPort
    {
        public bool BaselineStored { get; private set; }
        public bool ForecastStored { get; private set; }
        public ForecastResult? Forecast { get; private set; }
        public bool EvidenceStored { get; private set; }
        public bool IncidentStored { get; private set; }
        public bool GenerationStored { get; private set; }
        public ValueTask<IReadOnlyList<MetricPoint>> ReadMetricPointsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<RollupResult>> ReadRollupsAsync(AnalyticsQueryRequest request, RollupInterval interval, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AnalyticsRollupPage> ReadRollupPageAsync(AnalyticsQueryRequest request, RollupInterval interval, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<BaselineResult>> ReadBaselinesAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ForecastResult>> ReadForecastsAsync(AnalyticsQueryRequest request, TimeSpan horizon, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<IncidentThread>> ReadIncidentsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask StoreRollupsAsync(AnalyticsJobRequest request, IReadOnlyList<RollupResult> rollups, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask StoreBaselineAsync(AnalyticsJobRequest request, IReadOnlyList<BaselineResult> baselines, CancellationToken cancellationToken) { BaselineStored = true; return ValueTask.CompletedTask; }
        public ValueTask StoreForecastAsync(AnalyticsJobRequest request, ForecastResult forecast, CancellationToken cancellationToken) { ForecastStored = true; Forecast = forecast; return ValueTask.CompletedTask; }
        public ValueTask StoreEvidenceAsync(AnalyticsJobRequest request, EvidencePacket packet, CancellationToken cancellationToken) { EvidenceStored = true; return ValueTask.CompletedTask; }
        public ValueTask StoreIncidentAsync(AnalyticsJobRequest request, IncidentThread thread, CancellationToken cancellationToken) { IncidentStored = true; return ValueTask.CompletedTask; }
        public ValueTask StoreIncidentGenerationAsync(AnalyticsJobRequest request, IncidentGeneration generation, CancellationToken cancellationToken) { GenerationStored = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeLeases : IWorkerLeasePort
    {
        public WorkerLeaseIdentity Identity { get; } = new(new WorkerLeaseKey("analytics/derivation"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        public bool ReleaseCalled { get; private set; }
        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(LeaseAcquisitionResult.Acquired(new WorkerLease(Identity, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), DateTimeOffset.UtcNow));
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(LeaseRenewalResult.Renewed(new WorkerLease(Identity, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), DateTimeOffset.UtcNow));
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(LeaseOwnershipStatus.Current);
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken cancellationToken) { ReleaseCalled = true; return ValueTask.FromResult(LeaseReleaseStatus.Released); }
    }
}
