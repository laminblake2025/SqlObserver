#pragma warning disable CA1848
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

/// <summary>Fenced repository-only analytics backfill worker.</summary>
public sealed class AnalyticsBackfillWorker(
    IAnalyticsBackfillStore store,
    IWorkerLeasePort leases,
    WorkerExecutionId executionId,
    IAnalyticsRepositoryPort analytics,
    ILogger<AnalyticsBackfillWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    // A single bounded invocation may run for 45 seconds. Keep the initial
    // lease longer than that and renew well before expiry while work runs.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey("analytics/backfill"), executionId, new WorkerLeaseDuration(LeaseDuration), RepositoryTimeout), stoppingToken).ConfigureAwait(false);
                if (acquired.Status == LeaseAcquisitionStatus.Acquired && acquired.Lease is not null)
                {
                    using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    Task renewal = RenewLeaseUntilCancelledAsync(acquired.Lease.Identity, cycleCancellation);
                    try
                    {
                        IReadOnlyList<AnalyticsBackfillJob> jobs = await store.ClaimAsync(acquired.Lease.Identity, AnalyticsJobBounds.MaximumConcurrency, cycleCancellation.Token).ConfigureAwait(false);
                        using var gate = new SemaphoreSlim(AnalyticsJobBounds.MaximumConcurrency, AnalyticsJobBounds.MaximumConcurrency);
                        await Task.WhenAll(jobs.Select(job => ProcessBoundedAsync(job, acquired.Lease.Identity, cycleCancellation, gate))).ConfigureAwait(false);
                    }
                    finally
                    {
                        cycleCancellation.Cancel();
                        try { await renewal.ConfigureAwait(false); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                        await ReleaseAsync(acquired.Lease.Identity).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Analytics backfill cycle failed; queued work remains fenced for retry."); _ = exception; }
            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessBoundedAsync(AnalyticsBackfillJob original, WorkerLeaseIdentity lease, CancellationTokenSource cycleCancellation, SemaphoreSlim gate)
    {
        await gate.WaitAsync(cycleCancellation.Token).ConfigureAwait(false);
        try
        {
            original.Validate();
            var stopwatch = Stopwatch.StartNew();
            AnalyticsBackfillJob current = original;
            DateTimeOffset day = current.CurrentDayUtc ?? new(current.FromUtc.UtcDateTime.Date, TimeSpan.Zero);
            while (day < current.ToUtc && stopwatch.Elapsed < AnalyticsJobBounds.MaximumDuration && !cycleCancellation.IsCancellationRequested)
            {
                (DateTimeOffset requestedDayStart, DateTimeOffset dayEnd) = AnalyticsBackfillWindows.ForDay(current, day);
                await AssertLeaseAsync(lease, cycleCancellation.Token).ConfigureAwait(false);
                await store.EnsureHistoricalPartitionAsync(current, lease, day, cycleCancellation.Token).ConfigureAwait(false);
                bool more = false;
                // A page boundary is not a bucket boundary. Keep the boundary
                // bucket in memory until the following page proves that all of
                // its rows have been read. The persisted cursor deliberately
                // lags the read cursor while this carry exists, so an
                // interruption replays a complete page rather than losing a
                // partial bucket.
                var carry = new List<MetricPoint>();
                string? persistedCursor = current.Cursor;
                string? readCursor = current.Cursor;
                // A range may begin inside a five-minute bucket.  That bucket
                // is intrinsically partial and must never be written as a
                // complete rollup; discard it once the page stream reaches
                // the next bucket (or the end of the range).
                DateTimeOffset leadingBucket = RollupV1.BucketStart(requestedDayStart, RollupInterval.FiveMinutes);
                bool skipLeadingPartialBucket = requestedDayStart > leadingBucket;
                do
                {
                    if (stopwatch.Elapsed >= AnalyticsJobBounds.MaximumDuration) break;
                    await AssertLeaseAsync(lease, cycleCancellation.Token).ConfigureAwait(false);
                    // Keep the exact first-day lower bound for every page;
                    // switching a continuation to midnight can re-read data
                    // outside the requested job window and break cursor semantics.
                    DateTimeOffset pageStart = requestedDayStart;
                    AnalyticsBackfillPage page = await store.ReadPageAsync(current with { Cursor = readCursor }, pageStart, dayEnd, AnalyticsJobBounds.MaximumRows, AnalyticsJobBounds.MaximumBytes, cycleCancellation.Token).ConfigureAwait(false);
                    page.Validate();
                    carry.AddRange(page.Points);
                    if (carry.Count > 0)
                    {
                        DateTimeOffset? boundaryBucket = page.HasMore && page.Points.Count > 0
                            ? RollupV1.BucketStart(page.Points[^1].ObservedAtUtc, RollupInterval.FiveMinutes)
                            : null;
                        // Once the page proves that the leading bucket is
                        // complete in the source stream, discard its rows
                        // explicitly. They are outside the requested range
                        // and must never survive in carry until day end.
                        if (skipLeadingPartialBucket && (boundaryBucket is null || boundaryBucket.Value > leadingBucket))
                            carry.RemoveAll(point => RollupV1.BucketStart(point.ObservedAtUtc, RollupInterval.FiveMinutes) == leadingBucket);
                        var flush = carry.Where(point =>
                        {
                            DateTimeOffset bucket = RollupV1.BucketStart(point.ObservedAtUtc, RollupInterval.FiveMinutes);
                            if (skipLeadingPartialBucket && bucket == leadingBucket) return false;
                            return boundaryBucket is not null ? bucket < boundaryBucket.Value : !page.HasMore && bucket.AddMinutes(5) <= dayEnd;
                        }).ToArray();
                        if (flush.Length > 0)
                        {
                            var flushKeys = flush.Select(point => (point.MetricKey, Dimensions: CanonicalDimensions.Sha256(point.Dimensions), Bucket: RollupV1.BucketStart(point.ObservedAtUtc, RollupInterval.FiveMinutes))).ToHashSet();
                            carry.RemoveAll(point => flushKeys.Contains((point.MetricKey, CanonicalDimensions.Sha256(point.Dimensions), RollupV1.BucketStart(point.ObservedAtUtc, RollupInterval.FiveMinutes))));
                        }
                        IReadOnlyList<RollupResult> rollups = flush.Length == 0 ? Array.Empty<RollupResult>() : RollupV1.ComputeMany(flush, RollupInterval.FiveMinutes, sourceCutoffUtc: dayEnd, truncated: false);
                        if (rollups.Count > AnalyticsJobBounds.MaximumRows) throw new InvalidDataException("Backfill result exceeds row bound.");
                        string resultJson = JsonSerializer.Serialize(new { rollups });
                        if (Encoding.UTF8.GetByteCount(resultJson) > AnalyticsJobBounds.MaximumBytes) throw new InvalidDataException("Backfill result exceeds byte bound.");
                        if (rollups.Count > 0)
                        {
                            AnalyticsBackfillJob readJob = current with { Cursor = readCursor };
                            AnalyticsReplayEnvelope commit = AnalyticsReplayContract.CreateForBackfill("backfill.rollup.commit", readJob, lease, pageStart, readCursor, JsonSerializer.Serialize(rollups), resultJson);
                            AnalyticsReplayEnvelope replay = AnalyticsReplayContract.CreateForBackfill("backfill.job.replay", readJob, lease, pageStart, readCursor, JsonSerializer.Serialize(rollups), resultJson);
                            // The repository write is part of the claimed
                            // backfill job.  Replay operation ids are audit
                            // identities only and must never become the job
                            // id used by the fixed rollup function.
                            await analytics.StoreRollupsAsync(new AnalyticsJobRequest(readJob.JobId, readJob.TargetId, lease.Key.Value, lease, RepositoryTimeout, readJob.TargetRevision), rollups, cycleCancellation.Token).ConfigureAwait(false);
                            await store.RecordReplayAsync(replay.OperationId, readJob, lease, replay.RequestDigest, replay.ResultDigest, resultJson, cycleCancellation.Token).ConfigureAwait(false);
                        }
                    }
                    more = page.HasMore;
                    readCursor = page.NextCursor;
                    // The page contract supplies a cursor only through the
                    // last fully flushable bucket. It is safe to persist even
                    // while carry retains the current boundary bucket, so a
                    // bounded invocation makes durable progress without
                    // risking a partially computed rollup on restart.
                    if (page.SafeCursor is not null) persistedCursor = page.SafeCursor;
                    else if (carry.Count == 0) persistedCursor = readCursor;
                    current = current with { Cursor = readCursor, CurrentDayUtc = day };
                    await AssertLeaseAsync(lease, cycleCancellation.Token).ConfigureAwait(false);
                    await store.SaveCursorAsync(current with { Cursor = persistedCursor }, lease, requestedDayStart, persistedCursor, cycleCancellation.Token).ConfigureAwait(false);
                }
                while (more && !cycleCancellation.IsCancellationRequested);
                if (more || cycleCancellation.IsCancellationRequested || stopwatch.Elapsed >= AnalyticsJobBounds.MaximumDuration) break;
                day = dayEnd;
                current = current with { Cursor = null, CurrentDayUtc = day };
                if (day < current.ToUtc)
                    await store.SaveCursorAsync(current, lease, day, null, cycleCancellation.Token).ConfigureAwait(false);
            }
            AnalyticsBackfillCompletion completion = cycleCancellation.IsCancellationRequested ? AnalyticsBackfillCompletion.Partial : day >= original.ToUtc ? AnalyticsBackfillCompletion.Succeeded : AnalyticsBackfillCompletion.Partial;
            await CompleteWithReplayAsync(current, lease, completion, cycleCancellation.IsCancellationRequested ? "cancelled" : null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cycleCancellation.IsCancellationRequested)
        {
            try { await CompleteWithReplayAsync(original, lease, AnalyticsBackfillCompletion.Partial, "cancelled").ConfigureAwait(false); }
            catch (Exception exception) { logger.LogWarning("Analytics backfill cancellation could not be recorded."); _ = exception; }
        }
        catch (Exception exception)
        {
            try { await CompleteWithReplayAsync(original, lease, AnalyticsBackfillCompletion.Partial, "bounded_backfill_failure").ConfigureAwait(false); }
            catch (Exception completionException) { logger.LogWarning("Analytics backfill failure could not be recorded."); _ = completionException; }
            logger.LogWarning("Analytics backfill job failed under its bounded contract."); _ = exception;
        }
        finally { gate.Release(); }
    }

    private async ValueTask ReleaseAsync(WorkerLeaseIdentity identity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(identity, RepositoryTimeout), timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) { logger.LogWarning("Analytics backfill lease release failed; expiry will recover ownership."); _ = exception; }
    }

    private async Task RenewLeaseUntilCancelledAsync(WorkerLeaseIdentity identity, CancellationTokenSource cycleCancellation)
    {
        try
        {
            while (!cycleCancellation.IsCancellationRequested)
            {
                await Task.Delay(LeaseRenewInterval, cycleCancellation.Token).ConfigureAwait(false);
                LeaseRenewalResult renewed = await leases.RenewAsync(new RenewWorkerLeaseRequest(identity, new WorkerLeaseDuration(LeaseDuration), RepositoryTimeout), cycleCancellation.Token).ConfigureAwait(false);
                if (renewed.Status == LeaseRenewalStatus.OwnershipLost) { cycleCancellation.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (cycleCancellation.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogWarning("Analytics backfill lease renewal failed; cancelling fenced work."); _ = exception; cycleCancellation.Cancel(); }
    }

    private async ValueTask AssertLeaseAsync(WorkerLeaseIdentity identity, CancellationToken cancellationToken)
    {
        LeaseOwnershipStatus status = await leases.AssertOwnershipAsync(new AssertWorkerLeaseRequest(identity, RepositoryTimeout), cancellationToken).ConfigureAwait(false);
        if (status != LeaseOwnershipStatus.Current) throw new OperationCanceledException("Analytics backfill lease ownership was lost.", cancellationToken);
    }

    private async ValueTask CompleteWithReplayAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, AnalyticsBackfillCompletion completion, string? detail)
    {
        string payload = JsonSerializer.Serialize(new { completion = completion.ToString(), detail = detail ?? string.Empty });
        AnalyticsReplayEnvelope replay = AnalyticsReplayContract.CreateForBackfill("backfill.job.completion", job, lease, job.FromUtc, job.Cursor, payload, payload);
        // Completion is deliberately independent of the cancelled work
        // token, but still has a finite repository budget.
        using var completionCancellation = new CancellationTokenSource(CompletionTimeout);
        await store.RecordReplayAsync(replay.OperationId, job, lease, replay.RequestDigest, replay.ResultDigest, payload, completionCancellation.Token).ConfigureAwait(false);
        await store.CompleteAsync(job, lease, completion, detail, completionCancellation.Token).ConfigureAwait(false);
    }
}
