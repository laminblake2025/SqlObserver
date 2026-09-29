#pragma warning disable CA1848
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

/// <summary>Publishes bounded current Overview evidence from committed repository data.</summary>
public sealed class OverviewLatestStatusWorker(
    IOverviewLatestMaterializationPort materializer,
    IWorkerLeasePort leases,
    WorkerExecutionId executionId,
    ILogger<OverviewLatestStatusWorker> logger) : BackgroundService
{
    private static readonly WorkerLeaseKey WorkKey = new("overview/latest-status");
    private static readonly WorkerLeaseDuration LeaseDuration = new(TimeSpan.FromMinutes(2));
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LeaseAcquisitionResult acquired = await leases.AcquireAsync(
                    new AcquireWorkerLeaseRequest(WorkKey, executionId, LeaseDuration, Timeout),
                    stoppingToken).ConfigureAwait(false);
                if (acquired.Status == LeaseAcquisitionStatus.Acquired && acquired.Lease is not null)
                {
                    WorkerLeaseIdentity lease = acquired.Lease.Identity;
                    try
                    {
                        IReadOnlyList<OverviewRefreshTarget> due =
                            await materializer.ListDueAsync(lease, 16, stoppingToken).ConfigureAwait(false);
                        foreach (OverviewRefreshTarget target in due)
                        {
                            stoppingToken.ThrowIfCancellationRequested();
                            try
                            {
                                await materializer.RefreshCoreAsync(target, lease, stoppingToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                            {
                                logger.LogWarning(exception,
                                    "Overview latest-status refresh failed for target {TargetId}; remaining targets continue.",
                                    target.TargetId.Value);
                            }
                            LeaseRenewalResult renewed = await leases.RenewAsync(
                                new RenewWorkerLeaseRequest(lease, LeaseDuration, Timeout),
                                stoppingToken).ConfigureAwait(false);
                            if (renewed.Status != LeaseRenewalStatus.Renewed) break;
                        }
                    }
                    finally
                    {
                        try
                        {
                            await leases.ReleaseAsync(
                                new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Overview latest-status lease release failed.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Overview latest-status cycle failed; a later fenced cycle will retry.");
            }
            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
