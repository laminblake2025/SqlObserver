#pragma warning disable CA1848
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Observability;

namespace SqlObserver.Collector;

/// <summary>
/// Bounded, repository-only production derivation lane.  All source reads
/// and writes are fenced by the repository; this worker never opens a target
/// connection and never accepts SQL or untrusted job payloads.
/// </summary>
public sealed class AnalyticsDerivationWorker(
    IAnalyticsDerivationStore store,
    IWorkerLeasePort leases,
    WorkerExecutionId executionId,
    IAnalyticsRepositoryPort analytics,
    ILogger<AnalyticsDerivationWorker> logger,
    TimeSpan? maximumJobDuration = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));
    private const string LeaseKey = "analytics/derivation";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LeaseAcquisitionResult acquired = await leases.AcquireAsync(
                    new AcquireWorkerLeaseRequest(new WorkerLeaseKey(LeaseKey), executionId,
                        new WorkerLeaseDuration(LeaseDuration), RepositoryTimeout), stoppingToken).ConfigureAwait(false);
                if (acquired.Status == LeaseAcquisitionStatus.Acquired && acquired.Lease is not null)
                {
                    using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    Task renewal = RenewLeaseUntilCancelledAsync(acquired.Lease.Identity, cycleCancellation);
                    try
                    {
                        // Scheduling and claiming are separate fenced control
                        // operations. Scheduling must complete under this
                        // exact lease before any work can be claimed; this
                        // also keeps a scheduler failure from claiming stale
                        // or previously queued work.
                        int scheduled = await store.ScheduleAsync(
                            acquired.Lease.Identity, cycleCancellation.Token).ConfigureAwait(false);
                        if (scheduled is < 0 or > AnalyticsJobBounds.MaximumRows)
                            throw new InvalidDataException("Derivation schedule exceeded the bounded contract.");
                        IReadOnlyList<AnalyticsDerivationJob> jobs = await store.ClaimAsync(
                            acquired.Lease.Identity, AnalyticsJobBounds.MaximumConcurrency,
                            cycleCancellation.Token).ConfigureAwait(false);
                        if (jobs.Count > AnalyticsJobBounds.MaximumConcurrency)
                            throw new InvalidDataException("Derivation claim exceeded the concurrency bound.");
                        using var gate = new SemaphoreSlim(AnalyticsJobBounds.MaximumConcurrency,
                            AnalyticsJobBounds.MaximumConcurrency);
                        await Task.WhenAll(jobs.Select(job => ProcessBoundedAsync(job, acquired.Lease.Identity,
                            cycleCancellation, gate))).ConfigureAwait(false);
                    }
                    finally
                    {
                        cycleCancellation.Cancel();
                        try { await renewal.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                        await ReleaseAsync(acquired.Lease.Identity).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                RecordFailure("cycle", exception);
            }
            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessBoundedAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease,
        CancellationTokenSource cycleCancellation, SemaphoreSlim gate)
    {
        await gate.WaitAsync(cycleCancellation.Token).ConfigureAwait(false);
        using var budgetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cycleCancellation.Token);
        TimeSpan jobDuration = maximumJobDuration is { } configured && configured > TimeSpan.Zero && configured <= AnalyticsJobBounds.MaximumDuration
            ? configured : AnalyticsJobBounds.MaximumDuration;
        budgetCancellation.CancelAfter(jobDuration);
        try
        {
            job.Validate();
            if (job.RequestedAtUtc is { } requestedAt)
                RuntimeDiagnostics.RecordAnalyticsQueueAge(job.JobKind, DateTimeOffset.UtcNow - requestedAt);
            await AssertLeaseAsync(lease, budgetCancellation.Token).ConfigureAwait(false);
            switch (job.JobKind)
            {
                case "rollup":
                    await DeriveRollupsAsync(job, lease, budgetCancellation.Token).ConfigureAwait(false);
                    break;
                case "baseline":
                    await DeriveBaselineAsync(job, lease, budgetCancellation.Token).ConfigureAwait(false);
                    break;
                case "forecast":
                    await DeriveForecastAsync(job, lease, budgetCancellation.Token).ConfigureAwait(false);
                    break;
                case "evidence":
                    await DeriveEvidenceAsync(job, lease, budgetCancellation.Token).ConfigureAwait(false);
                    break;
                case "correlation":
                case "incident":
                    await DeriveIncidentsAsync(job, lease, budgetCancellation.Token).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException("Unknown analytics derivation kind.");
            }
            await CompleteAsync(job, lease, AnalyticsDerivationCompletion.Succeeded, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budgetCancellation.IsCancellationRequested)
        {
            // Timeout, lease loss, and host shutdown are retryable bounded
            // interruptions.  The repository owns attempt counting and may
            // terminalize the job after its configured maximum attempts.
            try { await CompleteAsync(job, lease, AnalyticsDerivationCompletion.Partial, "bounded_derivation_timeout").ConfigureAwait(false); }
            catch (Exception exception) { RecordFailure("completion", exception, job.JobId); }
            RecordFailure("job", cycleCancellation.IsCancellationRequested ? "cancelled" : "timeout", job.JobId);
        }
        catch (Exception exception)
        {
            try { await CompleteAsync(job, lease, AnalyticsDerivationCompletion.Partial, "bounded_derivation_failure").ConfigureAwait(false); }
            catch (Exception completionException) { RecordFailure("completion", completionException, job.JobId); }
            RecordFailure("job", exception, job.JobId);
        }
        finally { gate.Release(); }
    }

    private async Task DeriveRollupsAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, CancellationToken token)
    {
        RollupInterval interval = job.RollupInterval ?? throw new InvalidDataException("A rollup interval is required.");
        IReadOnlyList<MetricPoint> source = await store.ReadRollupInputsAsync(job, interval, token).ConfigureAwait(false);
        if (source.Count > AnalyticsJobBounds.MaximumRows) throw new InvalidDataException("Rollup input exceeded row bound.");
        IReadOnlyList<RollupResult> rollups = RollupV1.ComputeMany(source, interval, sourceCutoffUtc: job.SourceCutoffUtc);
        if (rollups.Count > AnalyticsJobBounds.MaximumRows) throw new InvalidDataException("Rollup result exceeded row bound.");
        EnsureBounded(rollups);
        await AssertLeaseAsync(lease, token).ConfigureAwait(false);
        // Request carries the actual claimed job identity. Replay operation
        // IDs are derived separately by the repository adapter.
        await analytics.StoreRollupsAsync(Request(job, lease), rollups, token).ConfigureAwait(false);
    }

    private async Task DeriveBaselineAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, CancellationToken token)
    {
        IReadOnlyList<RollupResult> input = await store.ReadBaselineInputsAsync(job, token).ConfigureAwait(false);
        IReadOnlyList<BaselineResult> result = BaselineV1.Compute(input, job.SourceCutoffUtc)
            .Select(row => row with { Generation = job.Generation }).ToArray();
        EnsureBounded(result);
        await AssertLeaseAsync(lease, token).ConfigureAwait(false);
        AnalyticsJobRequest request = Request(job, lease);
        await analytics.StoreBaselineAsync(request, result, token).ConfigureAwait(false);
    }

    private async Task DeriveForecastAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, CancellationToken token)
    {
        AnalyticsForecastInput input = await store.ReadForecastInputsAsync(job, token).ConfigureAwait(false);
        TimeSpan horizon = job.ForecastHorizon ?? TimeSpan.FromDays(1);
        ForecastResult result = input.DailyRollups.Count > 0
            ? ForecastV1.Compute(job.MetricKey!, input.DailyRollups, job.SourceCutoffUtc, horizon, input.Capacity, job.DimensionsSha256)
            // Compatibility for non-production adapters. PostgreSQL's
            // production adapter always populates DailyRollups above.
            : ForecastV1.Compute(job.MetricKey!, input.Points, job.SourceCutoffUtc, horizon, input.Capacity)
            with { SourceGeneration = job.Generation };
        // ForecastV1 uses NaN as an in-memory unavailable sentinel. JSON and
        // PostgreSQL persistence are finite-only, so normalize that sentinel
        // to an explicit unavailable row before the replay-fenced write.
        if (!result.Available)
            result = result with { Residual = 0, VisibilityState = "unavailable" };
        EnsureBounded(result);
        await AssertLeaseAsync(lease, token).ConfigureAwait(false);
        await analytics.StoreForecastAsync(Request(job, lease), result, token).ConfigureAwait(false);
    }

    private async Task DeriveEvidenceAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, CancellationToken token)
    {
        IReadOnlyList<AnalyticsEvidenceInput> inputs = await store.ReadEvidenceInputsAsync(job, token).ConfigureAwait(false);
        if (inputs.Count > AnalyticsJobBounds.MaximumRows) throw new InvalidDataException("Evidence input exceeded row bound.");
        foreach (AnalyticsEvidenceInput input in inputs)
        {
            input.Validate();
            EvidencePacket packet = EvidenceV1.Build(input.OccurredAtUtc, input.References, input.Tombstones, job.SourceCutoffUtc);
            packet = packet with { TargetId = job.TargetId.Value, TargetRevision = job.TargetRevision.Value, Generation = job.Generation };
            EnsureBounded(packet);
            await AssertLeaseAsync(lease, token).ConfigureAwait(false);
            await analytics.StoreEvidenceAsync(Request(job, lease), packet, token).ConfigureAwait(false);
        }
    }

    private async Task DeriveIncidentsAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, CancellationToken token)
    {
        IReadOnlyList<EvidencePacket> source = await store.ReadIncidentInputsAsync(job, token).ConfigureAwait(false);
        if (source.Count > AnalyticsJobBounds.MaximumRows) throw new InvalidDataException("Incident input exceeded row bound.");
        IReadOnlyList<IncidentThread> threads = IncidentV1.Sessionize(source);
        foreach (IncidentThread thread in threads)
        {
            await AssertLeaseAsync(lease, token).ConfigureAwait(false);
            // Generations are committed explicitly below.  This keeps the
            // thread and append-only generation writes independently replay
            // fenced while preserving the same deterministic output.
            IncidentThread threadOnly = thread with { Generations = Array.Empty<IncidentGeneration>() };
            EnsureBounded(threadOnly);
            AnalyticsJobRequest request = Request(job, lease);
            await analytics.StoreIncidentAsync(request, threadOnly, token).ConfigureAwait(false);
            foreach (IncidentGeneration generation in thread.Generations)
            {
                await AssertLeaseAsync(lease, token).ConfigureAwait(false);
                await analytics.StoreIncidentGenerationAsync(request, generation, token).ConfigureAwait(false);
            }
        }
    }

    private static AnalyticsJobRequest Request(AnalyticsDerivationJob job, WorkerLeaseIdentity lease) =>
        // The work key must remain the exact leased key: PostgreSQL's fixed
        // write functions assert ownership against that key.  The operation
        // function and payload already bind the derivation kind.
        new(job.JobId, job.TargetId, lease.Key.Value, lease, RepositoryTimeout, job.TargetRevision);

    private static void EnsureBounded<T>(T value)
    {
        string json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > AnalyticsJobBounds.MaximumBytes)
            throw new InvalidDataException("Analytics derivation result exceeded the byte bound.");
    }

    private async ValueTask CompleteAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease,
        AnalyticsDerivationCompletion completion, string? detail)
    {
        // Completion is still attempted after a job token is cancelled, but
        // never inherits an unbounded CancellationToken.None call.
        using var completionCancellation = new CancellationTokenSource(CompletionTimeout);
        await store.CompleteAsync(job, lease, completion, detail, completionCancellation.Token).ConfigureAwait(false);
    }

    private async Task RenewLeaseUntilCancelledAsync(WorkerLeaseIdentity identity, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(LeaseRenewInterval, cancellation.Token).ConfigureAwait(false);
                LeaseRenewalResult renewed = await leases.RenewAsync(new RenewWorkerLeaseRequest(identity,
                    new WorkerLeaseDuration(LeaseDuration), RepositoryTimeout), cancellation.Token).ConfigureAwait(false);
                if (renewed.Status == LeaseRenewalStatus.OwnershipLost) { cancellation.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { RecordFailure("lease_renewal", exception); cancellation.Cancel(); }
    }

    private async ValueTask AssertLeaseAsync(WorkerLeaseIdentity identity, CancellationToken token)
    {
        LeaseOwnershipStatus status = await leases.AssertOwnershipAsync(new AssertWorkerLeaseRequest(identity,
            RepositoryTimeout), token).ConfigureAwait(false);
        if (status != LeaseOwnershipStatus.Current)
            throw new OperationCanceledException("Analytics derivation lease ownership was lost.", token);
    }

    private async ValueTask ReleaseAsync(WorkerLeaseIdentity identity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(identity, RepositoryTimeout), timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) { RecordFailure("lease_release", exception); }
    }

    private void RecordFailure(string phase, Exception exception, Guid? jobId = null)
        => RecordFailure(phase, RuntimeDiagnostics.FailureCategory(exception), jobId);

    private void RecordFailure(string phase, string category, Guid? jobId = null)
    {
        RuntimeDiagnostics.RecordAnalyticsFailure(phase, category);
        logger.LogWarning("Analytics derivation interrupted. Phase={Phase} FailureCategory={FailureCategory} JobId={JobId}", phase, category, jobId);
    }
}
