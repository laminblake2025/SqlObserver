using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

/// <summary>Claims the transactional delivery outbox under a fenced lease and performs bounded delivery.</summary>
public sealed class AlertDeliveryWorker(IAlertRepositoryPort repository, IAlertDestinationPort dispatcher, IWorkerLeasePort leases, WorkerExecutionId executionId, ILogger<AlertDeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey("alerts/delivery"), executionId, new WorkerLeaseDuration(TimeSpan.FromSeconds(30)), new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), stoppingToken).ConfigureAwait(false);
                if (acquired.Status == LeaseAcquisitionStatus.Acquired && acquired.Lease is not null)
                {
                    using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    Task? renewalTask = null;
                    try
                    {
                        LeaseRenewalResult renewal = await leases.RenewAsync(new RenewWorkerLeaseRequest(acquired.Lease.Identity, new WorkerLeaseDuration(TimeSpan.FromSeconds(30)), new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), stoppingToken).ConfigureAwait(false);
                        if (renewal.Status != LeaseRenewalStatus.Renewed) continue;
                        // A shutdown cancellation arriving after the database has
                        // committed claims is ambiguous.  Keep the claim call on
                        // its own bounded token so the repository can run its
                        // exact-fence recovery before this control lease is released.
                        using var claimTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        IReadOnlyList<AlertDeliveryWork> work = await repository.ClaimDueDeliveriesAsync(acquired.Lease.Identity, 100, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), claimTimeout.Token).ConfigureAwait(false);
                        using var gate = new SemaphoreSlim(4, 4);
                        renewalTask = RenewLeaseLoopAsync(acquired.Lease.Identity, renewalCancellation);
                        var tasks = work.Select(item => ProcessDeliveryAsync(item, acquired.Lease.Identity, renewalCancellation, gate)).ToArray();
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    finally
                    {
                        renewalCancellation.Cancel();
                        try { if (renewalTask is not null) await renewalTask.ConfigureAwait(false); }
                        catch (Exception ex) { DeliveryFailed(logger, ex); }
                        finally
                        {
                            await ReleaseWithoutMaskingAsync(acquired.Lease.Identity).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { DeliveryFailed(logger, ex); }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseWithoutMaskingAsync(WorkerLeaseIdentity identity)
    {
        using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await leases.ReleaseAsync(
                new ReleaseWorkerLeaseRequest(identity, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),
                releaseTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A release transport/timeout failure must not replace a claim,
            // adapter, recovery, or shutdown cancellation failure. It is still
            // surfaced through a dedicated warning when release is the only
            // failure in the cycle.
            DeliveryLeaseReleaseFailed(logger, exception);
        }
    }

    private async Task ProcessDeliveryAsync(AlertDeliveryWork item, WorkerLeaseIdentity lease, CancellationTokenSource cycleCancellation, SemaphoreSlim gate)
    {
        bool enteredGate = false;
        bool finalizedByRepository = false;
        bool lostFence = false;
        IAlertDeliveryDispatchPermit? dispatchPermit = null;
        CancellationTokenSource? itemCancellation = null;
        Task? itemRenewal = null;
        AlertDeliveryResult? result = null;
        Exception? originalFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            await gate.WaitAsync(cycleCancellation.Token).ConfigureAwait(false);
            enteredGate = true;
            using var operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            CancellationToken operationToken = operationTimeout.Token;
            if (await leases.AssertOwnershipAsync(new AssertWorkerLeaseRequest(lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), operationToken).ConfigureAwait(false) != LeaseOwnershipStatus.Current)
            {
                // Let the repository's stale-fence path clear only this exact
                // claim; an ownership probe alone cannot perform durable row
                // cleanup and must not touch a newer claimant.
                try { _ = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), operationToken).ConfigureAwait(false); }
                catch (Exception exception) { DeliveryFailed(logger, exception); }
                lostFence = true;
                cycleCancellation.Cancel();
                return;
            }

            AlertDeliveryLeaseOutcome outcome = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), operationToken).ConfigureAwait(false);
            if (outcome == AlertDeliveryLeaseOutcome.LostFence)
            {
                lostFence = true;
                cycleCancellation.Cancel();
                return;
            }
            if (outcome != AlertDeliveryLeaseOutcome.Active)
            {
                // The atomic repository outcome owns the durable defer/cancel.
                finalizedByRepository = true;
                return;
            }

            itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(cycleCancellation.Token);
            dispatchPermit = await repository.AcquireDeliveryDispatchPermitAsync(item, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), itemCancellation.Token).ConfigureAwait(false);
            itemRenewal = RenewDeliveryLoopAsync(item, lease, itemCancellation);

            // Recheck immediately before the adapter side effect.
            AlertDeliveryLeaseOutcome preSend = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), itemCancellation.Token).ConfigureAwait(false);
            if (preSend == AlertDeliveryLeaseOutcome.LostFence)
            {
                lostFence = true;
                cycleCancellation.Cancel();
                return;
            }
            if (preSend != AlertDeliveryLeaseOutcome.Active)
            {
                finalizedByRepository = true;
                return;
            }
            result = await dispatcher.DeliverAsync(item with { Lease = lease, WorkKey = lease.Key.Value }, itemCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            originalFailure = exception;
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            DeliveryFailed(logger, exception);
        }
        finally
        {
            if (itemCancellation is not null)
            {
                itemCancellation.Cancel();
            }
            if (itemRenewal is not null)
            {
                try { await itemRenewal.ConfigureAwait(false); }
                catch (Exception exception) when (originalFailure is null) { originalFailure = exception; DeliveryFailed(logger, exception); }
                catch (Exception exception) { DeliveryFailed(logger, exception); }
            }

            if (!finalizedByRepository && !lostFence)
            {
                using var cleanupToken = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    if (result is not null)
                    {
                        AlertDeliveryResult completed = result with { TargetId = item.TargetId };
                        if (await leases.AssertOwnershipAsync(new AssertWorkerLeaseRequest(lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), cleanupToken.Token).ConfigureAwait(false) != LeaseOwnershipStatus.Current)
                        {
                            try { _ = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cleanupToken.Token).ConfigureAwait(false); }
                            catch (Exception exception) { cleanupFailure = exception; DeliveryFailed(logger, exception); }
                            lostFence = true;
                            DeliveryFailed(logger, new InvalidOperationException("Delivery completion lost its worker fence."));
                        }
                        else
                        {
                            AlertDeliveryResult persisted = await repository.CompleteDeliveryAsync(completed, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cleanupToken.Token).ConfigureAwait(false);
                            if (persisted.Reason == "lease_lost")
                            {
                                try { _ = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cleanupToken.Token).ConfigureAwait(false); }
                                catch (Exception exception) { cleanupFailure = exception; DeliveryFailed(logger, exception); }
                                lostFence = true;
                                DeliveryFailed(logger, new InvalidOperationException("Delivery completion reported lease_lost."));
                            }
                        }
                    }
                    else
                    {
                        string reason = originalFailure is OperationCanceledException ? "adapter_cancelled" : "adapter_failed";
                        AlertDeliveryResult persisted = await repository.CompleteDeliveryAsync(new AlertDeliveryResult(item.DeliveryId, false, false, reason, DateTimeOffset.UtcNow, item.TargetId), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cleanupToken.Token).ConfigureAwait(false);
                        if (persisted.Reason == "lease_lost")
                        {
                            try { _ = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cleanupToken.Token).ConfigureAwait(false); }
                            catch (Exception exception) { cleanupFailure = exception; DeliveryFailed(logger, exception); }
                            lostFence = true;
                            DeliveryFailed(logger, new InvalidOperationException("Delivery retry finalization reported lease_lost."));
                        }
                    }
                }
                catch (PostgresException exception) when (exception.SqlState == "55000")
                {
                    try { _ = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cleanupToken.Token).ConfigureAwait(false); }
                    catch (Exception recoveryException) { DeliveryFailed(logger, recoveryException); }
                    cleanupFailure = exception;
                    DeliveryFailed(logger, exception);
                }
                catch (AlertRepositoryOperationException exception) when (exception.Code == "lease_lost")
                {
                    try { _ = await repository.RenewDeliveryStateAsync(item, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cleanupToken.Token).ConfigureAwait(false); }
                    catch (Exception recoveryException) { DeliveryFailed(logger, recoveryException); }
                    cleanupFailure = exception;
                    DeliveryFailed(logger, exception);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                    DeliveryFailed(logger, exception);
                }
            }

            if (dispatchPermit is not null)
            {
                try { await dispatchPermit.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailure ??= exception; DeliveryFailed(logger, exception); }
            }
            itemCancellation?.Dispose();
            if (enteredGate) gate.Release();
        }

        if (originalFailure is not null)
        {
            if (cleanupFailure is not null) throw new AggregateException(originalFailure, cleanupFailure);
            throw originalFailure;
        }
        if (cleanupFailure is not null) throw cleanupFailure;
    }

    private async Task RenewDeliveryLoopAsync(AlertDeliveryWork work, WorkerLeaseIdentity lease, CancellationTokenSource itemCancellation)
    {
        if (work.TargetId is null) return;
        CancellationToken token = itemCancellation.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                AlertDeliveryLeaseOutcome outcome = await repository.RenewDeliveryStateAsync(work, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), token).ConfigureAwait(false);
                if (outcome == AlertDeliveryLeaseOutcome.Active) continue;

                // The atomic renewal/readiness function has already persisted the
                // defer/cancel intent while holding this owner/key/fence.
                itemCancellation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { itemCancellation.Cancel(); DeliveryFailed(logger, ex); }
    }
    private static readonly Action<ILogger, Exception?> DeliveryFailed = LoggerMessage.Define(LogLevel.Warning, new EventId(8102, "AlertDeliveryFailed"), "Alert delivery cycle failed; bounded retry will continue.");
    private static readonly Action<ILogger, Exception?> DeliveryLeaseReleaseFailed = LoggerMessage.Define(LogLevel.Warning, new EventId(8103, "AlertDeliveryLeaseReleaseFailed"), "Alert delivery lease release failed; the lease will expire naturally.");

    private async Task RenewLeaseLoopAsync(WorkerLeaseIdentity identity, CancellationTokenSource cancellation)
    {
        CancellationToken cancellationToken = cancellation.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                LeaseRenewalResult result = await leases.RenewAsync(new RenewWorkerLeaseRequest(identity, new WorkerLeaseDuration(TimeSpan.FromSeconds(30)), new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), cancellationToken).ConfigureAwait(false);
                if (result.Status != LeaseRenewalStatus.Renewed) { cancellation.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { cancellation.Cancel(); DeliveryFailed(logger, ex); }
    }
}
