using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.Collector;

/// <summary>Runs one guarded retention action per short fenced lease.</summary>
public sealed partial class RetentionMaintenanceWorker : BackgroundService
{
    private static readonly WorkerLeaseKey LeaseKey = new("retention/maintenance");
    private static readonly WorkerLeaseDuration LeaseDuration = new(TimeSpan.FromSeconds(30));
    private static readonly RepositoryCallTimeout LeaseTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly RepositoryCallTimeout StepTimeout = new(TimeSpan.FromSeconds(15));
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromMinutes(1);

    private readonly PostgreSqlPartitionMaintenancePort _partitions;
    private readonly IWorkerLeasePort _leases;
    private readonly WorkerExecutionId _executionId;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionMaintenanceWorker> _logger;

    public RetentionMaintenanceWorker(
        PostgreSqlPartitionMaintenancePort partitions,
        IWorkerLeasePort leases,
        WorkerExecutionId executionId,
        TimeProvider timeProvider,
        ILogger<RetentionMaintenanceWorker> logger)
    {
        _partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _executionId = executionId ?? throw new ArgumentNullException(nameof(executionId));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = IdleDelay;
            WorkerLeaseIdentity? identity = null;
            try
            {
                LeaseAcquisitionResult acquired = await _leases.AcquireAsync(
                    new AcquireWorkerLeaseRequest(LeaseKey, _executionId,
                        LeaseDuration, LeaseTimeout), stoppingToken).ConfigureAwait(false);
                if (acquired.Status == LeaseAcquisitionStatus.Acquired && acquired.Lease is not null)
                {
                    identity = acquired.Lease.Identity;
                    SystemRetentionStepOutcome outcome = await _partitions
                        .RunSystemRetentionStepAsync(identity, StepTimeout, stoppingToken)
                        .ConfigureAwait(false);
                    if (outcome is SystemRetentionStepOutcome.Detached or SystemRetentionStepOutcome.Dropped)
                    {
                        LogCompleted(_logger, outcome);
                        delay = BusyDelay;
                    }
                    else if (outcome is SystemRetentionStepOutcome.Retry or SystemRetentionStepOutcome.Failed)
                    {
                        LogActionFailed(_logger, outcome);
                        delay = FailureDelay;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCycleFailed(_logger, exception.GetType().Name);
                delay = FailureDelay;
            }
            finally
            {
                if (identity is not null)
                {
                    try
                    {
                        await _leases.ReleaseAsync(
                            new ReleaseWorkerLeaseRequest(identity, LeaseTimeout),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        LogLeaseReleaseFailed(_logger, exception.GetType().Name);
                    }
                }
            }
            try { await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(EventId = 3150, Level = LogLevel.Information,
        Message = "Fenced system retention completed one action. Outcome={Outcome}")]
    private static partial void LogCompleted(ILogger logger, SystemRetentionStepOutcome outcome);

    [LoggerMessage(EventId = 3151, Level = LogLevel.Warning,
        Message = "Fenced system retention recorded a failed action. Outcome={Outcome}")]
    private static partial void LogActionFailed(ILogger logger, SystemRetentionStepOutcome outcome);

    [LoggerMessage(EventId = 3153, Level = LogLevel.Warning,
        Message = "Fenced system retention cycle failed safely. FailureType={FailureType}")]
    private static partial void LogCycleFailed(ILogger logger, string failureType);

    [LoggerMessage(EventId = 3152, Level = LogLevel.Warning,
        Message = "Retention lease release failed safely. FailureType={FailureType}")]
    private static partial void LogLeaseReleaseFailed(ILogger logger, string failureType);
}
