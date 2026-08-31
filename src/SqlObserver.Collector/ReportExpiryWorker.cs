#pragma warning disable CA1848
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Reporting;

namespace SqlObserver.Collector;

/// <summary>Fenced, bounded report materialization cleanup. Expiry is the only
/// collector-side mutation permitted for report data.</summary>
public sealed class ReportExpiryWorker(
    IReportExpiryRepository reports,
    IWorkerLeasePort leases,
    WorkerExecutionId executionId,
    ILogger<ReportExpiryWorker> logger) : BackgroundService
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey("reports/expiry"), executionId, new WorkerLeaseDuration(TimeSpan.FromMinutes(2)), Timeout), stoppingToken).ConfigureAwait(false);
                if (acquired.Status == LeaseAcquisitionStatus.Acquired && acquired.Lease is not null)
                {
                    try { await reports.ExpireAsync(100, acquired.Lease.Identity, stoppingToken).ConfigureAwait(false); }
                    finally { await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(acquired.Lease.Identity, Timeout), CancellationToken.None).ConfigureAwait(false); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Report expiry cycle failed; reports remain available until the next fenced cycle."); }
            try { await Task.Delay(Poll, stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
