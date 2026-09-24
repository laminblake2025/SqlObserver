using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.Collector;

/// <summary>Reconciles the immutable collector catalog and continuously fills bounded dispatch slots.</summary>
public sealed partial class CollectionWorker : BackgroundService
{
    private static readonly WorkerLeaseKey CatalogLeaseKey = new("collector/catalog/reconcile");
    private static readonly WorkerLeaseDuration CatalogLeaseDuration = new(TimeSpan.FromSeconds(30));
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly TimeSpan ActiveCycleDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IdleCycleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly CollectorScheduler _scheduler;
    private readonly IWorkerLeasePort _leases;
    private readonly PostgreSqlPartitionMaintenancePort _partitionMaintenance;
    private readonly WorkerExecutionId _executionId;
    private readonly TimeProvider _timeProvider;
    private readonly M10PartitionMaintenanceCoordinator _m10Maintenance;
    private readonly ILogger<CollectionWorker> _logger;

    public CollectionWorker(
        CollectorScheduler scheduler,
        IWorkerLeasePort leases,
        PostgreSqlPartitionMaintenancePort partitionMaintenance,
        WorkerExecutionId executionId,
        TimeProvider timeProvider,
        ILogger<CollectionWorker> logger)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _partitionMaintenance = partitionMaintenance ?? throw new ArgumentNullException(nameof(partitionMaintenance));
        _executionId = executionId ?? throw new ArgumentNullException(nameof(executionId));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _m10Maintenance = new M10PartitionMaintenanceCoordinator(_timeProvider);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long lastReconcileTimestamp = 0;
        bool catalogReady = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!catalogReady ||
                    _timeProvider.GetElapsedTime(lastReconcileTimestamp) >= ReconcileInterval)
                {
                    catalogReady = await ReconcileCatalogSafelyAsync(stoppingToken).ConfigureAwait(false);
                    lastReconcileTimestamp = _timeProvider.GetTimestamp();
                }

                int dispatched = catalogReady
                    ? await DispatchSafelyAsync(stoppingToken).ConfigureAwait(false)
                    : 0;
                TimeSpan delay = dispatched > 0 || _scheduler.ActiveDispatchCount > 0
                    ? ActiveCycleDelay : IdleCycleDelay;
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        finally { await _scheduler.DrainAsync().ConfigureAwait(false); }
    }

    private async Task<bool> ReconcileCatalogSafelyAsync(CancellationToken cancellationToken)
    {
        WorkerLeaseIdentity? identity = null;
        try
        {
            LeaseAcquisitionResult acquisition = await _leases.AcquireAsync(
                    new AcquireWorkerLeaseRequest(
                        CatalogLeaseKey,
                        _executionId,
                        CatalogLeaseDuration,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (acquisition.Status != LeaseAcquisitionStatus.Acquired || acquisition.Lease is null)
            {
                return false;
            }

            identity = acquisition.Lease.Identity;
            M10MaintenanceAttempt m10 = await _m10Maintenance.TryRunAsync(
                    token => _partitionMaintenance.EnsureM10PartitionSetAsync(identity, RepositoryTimeout, token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (m10.State == M10MaintenanceAttemptState.Succeeded)
            {
                LogM10MaintenanceCompleted(_logger, m10.CheckedCount!.Value);
            }
            else if (m10.State == M10MaintenanceAttemptState.Failed)
            {
                LogM10MaintenanceFailure(_logger, m10.FailureType!);
            }

            await _partitionMaintenance.EnsureM9DailyPartitionsAsync(
                    identity,
                    RepositoryTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            CollectorCatalogReconcileResult result = await _scheduler.ReconcileCatalogAsync(
                    identity,
                    cancellationToken)
                .ConfigureAwait(false);
            LogCatalogReconciled(
                _logger,
                result.InsertedCount,
                result.UpdatedCount,
                result.UnchangedCount);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCatalogFailure(_logger, exception.GetType().Name);
            return false;
        }
        finally
        {
            if (identity is not null)
            {
                await ReleaseWithoutMaskingAsync(identity).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> DispatchSafelyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            int dispatched = await _scheduler.DispatchAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (dispatched > 0)
            {
                LogDispatchStarted(_logger, dispatched);
            }

            return dispatched;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCycleFailure(_logger, exception.GetType().Name);
            return 0;
        }
    }

    private async ValueTask ReleaseWithoutMaskingAsync(WorkerLeaseIdentity identity)
    {
        try
        {
            await _leases.ReleaseAsync(
                    new ReleaseWorkerLeaseRequest(identity, RepositoryTimeout),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogLeaseReleaseFailure(_logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Information,
        Message = "Collector catalog reconciled. Inserted={InsertedCount} Updated={UpdatedCount} Unchanged={UnchangedCount}")]
    private static partial void LogCatalogReconciled(
        ILogger logger,
        int insertedCount,
        int updatedCount,
        int unchangedCount);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Information,
        Message = "Collector dispatch started. Claimed={ClaimedCount}")]
    private static partial void LogDispatchStarted(ILogger logger, int claimedCount);

    [LoggerMessage(
        EventId = 3103,
        Level = LogLevel.Warning,
        Message = "Collector catalog reconciliation failed safely. FailureType={FailureType}")]
    private static partial void LogCatalogFailure(ILogger logger, string failureType);

    [LoggerMessage(
        EventId = 3104,
        Level = LogLevel.Warning,
        Message = "Collector due-work cycle failed safely. FailureType={FailureType}")]
    private static partial void LogCycleFailure(ILogger logger, string failureType);

    [LoggerMessage(
        EventId = 3105,
        Level = LogLevel.Warning,
        Message = "Collector catalog lease release failed safely. FailureType={FailureType}")]
    private static partial void LogLeaseReleaseFailure(ILogger logger, string failureType);

    [LoggerMessage(
        EventId = 3106,
        Level = LogLevel.Information,
        Message = "M10 partition maintenance checked/touched {CheckedCount} allowlisted partitions.")]
    private static partial void LogM10MaintenanceCompleted(ILogger logger, int checkedCount);

    [LoggerMessage(
        EventId = 3107,
        Level = LogLevel.Warning,
        Message = "M10 partition maintenance failed safely. FailureType={FailureType}")]
    private static partial void LogM10MaintenanceFailure(ILogger logger, string failureType);
}
