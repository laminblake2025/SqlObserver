using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

/// <summary>Runs one guarded retention action per short fenced lease.</summary>
public sealed partial class RetentionMaintenanceWorker : BackgroundService
{
    private static readonly WorkerLeaseKey LeaseKey = new("retention/maintenance");
    private static readonly WorkerLeaseDuration LeaseDuration = new(TimeSpan.FromSeconds(30));
    private static readonly RepositoryCallTimeout LeaseTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly RepositoryCallTimeout StepTimeout = new(TimeSpan.FromSeconds(15));
    private static readonly RepositoryCallTimeout OrphanTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromMinutes(1);

    private readonly IRetentionMaintenancePort _maintenance;
    private readonly IWorkerLeasePort _leases;
    private readonly WorkerExecutionId _executionId;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionMaintenanceWorker> _logger;
    private const int FamilyCount = 5;
    private int _nextFamily;
    private bool _sweepBusy;
    private bool _sweepFailed;

    public RetentionMaintenanceWorker(
        IRetentionMaintenancePort maintenance,
        IWorkerLeasePort leases,
        WorkerExecutionId executionId,
        TimeProvider timeProvider,
        ILogger<RetentionMaintenanceWorker> logger)
    {
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
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
            bool attempted = false;
            try
            {
                LeaseAcquisitionResult acquired = await _leases.AcquireAsync(
                    new AcquireWorkerLeaseRequest(LeaseKey, _executionId,
                        LeaseDuration, LeaseTimeout), stoppingToken).ConfigureAwait(false);
                if (acquired.Status == LeaseAcquisitionStatus.Acquired && acquired.Lease is not null)
                {
                    identity = acquired.Lease.Identity;
                    int family = _nextFamily;
                    _nextFamily = (_nextFamily + 1) % FamilyCount;
                    attempted = true;
                    var outcome = await RunFamilyAsync(family, identity, stoppingToken).ConfigureAwait(false);
                    _sweepBusy |= outcome == FamilyOutcome.Busy;
                    _sweepFailed |= outcome == FamilyOutcome.Failed;
                    delay = NextDelay();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCycleFailed(_logger, exception.GetType().Name);
                if (attempted)
                {
                    _sweepFailed = true;
                    delay = NextDelay();
                }
                else
                {
                    delay = FailureDelay;
                }
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

    // Every family receives a short lease even when earlier families keep
    // reporting work or failure. Only a complete idle sweep sleeps 15 minutes.
    private TimeSpan NextDelay()
    {
        if (_nextFamily != 0) return BusyDelay;
        TimeSpan delay = _sweepBusy ? BusyDelay : _sweepFailed ? FailureDelay : IdleDelay;
        _sweepBusy = false;
        _sweepFailed = false;
        return delay;
    }

    private enum FamilyOutcome { Idle, Busy, Failed }

    private async ValueTask<FamilyOutcome> RunFamilyAsync(int family, WorkerLeaseIdentity identity, CancellationToken token)
    {
        switch (family)
        {
            case 0:
                var partition = await _maintenance.RunSystemRetentionStepAsync(identity, StepTimeout, token).ConfigureAwait(false);
                if (partition is SystemRetentionStepOutcome.Retry or SystemRetentionStepOutcome.Failed)
                {
                    LogActionFailed(_logger, partition);
                    return FamilyOutcome.Failed;
                }
                if (partition == SystemRetentionStepOutcome.Idle) return FamilyOutcome.Idle;
                LogCompleted(_logger, partition);
                return FamilyOutcome.Busy;
            case 1:
                var graph = await _maintenance.RunQueryRunRetentionStepAsync(identity, StepTimeout, token).ConfigureAwait(false);
                if (graph == QueryRunRetentionStepOutcome.Retired) LogQueryRunRetired(_logger);
                else if (graph == QueryRunRetentionStepOutcome.Oversize) LogQueryRunOversize(_logger);
                return graph == QueryRunRetentionStepOutcome.Idle ? FamilyOutcome.Idle : FamilyOutcome.Busy;
            case 2:
                var copy = await _maintenance.RunOriginalCopyRetentionStepAsync(identity, StepTimeout, token).ConfigureAwait(false);
                if (copy == OriginalCopyRetentionStepOutcome.Retired) LogOriginalCopiesRetired(_logger);
                else if (copy == OriginalCopyRetentionStepOutcome.Blocked) LogOriginalCopyBlocked(_logger);
                return copy == OriginalCopyRetentionStepOutcome.Idle ? FamilyOutcome.Idle : FamilyOutcome.Busy;
            case 3:
                int text = await _maintenance.PruneOrphanQueryTextPayloadsAsync(identity, OrphanTimeout, token).ConfigureAwait(false);
                if (text == 0) return FamilyOutcome.Idle;
                LogOrphansPruned(_logger, text);
                return FamilyOutcome.Busy;
            case 4:
                int plan = await _maintenance.PruneOrphanQueryPlanPayloadsAsync(identity, OrphanTimeout, token).ConfigureAwait(false);
                if (plan == 0) return FamilyOutcome.Idle;
                LogOrphansPruned(_logger, plan);
                return FamilyOutcome.Busy;
            default: throw new InvalidOperationException("Unknown retention work family.");
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

    [LoggerMessage(EventId = 3154, Level = LogLevel.Information,
        Message = "Fenced retention removed old unlinked query text/plan payloads. Count={Count}")]
    private static partial void LogOrphansPruned(ILogger logger, int count);

    [LoggerMessage(EventId = 3155, Level = LogLevel.Information,
        Message = "Fenced retention retired one expired M7 run graph.")]
    private static partial void LogQueryRunRetired(ILogger logger);

    [LoggerMessage(EventId = 3156, Level = LogLevel.Warning,
        Message = "M7 run graph exceeds the bounded retention step; inspect system.query_run_retention_scan_cursor.")]
    private static partial void LogQueryRunOversize(ILogger logger);

    [LoggerMessage(EventId = 3157, Level = LogLevel.Information,
        Message = "Fenced retention removed one bounded batch of sealed original collection-run copies.")]
    private static partial void LogOriginalCopiesRetired(ILogger logger);

    [LoggerMessage(EventId = 3158, Level = LogLevel.Warning,
        Message = "Original collection-run copy cleanup is blocked; inspect system.collection_run_original_copy_blocked.")]
    private static partial void LogOriginalCopyBlocked(ILogger logger);
}
